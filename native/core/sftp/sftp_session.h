#pragma once

// F01: SFTP over an established SSH session (01-DESIGN.md section 11.1).
//
// SftpSession binds one SshSession and exposes the SFTP subsystem as blocking
// calls (listDir / stat / lstat / readLink / makeDir / rename / removeFile /
// removeDir / setStat / file open-read-write-seek-close). Threading follows
// the SshChannel discipline exactly: every libssh2 call runs on the session's
// loop thread. Each public call posts one task to that thread and blocks the
// caller on a condition variable until it finishes (execCommand pattern); the
// loop task drives EAGAIN continuations with short WSAPoll slices on the raw
// session socket (100 ms), checking the caller's cancel flag and the overall
// deadline every slice.
//
// Consequences (documented in 01-DESIGN section 11.1):
//   - never call from the session's loop thread (it would self-deadlock);
//   - while an SFTP call is in flight the loop thread serves nothing else,
//     so timers/channels of THIS session stall briefly (bounded by the call's
//     timeoutMs); prefer a dedicated session for SFTP;
//   - SshSession::close() issued mid-call runs after the in-flight call.
// Destruction contract (SshChannel style): close() first (it blocks until the
// loop-thread shutdown finishes), then destroy. A live File destroyed without
// close() leaks its handle (warned); SftpSession::close() force-closes tracked
// files and marks them orphaned so their later close() is a safe no-op.
//
// Error model: SftpResult carries the unified numeric code directly (0 on
// success; 6xx SFTP codes from error_codes.h for SFTP failures, 401/402/403
// reused for transport failures, 500 for local misuse). SshSessionError is
// untouched — SFTP never flows through the session state machine.
// FX status mapping (sftpStatusToCode) is pure logic and unit-tested.
//
// Pure logic in this header: entry-type mapping, remote path join.
// Everything else needs a live (or at least Idle, for admission tests)
// SshSession.

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <initializer_list>
#include <memory>
#include <mutex>
#include <set>
#include <string>
#include <vector>

#include "ssh/session.h"

// Forward declarations, keeps <libssh2_sftp.h> out of the public header
// (channel.h convention for <libssh2.h>).
struct _LIBSSH2_SFTP;
struct _LIBSSH2_SFTP_HANDLE;

namespace sshclient {
namespace sftp {

// ---- unified-code shorthands (values in ssh/error_codes.h) ----
int sftpStatusToCode(unsigned long sftpStatus); // LIBSSH2_FX_* -> unified code

enum class SftpEntryType {
    Unknown,
    File,
    Directory,
    Symlink,
    Fifo,
    Socket,
    CharDevice,
    BlockDevice,
};

SftpEntryType entryTypeFromPermissions(unsigned long permissions, bool hasPermissions);

// Remote path join with exactly one '/' separator (SFTP paths always use '/').
std::string joinRemotePath(const std::string& dir, const std::string& name);

struct SftpAttrs {
    bool hasSize = false;
    std::uint64_t size = 0;
    bool hasPermissions = false; // full mode incl. the S_IFMT type bits
    std::uint32_t permissions = 0;
    bool hasUidGid = false;
    std::uint32_t uid = 0;
    std::uint32_t gid = 0;
    bool hasTimes = false; // atime AND mtime travel together (libssh2/server pair)
    std::uint32_t atime = 0;
    std::uint32_t mtime = 0;
};

struct SftpEntry {
    std::string name;
    SftpEntryType type = SftpEntryType::Unknown;
    SftpAttrs attrs;
    std::string linkTarget; // symlinks only (resolved by listDir via readlink)
};

// Per-call options shared by every blocking call (metadata ops and file IO).
struct SftpCallOptions {
    std::uint32_t timeoutMs = 15000;
    const std::atomic<bool>* cancel = nullptr; // tripped -> SftpCancelled (606)
};

struct SftpResult {
    bool ok = false;
    int code = 0; // unified code (0 on success)
    std::string message;
    static SftpResult success() { return {true, 0, {}}; }
};

const char* toString(SftpEntryType type);

class SftpSession {
public:
    // Any-thread construction; session must outlive this object.
    explicit SftpSession(ssh::SshSession& session);
    ~SftpSession();

    SftpSession(const SftpSession&) = delete;
    SftpSession& operator=(const SftpSession&) = delete;

    // Open the SFTP subsystem (any thread except the loop thread; requires
    // the session Established). Idempotent: already-open returns success.
    SftpResult open(SftpCallOptions options = {});
    // Best-effort shutdown + force-close of tracked files (any thread except
    // the loop thread). Idempotent; blocks until the loop task finishes.
    void close();
    bool isOpen() const { return open_.load(std::memory_order_acquire); }

