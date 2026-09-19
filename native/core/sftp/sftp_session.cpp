#include "sftp_session.h"

#include "debug_log.h"
#include "io/SessionThread.h"
#include "ssh/error_codes.h"

#include <libssh2.h>
#include <libssh2_sftp.h>

#include <chrono>
#include <condition_variable>

#define SSH_LOG(...) sshclient::diagnostics::debugLog("sftp", __VA_ARGS__)

namespace sshclient {
namespace sftp {
namespace {

// EAGAIN wait granularity: shutdown/cancel/close stay responsive while a
// 50 ms socket starvation never spins (WSAPoll blocks the slice).
constexpr std::uint32_t kWaitSliceMs = 100;
// Caller-side grace beyond the loop-thread deadline: the loop task always
// ends by deadline+slice, this only covers a wedged loop thread. Expiry
// reports Timeout with unknown outcome (the loop task may still land later).
constexpr std::uint32_t kCallerGraceMs = 30000;

std::uint64_t steadyNowMs()
{
    return static_cast<std::uint64_t>(
        std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now().time_since_epoch())
            .count());
}

bool isEagain(_LIBSSH2_SESSION* raw)
{
    return ::libssh2_session_last_errno(raw) == LIBSSH2_ERROR_EAGAIN;
}

SftpAttrs attrsFromLibssh2(const LIBSSH2_SFTP_ATTRIBUTES& raw)
{
    SftpAttrs out;
    if ((raw.flags & LIBSSH2_SFTP_ATTR_SIZE) != 0) {
        out.hasSize = true;
        out.size = raw.filesize;
    }
    if ((raw.flags & LIBSSH2_SFTP_ATTR_PERMISSIONS) != 0) {
        out.hasPermissions = true;
        out.permissions = raw.permissions;
    }
    if ((raw.flags & LIBSSH2_SFTP_ATTR_UIDGID) != 0) {
        out.hasUidGid = true;
        out.uid = raw.uid;
        out.gid = raw.gid;
    }
    if ((raw.flags & LIBSSH2_SFTP_ATTR_ACMODTIME) != 0) {
        out.hasTimes = true;
        out.atime = raw.atime;
        out.mtime = raw.mtime;
    }
    return out;
}

} // namespace

// ---------------------------------------------------------------- pure logic

int sftpStatusToCode(unsigned long sftpStatus)
{
    switch (sftpStatus) {
    case LIBSSH2_FX_OK:
        return ssh::kSshErrorCodeNone;
    case LIBSSH2_FX_NO_SUCH_FILE:
    case LIBSSH2_FX_NO_SUCH_PATH:
    case LIBSSH2_FX_INVALID_HANDLE:
        return ssh::kSshErrorCodeSftpNoSuchFile;
    case LIBSSH2_FX_PERMISSION_DENIED:
    case LIBSSH2_FX_WRITE_PROTECT:
        return ssh::kSshErrorCodeSftpPermissionDenied;
    case LIBSSH2_FX_FILE_ALREADY_EXISTS:
    case LIBSSH2_FX_DIR_NOT_EMPTY:
        return ssh::kSshErrorCodeSftpAlreadyExists;
    case LIBSSH2_FX_EOF:
    case LIBSSH2_FX_FAILURE:
    case LIBSSH2_FX_BAD_MESSAGE:
    case LIBSSH2_FX_NO_CONNECTION:
    case LIBSSH2_FX_CONNECTION_LOST:
    case LIBSSH2_FX_OP_UNSUPPORTED:
    case LIBSSH2_FX_INVALID_FILENAME:
    case LIBSSH2_FX_LINK_LOOP:
    case LIBSSH2_FX_NO_SPACE_ON_FILESYSTEM:
    case LIBSSH2_FX_QUOTA_EXCEEDED:
    case LIBSSH2_FX_NO_MEDIA:
    case LIBSSH2_FX_UNKNOWN_PRINCIPAL:
    case LIBSSH2_FX_LOCK_CONFLICT:
    case LIBSSH2_FX_NOT_A_DIRECTORY:
        return ssh::kSshErrorCodeSftpTransferFailed;
    default:
        return ssh::kSshErrorCodeUnknown;
    }
}

SftpEntryType entryTypeFromPermissions(unsigned long permissions, bool hasPermissions)
{
    if (!hasPermissions) {
        return SftpEntryType::Unknown;
    }
    switch (permissions & LIBSSH2_SFTP_S_IFMT) {
    case LIBSSH2_SFTP_S_IFREG:
        return SftpEntryType::File;
    case LIBSSH2_SFTP_S_IFDIR:
        return SftpEntryType::Directory;
    case LIBSSH2_SFTP_S_IFLNK:
        return SftpEntryType::Symlink;
    case LIBSSH2_SFTP_S_IFIFO:
        return SftpEntryType::Fifo;
    case LIBSSH2_SFTP_S_IFSOCK:
        return SftpEntryType::Socket;
    case LIBSSH2_SFTP_S_IFCHR:
        return SftpEntryType::CharDevice;
    case LIBSSH2_SFTP_S_IFBLK:
        return SftpEntryType::BlockDevice;
    default:
        return SftpEntryType::Unknown;
    }
}

std::string joinRemotePath(const std::string& dir, const std::string& name)
{
    if (dir.empty() || dir.back() == '/') {
        return dir + name;
    }
    return dir + "/" + name;
}

const char* toString(SftpEntryType type)
{
    switch (type) {
    case SftpEntryType::Unknown:
        return "unknown";
    case SftpEntryType::File:
        return "file";
    case SftpEntryType::Directory:
        return "directory";
    case SftpEntryType::Symlink:
        return "symlink";
    case SftpEntryType::Fifo:
        return "fifo";
    case SftpEntryType::Socket:
        return "socket";
    case SftpEntryType::CharDevice:
        return "char_device";
    case SftpEntryType::BlockDevice:
        return "block_device";
    }
    return "unknown";
}

unsigned long SftpSession::toOpenFlags(std::initializer_list<OpenFlags> flags)
{
    unsigned long out = 0;
    for (OpenFlags flag : flags) {
        out |= static_cast<unsigned long>(flag);
    }
    return out;
}

// ---------------------------------------------------------------- lifecycle

SftpSession::SftpSession(ssh::SshSession& session) : session_(session)
{
}

SftpSession::~SftpSession()
{
    if (sftp_ != nullptr) {
        SSH_LOG("warning: SftpSession destroyed with a live subsystem (contract violation, leak)");
    }
    if (!files_.empty()) {
        SSH_LOG("warning: SftpSession destroyed with %u tracked files (contract violation, leak)",
                static_cast<unsigned>(files_.size()));
    }
}

SftpResult SftpSession::open(SftpCallOptions options)
{
    std::lock_guard<std::mutex> lock(openMutex_);
    if (isOpen()) {
        return SftpResult::success();
    }
    bool expected = false;
    if (!openAdmitted_.compare_exchange_strong(expected, true)) {
        // A previous open is in flight or failed open state; report by flag.
        return isOpen() ? SftpResult::success()
                        : SftpResult{false, ssh::kSshErrorCodeSftpInitFailed,
                                     "sftp open already attempted and not established"};
    }
    SftpResult result = runBlocking("sftp_init", options, [this, options] {
        const std::uint64_t deadline = steadyNowMs() + options.timeoutMs;
        LIBSSH2_SFTP* sftp = nullptr;
        SftpResult abort;
        for (;;) {
            sftp = ::libssh2_sftp_init(session_.session_);
            if (sftp != nullptr) {
                break;
            }
            if (!isEagain(session_.session_)) {
                return failureFromCurrentError("libssh2_sftp_init");
            }
            if (!waitSlice(options, deadline, abort)) {
                return abort;
            }
        }
        sftp_ = sftp;
        open_.store(true, std::memory_order_release);
        return SftpResult::success();
    });
    if (!result.ok) {
        // Allow a later retry after a failed open (admission slot stays taken
        // only while usable).
        openAdmitted_.store(false, std::memory_order_release);
    }
    return result;
}

void SftpSession::close()
{
    std::lock_guard<std::mutex> lock(openMutex_);
    if (!openAdmitted_.load(std::memory_order_acquire) && !isOpen()) {
        return; // never opened: nothing to close, no task posted
    }
    if (!session_.thread_.isRunning()) {
        // Loop thread gone (owner tore down the session first): handles die
        // with it; drop without touching libssh2.
        SSH_LOG("warning: sftp close with a dead IO thread, dropping handles");
        for (File* file : files_) {
            file->handle_ = nullptr;
            file->orphaned_.store(true, std::memory_order_release);
            file->closed_.store(true, std::memory_order_release);
        }
        files_.clear();
        sftp_ = nullptr;
        open_.store(false, std::memory_order_release);
        openAdmitted_.store(false, std::memory_order_release);
        return;
    }
    // Best effort: even a failed/transport-dead open lands here to release the
    // admission flag. runBlockingAlways skips the Established gate (the loop
    // body tolerates a torn-down session and drops without libssh2 calls).
    runBlockingAlways("sftp_shutdown", SftpCallOptions{}, [this] {
        if (session_.session_ == nullptr) {
            // releaseResources already freed the session (and with it the
            // sftp channel and all file handles): drop, never touch libssh2.
            for (File* file : files_) {
                file->handle_ = nullptr;
                file->orphaned_.store(true, std::memory_order_release);
                file->closed_.store(true, std::memory_order_release);
            }
            files_.clear();
            sftp_ = nullptr;
            open_.store(false, std::memory_order_release);
            openAdmitted_.store(false, std::memory_order_release);
            return SftpResult::success();
        }
        for (File* file : files_) {
            if (file->handle_ != nullptr) {
                ::libssh2_sftp_close_handle(file->handle_);
                file->handle_ = nullptr;
            }
            file->orphaned_.store(true, std::memory_order_release);
            file->closed_.store(true, std::memory_order_release);
        }
        files_.clear();
        if (sftp_ != nullptr) {
            // Shutdown may EAGAIN; bounded retries, then drop (the session
            // free covers the rest, same convention as channel onSessionLost).
            for (int attempt = 0; attempt < 50; ++attempt) {
                const int rc = ::libssh2_sftp_shutdown(sftp_);
                if (rc == 0) {
                    break;
                }
                if (::libssh2_session_last_errno(session_.session_) !=
                    LIBSSH2_ERROR_EAGAIN) {
                    break;
                }
                SftpResult abort;
                SftpCallOptions slice;
                slice.timeoutMs = kWaitSliceMs;
                if (!waitSlice(slice, steadyNowMs() + kWaitSliceMs, abort)) {
                    break;
                }
            }
            sftp_ = nullptr;
        }
        open_.store(false, std::memory_order_release);
        openAdmitted_.store(false, std::memory_order_release);
        return SftpResult::success();
    },
                     false);
    session_.updateSocketInterest();
}

// ---------------------------------------------------------------- blocking core (caller thread + loop thread)

SftpResult SftpSession::runBlocking(const char* what, const SftpCallOptions& options,
                                    LoopWork work)
{
    if (session_.state() != ssh::SshSessionState::Established) {
        return SftpResult{false, ssh::kSshErrorCodeSftpTransferFailed,
                          std::string(what) +
                              " rejected: session not established (state=" +
                              ssh::toString(session_.state()) + ")"};
    }
    return runBlockingAlways(what, options, std::move(work), true);
}

SftpResult SftpSession::runBlockingAlways(const char* what, const SftpCallOptions& options,
                                          LoopWork work, bool requireEstablished)
{
    if (!session_.thread_.isRunning()) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError,
                          std::string(what) + " rejected: session IO thread not running"};
    }
    struct CallState {
        std::mutex mutex;
        std::condition_variable cv;
        bool done = false;
        SftpResult result{false, ssh::kSshErrorCodeInternalError, "never ran"};
    };
    auto call = std::make_shared<CallState>();
    session_.thread_.post([this, call, work = std::move(work), requireEstablished] {
        SftpResult result;
        try {
            // Re-check on the loop thread: the session may have raced away
            // after admission (same convention as SshChannel::begin). The
            // close() path skips the gate and tolerates teardown itself.
            const bool alive = session_.session_ != nullptr &&
                               session_.socket_ != INVALID_SOCKET &&
                               (!requireEstablished ||
                                session_.state() == ssh::SshSessionState::Established);
            if (!alive) {
                if (requireEstablished) {
                    result = SftpResult{false, ssh::kSshErrorCodeSftpTransferFailed,
                                        "session left the established state"};
                } else {
                    result = work();
                }
            } else {
                result = work();
            }
        } catch (const std::exception& e) {
            result = SftpResult{false, ssh::kSshErrorCodeInternalError, e.what()};
        } catch (...) {
            result = SftpResult{false, ssh::kSshErrorCodeInternalError,
                                "sftp loop task threw"};
        }
        session_.updateSocketInterest();
        {
            std::lock_guard<std::mutex> lock(call->mutex);
            call->result = std::move(result);
            call->done = true;
        }
        call->cv.notify_one();
    });
    std::unique_lock<std::mutex> lock(call->mutex);
    const std::uint64_t waitMs =
        static_cast<std::uint64_t>(options.timeoutMs) + kCallerGraceMs;
    if (call->cv.wait_for(lock, std::chrono::milliseconds(waitMs), [&] { return call->done; })) {
        return call->result;
    }
    SSH_LOG("%s: caller-side watchdog expired (loop task still in flight, outcome unknown)", what);
    return SftpResult{false, ssh::kSshErrorCodeSessionTimeout,
                      std::string(what) + ": timed out waiting for the IO thread"};
}

// Loop thread: wait until the session socket is ready for libssh2's declared
// direction, a 100 ms slice at a time. Returns false with abortOut set on
// cancel / deadline / transport error (caller must stop and return abortOut).
bool SftpSession::waitSlice(const SftpCallOptions& options, std::uint64_t deadlineMs,
                            SftpResult& abortOut)
{
    for (;;) {
        if (options.cancel != nullptr && options.cancel->load(std::memory_order_acquire)) {
            abortOut = SftpResult{false, ssh::kSshErrorCodeSftpCancelled, "cancelled"};
            return false;
        }
        if (steadyNowMs() >= deadlineMs) {
            abortOut = SftpResult{false, ssh::kSshErrorCodeSessionTimeout, "timed out"};
            return false;
        }
        const SOCKET sock = session_.socket_;
        if (sock == INVALID_SOCKET || session_.session_ == nullptr) {
            // The session was torn down under us (releaseResources runs on
            // this same thread, so this only fires when observed between
            // tasks — report and stop touching libssh2).
            sftp_ = nullptr;
            open_.store(false, std::memory_order_release);
            abortOut = SftpResult{false, ssh::kSshErrorCodeSocketError,
                                  "session socket gone mid-operation"};
            return false;
        }
        const int directions = ::libssh2_session_block_directions(session_.session_);
        short events = 0;
        if ((directions & LIBSSH2_SESSION_BLOCK_INBOUND) != 0) {
            events |= POLLRDNORM;
        }
        if ((directions & LIBSSH2_SESSION_BLOCK_OUTBOUND) != 0) {
            events |= POLLWRNORM;
        }
        if (events == 0) {
            return true; // not blocked: retry the call immediately
        }
        WSAPOLLFD fd{};
        fd.fd = sock;
        fd.events = events;
        const std::uint64_t now = steadyNowMs();
        int slice = static_cast<int>(deadlineMs > now ? deadlineMs - now : 0);
        if (slice > static_cast<int>(kWaitSliceMs)) {
            slice = static_cast<int>(kWaitSliceMs);
        }
        const int ready = ::WSAPoll(&fd, 1, slice);
        if (ready == SOCKET_ERROR) {
            const int error = ::WSAGetLastError();
            abortOut = SftpResult{false, ssh::kSshErrorCodeSocketError,
                                  "WSAPoll failed: " + std::to_string(error)};
            return false;
        }
        if (ready > 0) {
            if ((fd.revents & (POLLHUP)) != 0) {
                abortOut = SftpResult{false, ssh::kSshErrorCodeRemoteClosed,
                                      "peer closed the connection mid-operation"};
                return false;
            }
            if ((fd.revents & (POLLERR | POLLNVAL)) != 0) {
                abortOut = SftpResult{false, ssh::kSshErrorCodeSocketError,
                                      "socket error mid-operation"};
                return false;
            }
            return true;
        }
        // Slice idle-timeout: loop around to re-check cancel/deadline.
    }
}