    // ---- metadata ops (any thread except the loop thread; require open) ----
    // listDir filters out "." and ".." and resolves linkTarget for symlinks.
    SftpResult listDir(const std::string& path, std::vector<SftpEntry>& entriesOut,
                       SftpCallOptions options = {});
    // followSymlink=true -> stat, false -> lstat.
    SftpResult stat(const std::string& path, bool followSymlink, SftpAttrs& attrsOut,
                    SftpCallOptions options = {});
    SftpResult readLink(const std::string& path, std::string& targetOut,
                        SftpCallOptions options = {});
    SftpResult makeDir(const std::string& path, std::uint32_t mode = 0755,
                       SftpCallOptions options = {});
    // POSIX-overwrite rename (libssh2_sftp_rename: OVERWRITE|ATOMIC|NATIVE).
    SftpResult rename(const std::string& oldPath, const std::string& newPath,
                      SftpCallOptions options = {});
    SftpResult removeFile(const std::string& path, SftpCallOptions options = {});
    SftpResult removeDir(const std::string& path, SftpCallOptions options = {});
    // Applies permissions and/or atime+mtime from attrs (only flagged fields).
    SftpResult setStat(const std::string& path, const SftpAttrs& attrs,
                       SftpCallOptions options = {});

    // ---- file IO ----
    enum class OpenFlags : unsigned long {
        Read = 1,
        Write = 2,
        Append = 4,
        Create = 8,
        Truncate = 16,
        Exclusive = 32,
    };

    class File {
    public:
        ~File();

        File(const File&) = delete;
        File& operator=(const File&) = delete;

        // Reads up to maxLen bytes; bytesReadOut==0 means EOF. Short reads are
        // normal (caller loops); EAGAIN waits internally within timeoutMs.
        SftpResult readSome(char* buffer, std::size_t maxLen, std::size_t& bytesReadOut,
                            SftpCallOptions options = {});
        // Loops until all len bytes are acked (libssh2 short-writes); a zero
        // return is treated as EAGAIN (wait + retry), never a spin.
        SftpResult writeAll(const char* data, std::size_t len, SftpCallOptions options = {});
        SftpResult seek(std::uint64_t offset, SftpCallOptions options = {});
        // Idempotent; marks orphaned files safe. Must precede destruction.
        SftpResult close(SftpCallOptions options = {});
        bool isClosed() const { return closed_.load(std::memory_order_acquire); }

    private:
        friend class SftpSession;
        File(SftpSession& owner, struct _LIBSSH2_SFTP_HANDLE* handle);

        SftpSession& owner_;
        struct _LIBSSH2_SFTP_HANDLE* handle_ = nullptr; // loop thread only
        std::atomic<bool> closed_{false};
        std::atomic<bool> orphaned_{false}; // session close() freed it already
    };

    SftpResult openFile(const std::string& path, unsigned long flags, long mode,
                        std::unique_ptr<File>& fileOut, SftpCallOptions options = {});
    // flags overload for the enum above (bitwise-or via toOpenFlags).
    SftpResult openFile(const std::string& path, OpenFlags flags, long mode,
                        std::unique_ptr<File>& fileOut, SftpCallOptions options = {});

    static unsigned long toOpenFlags(OpenFlags flags) { return static_cast<unsigned long>(flags); }
    static unsigned long toOpenFlags(std::initializer_list<OpenFlags> flags);

private:
    friend class File;

    // Loop-thread body of one posted call. Checks Established/socket/handle
    // preconditions, runs work (which uses waitSlice for EAGAIN), then
    // refreshes the socket interest. Never throws.
    using LoopWork = std::function<SftpResult()>;
    SftpResult runBlocking(const char* what, const SftpCallOptions& options, LoopWork work);
    // Same without the Established gate (close() path: the loop body tolerates
    // a torn-down session and drops without libssh2 calls).
    SftpResult runBlockingAlways(const char* what, const SftpCallOptions& options,
                                 LoopWork work, bool requireEstablished);

    // Loop thread only below.
    bool waitSlice(const SftpCallOptions& options, std::uint64_t deadlineMs,
                   SftpResult& abortOut); // true = retry the libssh2 call now
    SftpResult failureFromCurrentError(const char* what); // session errno + FX status
    std::string lastLibssh2Error();
    void trackFile(File* file);
    void untrackFile(File* file);

    ssh::SshSession& session_;
    std::atomic<bool> open_{false};
    std::atomic<bool> openAdmitted_{false};
    std::mutex openMutex_; // serializes open()/close() admission on caller threads
    struct _LIBSSH2_SFTP* sftp_ = nullptr; // loop thread only
    std::set<File*> files_;                // loop thread only (all touches there)
};

} // namespace sftp
} // namespace sshclient