std::string SftpSession::lastLibssh2Error()
{
    char* message = nullptr;
    int messageLength = 0;
    ::libssh2_session_last_error(session_.session_, &message, &messageLength, 0);
    if (message == nullptr || messageLength <= 0) {
        return "unknown error";
    }
    return std::string(message, static_cast<std::size_t>(messageLength));
}

// Loop thread: classify the current failure from the session errno first
// (transport class reuses 401/402/403), then the SFTP FX status.
SftpResult SftpSession::failureFromCurrentError(const char* what)
{
    const int sessionErrno = ::libssh2_session_last_errno(session_.session_);
    if (sessionErrno == LIBSSH2_ERROR_SOCKET_TIMEOUT) {
        return SftpResult{false, ssh::kSshErrorCodeSessionTimeout,
                          std::string(what) + ": socket timeout"};
    }
    if (sessionErrno == LIBSSH2_ERROR_SOCKET_DISCONNECT) {
        return SftpResult{false, ssh::kSshErrorCodeRemoteClosed,
                          std::string(what) + ": " + lastLibssh2Error()};
    }
    if (sessionErrno == LIBSSH2_ERROR_SOCKET_SEND || sessionErrno == LIBSSH2_ERROR_SOCKET_RECV) {
        return SftpResult{false, ssh::kSshErrorCodeSocketError,
                          std::string(what) + ": " + lastLibssh2Error()};
    }
    unsigned long fx = LIBSSH2_FX_OK;
    if (sftp_ != nullptr) {
        fx = ::libssh2_sftp_last_error(sftp_);
    }
    const int code = sftpStatusToCode(fx);
    return SftpResult{false, code, std::string(what) + ": " + lastLibssh2Error()};
}

void SftpSession::trackFile(File* file)
{
    files_.insert(file);
}

void SftpSession::untrackFile(File* file)
{
    files_.erase(file);
}

// ---------------------------------------------------------------- metadata ops

SftpResult SftpSession::listDir(const std::string& path, std::vector<SftpEntry>& entriesOut,
                                SftpCallOptions options)
{
    if (!isOpen()) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError,
                          "listDir rejected: sftp session not open"};
    }
    if (path.empty()) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError,
                          "listDir rejected: empty path"};
    }
    return runBlocking("listDir", options, [this, &path, &entriesOut, options] {
        const std::uint64_t deadline = steadyNowMs() + options.timeoutMs;
        LIBSSH2_SFTP_HANDLE* dir = nullptr;
        SftpResult abort;
        for (;;) {
            dir = ::libssh2_sftp_opendir(sftp_, path.c_str());
            if (dir != nullptr) {
                break;
            }
            if (!isEagain(session_.session_)) {
                return failureFromCurrentError("opendir");
            }
            if (!waitSlice(options, deadline, abort)) {
                return abort;
            }
        }
        std::vector<SftpEntry> entries;
        char name[512]{};
        LIBSSH2_SFTP_ATTRIBUTES raw{};
        for (;;) {
            const int rc =
                ::libssh2_sftp_readdir_ex(dir, name, sizeof(name), nullptr, 0, &raw);
            if (rc == 0) {
                break; // end of directory
            }
            if (rc < 0) {
                if (isEagain(session_.session_)) {
                    if (!waitSlice(options, deadline, abort)) {
                        ::libssh2_sftp_close_handle(dir);
                        return abort;
                    }
                    continue;
                }
                SftpResult failure = failureFromCurrentError("readdir");
                ::libssh2_sftp_close_handle(dir);
                return failure;
            }
            SftpEntry entry;
            entry.name.assign(name, static_cast<std::size_t>(rc));
            if (entry.name == "." || entry.name == "..") {
                continue;
            }
            entry.attrs = attrsFromLibssh2(raw);
            entry.type = entryTypeFromPermissions(entry.attrs.permissions,
                                                 entry.attrs.hasPermissions);
            entries.push_back(std::move(entry));
        }
        // Best-effort close of the dir handle (EAGAIN retried within the call).
        for (int attempt = 0; attempt < 50; ++attempt) {
            const int rc = ::libssh2_sftp_close_handle(dir);
            if (rc == 0) {
                break;
            }
            if (!isEagain(session_.session_)) {
                break;
            }
            if (!waitSlice(options, deadline, abort)) {
                return abort;
            }
        }
        // Resolve symlink targets (needs the parent path; failures leave the
        // target empty rather than failing the whole listing).
        for (SftpEntry& entry : entries) {
            if (entry.type != SftpEntryType::Symlink) {
                continue;
            }
            const std::string full = joinRemotePath(path, entry.name);
            char target[1024]{};
            for (;;) {
                const int rc = ::libssh2_sftp_readlink(sftp_, full.c_str(), target,
                                                       sizeof(target));
                if (rc >= 0) {
                    entry.linkTarget.assign(target, static_cast<std::size_t>(rc));
                    break;
                }
                if (!isEagain(session_.session_)) {
                    break; // leave empty, keep the entry
                }
                if (!waitSlice(options, deadline, abort)) {
                    return abort;
                }
            }
        }
        entriesOut = std::move(entries);
        return SftpResult::success();
    });
}

SftpResult SftpSession::stat(const std::string& path, bool followSymlink, SftpAttrs& attrsOut,
                             SftpCallOptions options)
{
    if (!isOpen()) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError,
                          "stat rejected: sftp session not open"};
    }
    if (path.empty()) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError, "stat rejected: empty path"};
    }
    return runBlocking("stat", options, [this, &path, followSymlink, &attrsOut, options] {
        const std::uint64_t deadline = steadyNowMs() + options.timeoutMs;
        LIBSSH2_SFTP_ATTRIBUTES raw{};
        SftpResult abort;
        for (;;) {
            const int rc = ::libssh2_sftp_stat_ex(
                sftp_, path.c_str(), static_cast<unsigned int>(path.size()),
                followSymlink ? LIBSSH2_SFTP_STAT : LIBSSH2_SFTP_LSTAT, &raw);
            if (rc == 0) {
                attrsOut = attrsFromLibssh2(raw);
                return SftpResult::success();
            }
            if (!isEagain(session_.session_)) {
                return failureFromCurrentError("stat");
            }
            if (!waitSlice(options, deadline, abort)) {
                return abort;
            }
        }
    });
}

SftpResult SftpSession::readLink(const std::string& path, std::string& targetOut,
                                 SftpCallOptions options)
{
    if (!isOpen()) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError,
                          "readLink rejected: sftp session not open"};
    }
    if (path.empty()) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError,
                          "readLink rejected: empty path"};
    }
    return runBlocking("readlink", options, [this, &path, &targetOut, options] {
        const std::uint64_t deadline = steadyNowMs() + options.timeoutMs;
        char target[4096]{};
        SftpResult abort;
        for (;;) {
            const int rc =
                ::libssh2_sftp_readlink(sftp_, path.c_str(), target, sizeof(target));
            if (rc >= 0) {
                targetOut.assign(target, static_cast<std::size_t>(rc));
                return SftpResult::success();
            }
            if (!isEagain(session_.session_)) {
                return failureFromCurrentError("readlink");
            }
            if (!waitSlice(options, deadline, abort)) {
                return abort;
            }
        }
    });
}

SftpResult SftpSession::makeDir(const std::string& path, std::uint32_t mode,
                                SftpCallOptions options)
{
    if (!isOpen()) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError,
                          "makeDir rejected: sftp session not open"};
    }
    if (path.empty()) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError,
                          "makeDir rejected: empty path"};
    }
    return runBlocking("mkdir", options, [this, &path, mode, options] {
        const std::uint64_t deadline = steadyNowMs() + options.timeoutMs;
        SftpResult abort;
        for (;;) {
            const int rc = ::libssh2_sftp_mkdir_ex(sftp_, path.c_str(),
                                                   static_cast<unsigned int>(path.size()),
                                                   static_cast<long>(mode));
            if (rc == 0) {
                return SftpResult::success();
            }
            if (!isEagain(session_.session_)) {
                return failureFromCurrentError("mkdir");
            }
            if (!waitSlice(options, deadline, abort)) {
                return abort;
            }
        }
    });
}

SftpResult SftpSession::rename(const std::string& oldPath, const std::string& newPath,
                               SftpCallOptions options)
{
    if (!isOpen()) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError,
                          "rename rejected: sftp session not open"};
    }
    if (oldPath.empty() || newPath.empty()) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError,
                          "rename rejected: empty path"};
    }
    return runBlocking("rename", options, [this, &oldPath, &newPath, options] {
        const std::uint64_t deadline = steadyNowMs() + options.timeoutMs;
        SftpResult abort;
        for (;;) {
            const int rc = ::libssh2_sftp_rename(sftp_, oldPath.c_str(), newPath.c_str());
            if (rc == 0) {
                return SftpResult::success();
            }
            if (!isEagain(session_.session_)) {
                return failureFromCurrentError("rename");
            }
            if (!waitSlice(options, deadline, abort)) {
                return abort;
            }
        }
    });
}

SftpResult SftpSession::removeFile(const std::string& path, SftpCallOptions options)
{
    if (!isOpen()) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError,
                          "removeFile rejected: sftp session not open"};
    }
    if (path.empty()) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError,
                          "removeFile rejected: empty path"};
    }
    return runBlocking("unlink", options, [this, &path, options] {
        const std::uint64_t deadline = steadyNowMs() + options.timeoutMs;
        SftpResult abort;
        for (;;) {
            const int rc = ::libssh2_sftp_unlink_ex(sftp_, path.c_str(),
                                                    static_cast<unsigned int>(path.size()));
            if (rc == 0) {
                return SftpResult::success();
            }
            if (!isEagain(session_.session_)) {
                return failureFromCurrentError("unlink");
            }
            if (!waitSlice(options, deadline, abort)) {
                return abort;
            }
        }
    });
}

SftpResult SftpSession::removeDir(const std::string& path, SftpCallOptions options)
{
    if (!isOpen()) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError,
                          "removeDir rejected: sftp session not open"};
    }
    if (path.empty()) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError,
                          "removeDir rejected: empty path"};
    }
    return runBlocking("rmdir", options, [this, &path, options] {
        const std::uint64_t deadline = steadyNowMs() + options.timeoutMs;
        SftpResult abort;
        for (;;) {
            const int rc = ::libssh2_sftp_rmdir_ex(sftp_, path.c_str(),
                                                   static_cast<unsigned int>(path.size()));
            if (rc == 0) {
                return SftpResult::success();
            }
            if (!isEagain(session_.session_)) {
                return failureFromCurrentError("rmdir");
            }
            if (!waitSlice(options, deadline, abort)) {
                return abort;
            }
        }
    });
}

SftpResult SftpSession::setStat(const std::string& path, const SftpAttrs& attrs,
                                SftpCallOptions options)
{
    if (!isOpen()) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError,
                          "setStat rejected: sftp session not open"};
    }
    if (path.empty()) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError,
                          "setStat rejected: empty path"};
    }
    if (!attrs.hasPermissions && !attrs.hasTimes && !attrs.hasUidGid && !attrs.hasSize) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError,
                          "setStat rejected: no flagged field to apply"};
    }
    return runBlocking("setstat", options, [this, &path, &attrs, options] {
        const std::uint64_t deadline = steadyNowMs() + options.timeoutMs;
        LIBSSH2_SFTP_ATTRIBUTES raw{};
        if (attrs.hasSize) {
            raw.flags |= LIBSSH2_SFTP_ATTR_SIZE;
            raw.filesize = attrs.size;
        }
        if (attrs.hasUidGid) {
            raw.flags |= LIBSSH2_SFTP_ATTR_UIDGID;
            raw.uid = attrs.uid;
            raw.gid = attrs.gid;
        }
        if (attrs.hasPermissions) {
            raw.flags |= LIBSSH2_SFTP_ATTR_PERMISSIONS;
            // Only the low 12 permission bits travel; the S_IFMT type bits
            // are read-only server-side (chmod mask).
            raw.permissions = attrs.permissions & 07777u;
        }
        if (attrs.hasTimes) {
            raw.flags |= LIBSSH2_SFTP_ATTR_ACMODTIME;
            raw.atime = attrs.atime;
            raw.mtime = attrs.mtime;
        }
        SftpResult abort;
        for (;;) {
            const int rc = ::libssh2_sftp_stat_ex(
                sftp_, path.c_str(), static_cast<unsigned int>(path.size()),
                LIBSSH2_SFTP_SETSTAT, &raw);
            if (rc == 0) {
                return SftpResult::success();
            }
            if (!isEagain(session_.session_)) {
                return failureFromCurrentError("setstat");
            }
            if (!waitSlice(options, deadline, abort)) {
                return abort;
            }
        }
    });
}

// ---------------------------------------------------------------- file IO

SftpSession::File::File(SftpSession& owner, struct _LIBSSH2_SFTP_HANDLE* handle)
    : owner_(owner), handle_(handle)
{
}

SftpSession::File::~File()
{
    if (handle_ != nullptr && !orphaned_.load(std::memory_order_acquire)) {
        SSH_LOG("warning: SftpSession::File destroyed with a live handle (contract violation, "
                "leak)");
    }
}

SftpResult SftpSession::File::readSome(char* buffer, std::size_t maxLen,
                                       std::size_t& bytesReadOut, SftpCallOptions options)
{
    bytesReadOut = 0;
    if (buffer == nullptr || maxLen == 0) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError,
                          "read rejected: null buffer or zero length"};
    }
    if (isClosed() || orphaned_.load(std::memory_order_acquire)) {
        return SftpResult{false, ssh::kSshErrorCodeSftpTransferFailed,
                          "read rejected: file closed"};
    }
    return owner_.runBlocking("sftp_read", options,
                              [this, buffer, maxLen, &bytesReadOut, options] {
                                  if (handle_ == nullptr) {
                                      return SftpResult{
                                          false, ssh::kSshErrorCodeSftpTransferFailed,
                                          "read rejected: file closed"};
                                  }
                                  const std::uint64_t deadline =
                                      steadyNowMs() + options.timeoutMs;
                                  SftpResult abort;
                                  for (;;) {
                                      const ssize_t n = ::libssh2_sftp_read(
                                          handle_, buffer, maxLen);
                                      if (n > 0) {
                                          bytesReadOut =
                                              static_cast<std::size_t>(n);
                                          return SftpResult::success();
                                      }
                                      if (n == 0) {
                                          return SftpResult::success(); // EOF
                                      }
                                      if (!isEagain(owner_.session_.session_)) {
                                          return owner_.failureFromCurrentError(
                                              "sftp_read");
                                      }
                                      if (!owner_.waitSlice(options, deadline, abort)) {
                                          return abort;
                                      }
                                  }
                              });
}

SftpResult SftpSession::File::writeAll(const char* data, std::size_t len,
                                       SftpCallOptions options)
{
    if (data == nullptr && len != 0) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError,
                          "write rejected: null buffer"};
    }
    if (isClosed() || orphaned_.load(std::memory_order_acquire)) {
        return SftpResult{false, ssh::kSshErrorCodeSftpTransferFailed,
                          "write rejected: file closed"};
    }
    if (len == 0) {
        return SftpResult::success();
    }
    return owner_.runBlocking("sftp_write", options, [this, data, len, options] {
        if (handle_ == nullptr) {
            return SftpResult{false, ssh::kSshErrorCodeSftpTransferFailed,
                              "write rejected: file closed"};
        }
        const std::uint64_t deadline = steadyNowMs() + options.timeoutMs;
        std::size_t sent = 0;
        SftpResult abort;
        while (sent < len) {
            const ssize_t n = ::libssh2_sftp_write(handle_, data + sent, len - sent);
            if (n > 0) {
                sent += static_cast<std::size_t>(n);
                continue;
            }
            // n==0 (nothing acked yet) and EAGAIN both mean wait + retry;
            // any other negative is a hard error.
            if (n != 0 && !isEagain(owner_.session_.session_)) {
                return owner_.failureFromCurrentError("sftp_write");
            }
            if (!owner_.waitSlice(options, deadline, abort)) {
                return abort;
            }
        }
        return SftpResult::success();
    });
}

SftpResult SftpSession::File::seek(std::uint64_t offset, SftpCallOptions options)
{
    if (isClosed() || orphaned_.load(std::memory_order_acquire)) {
        return SftpResult{false, ssh::kSshErrorCodeSftpTransferFailed,
                          "seek rejected: file closed"};
    }
    // seek64 is local-only (no packets), still routed through the loop thread
    // so handle lifetime stays single-threaded.
    return owner_.runBlocking("sftp_seek", options, [this, offset] {
        if (handle_ == nullptr) {
            return SftpResult{false, ssh::kSshErrorCodeSftpTransferFailed,
                              "seek rejected: file closed"};
        }
        ::libssh2_sftp_seek64(handle_, offset);
        return SftpResult::success();
    });
}

SftpResult SftpSession::File::close(SftpCallOptions options)
{
    if (closed_.load(std::memory_order_acquire)) {
        return SftpResult::success(); // idempotent
    }
    if (orphaned_.load(std::memory_order_acquire)) {
        closed_.store(true, std::memory_order_release);
        return SftpResult::success(); // session close() already freed it
    }
    // The loop body is idempotent (null handle = done), so a retry after a
    // timeout/cancel posts again safely instead of double-closing.
    SftpResult result = owner_.runBlocking("sftp_close", options, [this, options] {
        if (handle_ == nullptr) {
            return SftpResult::success();
        }
        const std::uint64_t deadline = steadyNowMs() + options.timeoutMs;
        SftpResult abort;
        for (;;) {
            const int rc = ::libssh2_sftp_close_handle(handle_);
            if (rc == 0) {
                handle_ = nullptr;
                owner_.untrackFile(this);
                return SftpResult::success();
            }
            if (!isEagain(owner_.session_.session_)) {
                // Close failed but the handle is unusable afterwards either
                // way: drop it so a second close/destruction stays safe.
                handle_ = nullptr;
                owner_.untrackFile(this);
                return owner_.failureFromCurrentError("sftp_close");
            }
            if (!owner_.waitSlice(options, deadline, abort)) {
                return abort;
            }
        }
    });
    if (result.ok) {
        closed_.store(true, std::memory_order_release);
    }
    return result;
}

SftpResult SftpSession::openFile(const std::string& path, unsigned long flags, long mode,
                                 std::unique_ptr<File>& fileOut, SftpCallOptions options)
{
    fileOut.reset();
    if (!isOpen()) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError,
                          "openFile rejected: sftp session not open"};
    }
    if (path.empty() || flags == 0) {
        return SftpResult{false, ssh::kSshErrorCodeInternalError,
                          "openFile rejected: empty path or zero flags"};
    }
    return runBlocking("sftp_open", options, [this, &path, flags, mode, &fileOut, options] {
        const std::uint64_t deadline = steadyNowMs() + options.timeoutMs;
        SftpResult abort;
        LIBSSH2_SFTP_HANDLE* handle = nullptr;
        for (;;) {
            handle = ::libssh2_sftp_open_ex(sftp_, path.c_str(),
                                            static_cast<unsigned int>(path.size()), flags,
                                            mode, LIBSSH2_SFTP_OPENFILE);
            if (handle != nullptr) {
                break;
            }
            if (!isEagain(session_.session_)) {
                return failureFromCurrentError("sftp_open");
            }
            if (!waitSlice(options, deadline, abort)) {
                return abort;
            }
        }
        auto file = std::unique_ptr<File>(new File(*this, handle));
        trackFile(file.get());
        fileOut = std::move(file);
        return SftpResult::success();
    });
}

SftpResult SftpSession::openFile(const std::string& path, OpenFlags flags, long mode,
                                 std::unique_ptr<File>& fileOut, SftpCallOptions options)
{
    return openFile(path, toOpenFlags(flags), mode, fileOut, options);
}

} // namespace sftp
} // namespace sshclient
