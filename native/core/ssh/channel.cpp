#include "channel.h"

#include "debug_log.h"
#include "session.h"
#include "io/SessionThread.h"

#include <libssh2.h>

#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <utility>

#define SSH_LOG(...) sshclient::diagnostics::debugLog("ssh", __VA_ARGS__)

namespace sshclient {
namespace ssh {

// ---------------------------------------------------------------- pure logic

void PendingWriteQueue::push(std::string chunk)
{
    if (chunk.empty()) {
        return;
    }
    chunks_.push_back(std::move(chunk));
}

bool PendingWriteQueue::flush(const std::function<ptrdiff_t(const char* data, size_t len)>& writer,
                              bool* stalledOut, size_t* flushedBytesOut)
{
    if (stalledOut != nullptr) {
        *stalledOut = false;
    }
    if (flushedBytesOut != nullptr) {
        *flushedBytesOut = 0;
    }
    bool progress = false;
    while (!chunks_.empty()) {
        std::string& head = chunks_.front();
        const ptrdiff_t sent =
            writer(head.data() + headOffset_, head.size() - headOffset_);
        if (sent < 0) {
            if (stalledOut != nullptr) {
                *stalledOut = true;
            }
            break; // stalled (EAGAIN): keep the remainder for the next event
        }
        if (sent == 0) {
            break; // no progress; guard against a spinning loop
        }
        progress = true;
        const size_t n = static_cast<size_t>(sent);
        if (flushedBytesOut != nullptr) {
            *flushedBytesOut += n;
        }
        headOffset_ += n;
        if (headOffset_ == head.size()) {
            chunks_.pop_front();
            headOffset_ = 0;
        }
    }
    return progress;
}

void PendingWriteQueue::clear()
{
    chunks_.clear();
    headOffset_ = 0;
}

size_t PendingWriteQueue::remainingBytes() const
{
    size_t total = 0;
    for (const std::string& chunk : chunks_) {
        total += chunk.size();
    }
    return total - headOffset_; // the sent head part was already accounted off
}

ChannelReadKind classifyChannelRead(ptrdiff_t result)
{
    if (result > 0) {
        return ChannelReadKind::Data;
    }
    if (result == 0) {
        return ChannelReadKind::Eof;
    }
    if (result == LIBSSH2_ERROR_EAGAIN) {
        return ChannelReadKind::Stalled;
    }
    return ChannelReadKind::Error;
}

// ---------------------------------------------------------------- lifecycle

SshChannel::SshChannel(SshSession& session, SshChannelCallbacks callbacks)
    : session_(session), callbacks_(std::move(callbacks))
{
}

SshChannel::~SshChannel()
{
    // Destruction contract (see header): destroy only after an onOpen failure
    // or after onClose, when the libssh2 handle is freed and the channel is
    // unregistered. A violation cannot be freed safely cross-thread; warn only.
    if (channel_ != nullptr) {
        SSH_LOG("warning: SshChannel destroyed with a live handle (contract violation, leak)");
    }
}

// ---------------------------------------------------------------- open admission (any thread)

bool SshChannel::openShell(PtySpec pty)
{
    if (pty.termType.empty() || pty.cols == 0 || pty.rows == 0) {
        SSH_LOG("openShell rejected: invalid PTY spec (empty term or zero size)");
        return false;
    }
    return admitOpen(RequestKind::Shell, true, std::move(pty), "");
}

bool SshChannel::exec(std::string command)
{
    if (command.empty()) {
        SSH_LOG("exec rejected: empty command");
        return false;
    }
    return admitOpen(RequestKind::Exec, false, PtySpec{}, std::move(command));
}

bool SshChannel::execWithPty(PtySpec pty, std::string command)
{
    if (pty.termType.empty() || pty.cols == 0 || pty.rows == 0 || command.empty()) {
        SSH_LOG("execWithPty rejected: invalid PTY spec or empty command");
        return false;
    }
    return admitOpen(RequestKind::Exec, true, std::move(pty), std::move(command));
}

// Admission (any thread): CAS the slot -> stage the args -> post assembly.
// The staging writes and the loop thread's reads are ordered by the post()
// lock (same happens-before convention as SshSession::connect).
bool SshChannel::admitOpen(RequestKind kind, bool withPty, PtySpec pty, std::string command)
{
    if (session_.state() != SshSessionState::Established) {
        SSH_LOG("channel open rejected: session state %s (only established admits channels)",
                toString(session_.state()));
        return false;
    }
    bool expected = false;
    if (!openAdmitted_.compare_exchange_strong(expected, true)) {
        SSH_LOG("channel open rejected: this channel was opened before");
        return false;
    }
    kind_ = kind;
    pty_ = std::move(pty);
    command_ = std::move(command);
    // hasPty_ publishes before acceptWrites_: the resize admission path reads
    // hasPty_ after acquiring acceptWrites_.
    hasPty_.store(withPty, std::memory_order_relaxed);
    acceptWrites_.store(true, std::memory_order_release);
    session_.thread_.post([this] { begin(); });
    return true;
}

// ---------------------------------------------------------------- data-plane admission (any thread)

bool SshChannel::write(const char* data, size_t len)
{
    if (data == nullptr || !acceptWrites_.load(std::memory_order_acquire)) {
        return false;
    }
    if (len == 0) {
        return true;
    }
    // Backpressure accounting: book first, then audit — over the cap refunds
    // and rejects the whole call (no partial writes, the caller need not track
    // a half-written boundary); a racing concurrent write only makes the
    // rejection more conservative.
    const size_t prev = pendingBytes_.fetch_add(len, std::memory_order_acq_rel);
    if (prev + len > kMaxPendingWriteBytes) {
        pendingBytes_.fetch_sub(len, std::memory_order_release);
        SSH_LOG("write backpressure rejection: %zu + %zu over cap %zu", prev, len,
                kMaxPendingWriteBytes);
        return false;
    }
    session_.thread_.post([this, chunk = std::string(data, len)]() mutable {
        // The channel may be finishing when this lands (write/close cross-thread
        // race): drop and refund. Idle/Opening do NOT drop — open*'s begin()
        // arrives before this task in post FIFO order, so data queues until the
        // channel is ready and then flushes in order.
        const ChannelState st = state_.load(std::memory_order_acquire);
        if (st == ChannelState::Closing || st == ChannelState::Closed) {
            pendingBytes_.fetch_sub(chunk.size(), std::memory_order_release);
            return;
        }
        writeQueue_.push(std::move(chunk));
        pump();
        session_.updateSocketInterest();
    });
    return true;
}

bool SshChannel::resize(std::uint32_t cols, std::uint32_t rows)
{
    if (cols == 0 || rows == 0 || !hasPty_.load(std::memory_order_acquire) ||
        !acceptWrites_.load(std::memory_order_acquire)) {
        return false;
    }
    session_.thread_.post([this, cols, rows] {
        pendingResize_ = std::make_pair(cols, rows); // coalesce: last wins
        pump();
        session_.updateSocketInterest();
    });
    return true;
}

bool SshChannel::setenv(std::string name, std::string value)
{
    if (name.empty() || !acceptWrites_.load(std::memory_order_acquire)) {
        return false;
    }
    session_.thread_.post([this, name = std::move(name), value = std::move(value)]() mutable {
        pendingEnv_.emplace_back(std::move(name), std::move(value));
        pump();
        session_.updateSocketInterest();
    });
    return true;
}

bool SshChannel::sendEof()
{
    if (!acceptWrites_.load(std::memory_order_acquire)) {
        return false;
    }
    session_.thread_.post([this] {
        eofRequested_ = true;
        pump();
        session_.updateSocketInterest();
    });
    return true;
}

void SshChannel::close()
{
    if (!openAdmitted_.load(std::memory_order_acquire)) {
        return; // never opened: nothing to close (and no task posted, per contract)
    }
    bool expected = false;
    if (!closeRequested_.compare_exchange_strong(expected, true)) {
        return; // idempotent
    }
    session_.thread_.post([this] {
        localCloseRequested_ = true;
        pump();
        session_.updateSocketInterest();
    });
}

// ---------------------------------------------------------------- state machine (static pure)

bool SshChannel::isLegalTransition(ChannelState from, ChannelState to)
{
    using S = ChannelState;
    switch (from) {
    case S::Idle:
        return to == S::Opening;
    case S::Opening:
        return to == S::RequestingPty || to == S::Starting || to == S::Closing ||
               to == S::Closed;
    case S::RequestingPty:
        return to == S::Starting || to == S::Closing || to == S::Closed;
    case S::Starting:
        return to == S::Open || to == S::Closing || to == S::Closed;
    case S::Open:
        // Closed: forced cleanup on session loss.
        return to == S::Closing || to == S::Closed;
    case S::Closing:
        return to == S::Closed;
    case S::Closed:
        return false;
    }
    return false;
}

// ---------------------------------------------------------------- assembly and master pump (loop thread)

void SshChannel::begin()
{
    transitionTo(ChannelState::Opening);
    if (session_.state() != SshSessionState::Established || session_.session_ == nullptr) {
        // The session raced away after admission: never registered, terminate
        // right here (reported as an onOpen failure).
        failOpen(SshChannelError::NotEstablished, "session left the established state");
        return;
    }
    session_.registerChannel(this);
    pump();
    session_.updateSocketInterest();
}

bool SshChannel::pump()
{
    bool progress = false;
    const ChannelState st = state_.load(std::memory_order_acquire);
    switch (st) {
    case ChannelState::Opening:
    case ChannelState::RequestingPty:
    case ChannelState::Starting:
        progress = driveOpen();
        if (state_.load(std::memory_order_acquire) == ChannelState::Open) {
            // Open completed: continue into the data plane on the same beat so
            // queued write/resize/eof take effect immediately.
            progress = true;
        } else {
            break; // still opening (EAGAIN) or already failed
        }
        [[fallthrough]];
    case ChannelState::Open: {
        if (driveEnv()) {
            progress = true;
        }
        if (driveResize()) {
            progress = true;
        }
        if (driveSendEof()) {
            progress = true;
        }
        if (drainReads()) {
            progress = true;
        }
        if (flushWrites()) {
            progress = true;
        }
        if (eofSeen_ || localCloseRequested_) {
            // Peer EOF (leftovers drained above) or local close: enter the
            // close handshake.
            transitionTo(ChannelState::Closing);
            progress = true;
        }
        if (state_.load(std::memory_order_acquire) == ChannelState::Closing && driveClose()) {
            progress = true;
        }
        break;
    }
    case ChannelState::Closing:
        if (driveClose()) {
            progress = true;
        }
        break;
    default:
        break; // Idle/Closed: defensive no-op
    }
    return progress;
}

// ---------------------------------------------------------------- open steps (loop thread)

bool SshChannel::driveOpen()
{
    bool progress = false;
    for (;;) {
        const ChannelState st = state_.load(std::memory_order_acquire);
        if (st == ChannelState::Opening) {
            _LIBSSH2_CHANNEL* ch = ::libssh2_channel_open_session(session_.session_);
            if (ch == nullptr) {
                if (::libssh2_session_last_errno(session_.session_) == LIBSSH2_ERROR_EAGAIN) {
                    noteBlocked();
                    return progress;
                }
                failOpen(SshChannelError::OpenFailed, lastLibssh2Error());
                return true;
            }
            channel_ = ch;
            blockedOutbound_ = false;
            transitionTo(hasPty_.load(std::memory_order_relaxed) ? ChannelState::RequestingPty
                                                                 : ChannelState::Starting);
            progress = true;
            continue;
        }
        if (st == ChannelState::RequestingPty) {
            // Pixel dims 0: terminal size applies in character cells only
            // (01-DESIGN section 7.2).
            const int rc = ::libssh2_channel_request_pty_ex(
                channel_, pty_.termType.c_str(),
                static_cast<unsigned int>(pty_.termType.size()), nullptr, 0,
                static_cast<int>(pty_.cols), static_cast<int>(pty_.rows), 0, 0);
            if (rc == LIBSSH2_ERROR_EAGAIN) {
                noteBlocked();
                return progress;
            }
            if (rc != 0) {
                failOpen(SshChannelError::PtyFailed, lastLibssh2Error());
                return true;
            }
            blockedOutbound_ = false;
            transitionTo(ChannelState::Starting);
            progress = true;
            continue;
        }
        if (st == ChannelState::Starting) {
            // Env requests belong before process_startup on the wire; anything
            // queued by now goes out first (later ones run in driveEnv while
            // Open, where a rejection is logged and ignored).
            driveEnv();
            const bool isShell = kind_ == RequestKind::Shell;
            const char* request = isShell ? "shell" : "exec";
            const int rc = ::libssh2_channel_process_startup(
                channel_, request, static_cast<unsigned int>(std::strlen(request)),
                isShell ? nullptr : command_.c_str(),
                isShell ? 0 : static_cast<unsigned int>(command_.size()));
            if (rc == LIBSSH2_ERROR_EAGAIN) {
                noteBlocked();
                return progress;
            }
            if (rc != 0) {
                failOpen(SshChannelError::StartupFailed, lastLibssh2Error());
                return true;
            }
            command_.clear(); // the exec command has served its purpose
            blockedOutbound_ = false;
            transitionTo(ChannelState::Open);
            if (callbacks_.onOpen) {
                callbacks_.onOpen({true, SshChannelError::None, ""});
            }
            return true;
        }
        return progress; // defensive: not in an open step
    }
}

// ---------------------------------------------------------------- data-plane drivers (loop thread)

bool SshChannel::driveEnv()
{
    bool progress = false;
    while (!pendingEnv_.empty()) {
        const std::pair<std::string, std::string>& front = pendingEnv_.front();
        const int rc = ::libssh2_channel_setenv_ex(
            channel_, front.first.c_str(), static_cast<unsigned int>(front.first.size()),
            front.second.c_str(), static_cast<unsigned int>(front.second.size()));
        if (rc == LIBSSH2_ERROR_EAGAIN) {
            noteBlocked();
            return progress;
        }
        blockedOutbound_ = false;
        if (rc != 0) {
            // Servers commonly restrict AcceptEnv: a rejection is logged and
            // ignored, never fatal (task spec).
            SSH_LOG("setenv %s rejected (ignored): %s", front.first.c_str(),
                    lastLibssh2Error().c_str());
        }
        pendingEnv_.pop_front();
        progress = true;
    }
    return progress;
}

bool SshChannel::driveResize()
{
    if (!pendingResize_.has_value()) {
        return false;
    }
    const std::uint32_t cols = pendingResize_->first;
    const std::uint32_t rows = pendingResize_->second;
    const int rc = ::libssh2_channel_request_pty_size_ex(
        channel_, static_cast<int>(cols), static_cast<int>(rows), 0, 0);
    if (rc == LIBSSH2_ERROR_EAGAIN) {
        noteBlocked();
        return false;
    }
    pendingResize_.reset();
    blockedOutbound_ = false;
    if (rc != 0) {
        // A rejected resize is not fatal: log and drop the request (the
        // channel stays usable).
        SSH_LOG("request_pty_size rejected: %s", lastLibssh2Error().c_str());
        return false;
    }
    SSH_LOG("PTY size adjusted: %ux%u", cols, rows);
    return true;
}

bool SshChannel::driveSendEof()
{
    // EOF must follow the pending bytes on the wire: hold while the queue is
    // non-empty (if the peer stopped reading, neither goes out, consistently).
    if (!eofRequested_ || eofSent_ || !writeQueue_.empty()) {
        return false;
    }
    const int rc = ::libssh2_channel_send_eof(channel_);
    if (rc == LIBSSH2_ERROR_EAGAIN) {
        noteBlocked();
        return false;
    }
    blockedOutbound_ = false;
    if (rc != 0) {
        // A failed EOF send is not fatal (the peer is likely broken anyway):
        // give up; a peer stuck waiting on stdin EOF is ultimately covered by
        // peer exit / session teardown.
        SSH_LOG("send_eof failed: %s", lastLibssh2Error().c_str());
        eofRequested_ = false;
        return false;
    }
    eofSent_ = true;
    return true;
}

bool SshChannel::flushWrites()
{
    bool stalled = false;
    bool hardError = false;
    size_t flushed = 0;
    const bool progress = writeQueue_.flush(
        [this, &hardError](const char* data, size_t len) -> ptrdiff_t {
            const ptrdiff_t n = ::libssh2_channel_write_ex(channel_, 0, data, len);
            if (n < 0 && n != LIBSSH2_ERROR_EAGAIN) {
                // Hard error (peer CHANNEL_CLOSED etc.): report as a stall to
                // stop the queue walk, then handle it below.
                hardError = true;
                return -1;
            }
            return n;
        },
        &stalled, &flushed);
    if (flushed > 0) {
        pendingBytes_.fetch_sub(flushed, std::memory_order_release);
        blockedOutbound_ = false;
    }
    if (hardError) {
        // The remainder has nowhere to go: drop the queue. No separate error
        // report — the peer's exit info / the session's disconnect detection
        // supplies the final close reason.
        SSH_LOG("channel write failed (%s), dropping %zu pending bytes",
                lastLibssh2Error().c_str(), pendingBytes_.load(std::memory_order_acquire));
        dropWriteQueue();
        blockedOutbound_ = false;
        return progress;
    }
    if (stalled) {
        noteBlocked();
    }
    return progress;
}

bool SshChannel::drainReads()
{
    bool progress = false;
    char buf[32768]; // LIBSSH2_CHANNEL_PACKET_DEFAULT granularity, stack buffer
    // Drain both streams alternately until both stall: with the default
    // EXTENDED_DATA_NORMAL mode stderr is a separate stream, and reading only
    // stdout lets stderr pile up, suppresses window refill and eventually
    // stalls stdout too.
    for (;;) {
        bool roundProgress = false;
        for (const int stream : {0, SSH_EXTENDED_DATA_STDERR}) {
            for (;;) {
                const ptrdiff_t n =
                    ::libssh2_channel_read_ex(channel_, stream, buf, sizeof(buf));
                const ChannelReadKind kind = classifyChannelRead(n);
                if (kind == ChannelReadKind::Data) {
                    progress = true;
                    roundProgress = true;
                    if (callbacks_.onData) {
                        callbacks_.onData(
                            std::string(buf, static_cast<size_t>(n)),
                            stream == 0 ? ChannelStream::Stdout : ChannelStream::Stderr);
                    }
                    continue;
                }
                if (kind == ChannelReadKind::Eof) {
                    eofSeen_ = true; // channel EOF (read returned 0)
                } else if (kind == ChannelReadKind::Error) {
                    // Read error (peer CHANNEL_CLOSED and similar): finish via
                    // the EOF path; the exit info / session disconnect
                    // detection supplies the final reason, no report here.
                    SSH_LOG("channel read failed (stream %d): %s", stream,
                            lastLibssh2Error().c_str());
                    eofSeen_ = true;
                }
                break; // Stalled/Eof/Error: move to the other stream
            }
        }
        if (!roundProgress) {
            break;
        }
    }
    // A remote close sets eof inside libssh2 (packet.c), so an abrupt
    // close-without-eof from the peer is still observed here.
    if (!eofSeen_ && ::libssh2_channel_eof(channel_) != 0) {
        eofSeen_ = true;
    }
    return progress;
}

// ---------------------------------------------------------------- close path (loop thread)

bool SshChannel::driveClose()
{
    bool progress = false;
    if (!closeCompleted_) {
        // Best-effort flush of the write queue; a stall (peer stopped reading
        // or already closed) does not block the close — leftover bytes are
        // discarded with channel_free.
        if (!writeQueue_.empty() && flushWrites()) {
            progress = true;
        }
        const int rc = ::libssh2_channel_close(channel_);
        if (rc == LIBSSH2_ERROR_EAGAIN) {
            noteBlocked();
            return progress;
        }
        if (rc != 0) {
            const std::string message = "channel close failed: " + lastLibssh2Error();
            if (channel_ != nullptr) {
                ::libssh2_channel_free(channel_); // best effort; session_free covers the rest
                channel_ = nullptr;
            }
            finishClose(ChannelCloseReason::Error, message);
            return true;
        }
        closeCompleted_ = true;
        // exit-status / exit-signal are only guaranteed readable once the
        // close handshake completed.
        collectExitInfo();
        progress = true;
    }
    if (channel_ != nullptr) {
        const int rc = ::libssh2_channel_free(channel_);
        if (rc == LIBSSH2_ERROR_EAGAIN) {
            // Defensive: with close completed, free no longer sends packets
            // and should never EAGAIN.
            noteBlocked();
            return progress;
        }
        channel_ = nullptr;
        progress = true;
    }
    // Close reason precedence (see header): signal > exit status > plain
    // EOF/close.
    ChannelCloseReason reason;
    std::string message;
    if (!exitSignal_.empty()) {
        reason = ChannelCloseReason::ExitSignal;
    } else if (eofSeen_) {
        reason = ChannelCloseReason::ExitStatus;
    } else {
        reason = ChannelCloseReason::PeerEof;
        message = localCloseRequested_ ? "local close, close handshake completed"
                                       : "peer closed the channel without EOF/exit info";
    }
    finishClose(reason, message);
    return true;
}

void SshChannel::collectExitInfo()
{
    exitStatus_ = ::libssh2_channel_get_exit_status(channel_);
    char* signal = nullptr;
    size_t signalLen = 0;
    if (::libssh2_channel_get_exit_signal(channel_, &signal, &signalLen, nullptr, nullptr,
                                          nullptr, nullptr) == 0 &&
        signal != nullptr) {
        exitSignal_.assign(signal, signalLen);
        // get_exit_signal allocates through the session allocator (channel.c
        // LIBSSH2_ALLOC); release with the matching libssh2_free.
        ::libssh2_free(session_.session_, signal);
    }
}

void SshChannel::failOpen(SshChannelError error, const std::string& message)
{
    SSH_LOG("channel open failed (%s): %s", toString(error), message.c_str());
    openFailed_ = true; // finishClose suppresses onClose on this path
    if (callbacks_.onOpen) {
        callbacks_.onOpen({false, error, message});
    }
    if (channel_ != nullptr) {
        // Failed mid-open (pty/startup rejected): release the handle through
        // the normal close path.
        transitionTo(ChannelState::Closing);
        driveClose();
        return;
    }
    finishClose(ChannelCloseReason::Error, message); // unregister + terminal only
}

void SshChannel::finishClose(ChannelCloseReason reason, const std::string& message)
{
    session_.unregisterChannel(this);
    dropWriteQueue();
    transitionTo(ChannelState::Closed);
    if (openFailed_ || !callbacks_.onClose) {
        return;
    }
    ChannelCloseInfo info;
    info.reason = reason;
    info.exitStatus = exitStatus_;
    info.exitSignal = exitSignal_;
    info.message = message;
    callbacks_.onClose(info);
}

void SshChannel::onSessionLost()
{
    // Loop thread, called from SshSession::releaseResources. free() may
    // EAGAIN (peer stopped reading, close packet cannot enter a full send
    // buffer) — the handle then stays in the session's channel table and the
    // session_free retry path in releaseResources covers it (see session.cpp);
    // here we only stop referencing it.
    if (channel_ != nullptr) {
        ::libssh2_channel_free(channel_);
        channel_ = nullptr;
    }
    const ChannelState st = state_.load(std::memory_order_acquire);
    if (st == ChannelState::Open || st == ChannelState::Closing) {
        transitionTo(ChannelState::Closed);
        dropWriteQueue();
        if (!openFailed_ && callbacks_.onClose) {
            ChannelCloseInfo info;
            info.reason = ChannelCloseReason::Error;
            info.message = "session lost/closed, channel force-cleaned";
            callbacks_.onClose(info);
        }
        return;
    }
    if (st == ChannelState::Opening || st == ChannelState::RequestingPty ||
        st == ChannelState::Starting) {
        // Session lost mid-open: report as an onOpen failure (contract: a
        // failed open is never followed by onClose).
        openFailed_ = true;
        transitionTo(ChannelState::Closed);
        dropWriteQueue();
        if (callbacks_.onOpen) {
            callbacks_.onOpen(
                {false, SshChannelError::SessionLost, "session lost/closed, channel force-cleaned"});
        }
    }
    // Idle (never registered, cannot happen) / Closed (already terminal): no-op.
}

// ---------------------------------------------------------------- internals (loop thread)

void SshChannel::dropWriteQueue()
{
    const size_t dropped = writeQueue_.remainingBytes();
    writeQueue_.clear();
    if (dropped > 0) {
        pendingBytes_.fetch_sub(dropped, std::memory_order_release);
    }
}

void SshChannel::noteBlocked()
{
    const int dirs = ::libssh2_session_block_directions(session_.session_);
    blockedOutbound_ = (dirs & LIBSSH2_SESSION_BLOCK_OUTBOUND) != 0;
}

void SshChannel::transitionTo(ChannelState to)
{
    const ChannelState from = state_.load(std::memory_order_acquire);
    if (!isLegalTransition(from, to)) {
        SSH_LOG("illegal channel transition %s -> %s rejected", toString(from), toString(to));
        return;
    }
    if (to == ChannelState::Closing || to == ChannelState::Closed) {
        acceptWrites_.store(false, std::memory_order_release); // no new writes past teardown
    }
    state_.store(to, std::memory_order_release);
    SSH_LOG("channel state %s -> %s", toString(from), toString(to));
}

std::string SshChannel::lastLibssh2Error()
{
    char* errmsg = nullptr;
    int errmsgLen = 0;
    ::libssh2_session_last_error(session_.session_, &errmsg, &errmsgLen, 0);
    return errmsg != nullptr ? std::string(errmsg, static_cast<size_t>(errmsgLen))
                             : std::string("unknown error");
}

// ---------------------------------------------------------------- blocking one-shot exec

ExecResult execCommand(SshSession& session, const std::string& command, std::uint32_t timeoutMs)
{
    ExecResult result;
    std::mutex mutex;
    std::condition_variable cv;
    bool done = false;

    SshChannelCallbacks callbacks;
    callbacks.onOpen = [&](const ChannelOpenResult& open) {
        if (open.success) {
            return;
        }
        std::lock_guard<std::mutex> lock(mutex);
        result.ok = false;
        result.message = open.message;
        done = true;
        cv.notify_one();
    };
    callbacks.onData = [&](const std::string& data, ChannelStream stream) {
        std::lock_guard<std::mutex> lock(mutex);
        if (stream == ChannelStream::Stdout) {
            result.stdoutData += data;
        } else {
            result.stderrData += data;
        }
    };
    callbacks.onClose = [&](const ChannelCloseInfo& info) {
        std::lock_guard<std::mutex> lock(mutex);
        result.exitStatus = info.exitStatus;
        result.ok = info.reason != ChannelCloseReason::Error;
        result.message = info.message;
        done = true;
        cv.notify_one();
    };

    SshChannel channel(session, std::move(callbacks));
    if (!channel.exec(command)) {
        result.message = "exec admission rejected (session not established?)";
        return result;
    }

    std::unique_lock<std::mutex> lock(mutex);
    if (cv.wait_for(lock, std::chrono::milliseconds(timeoutMs), [&] { return done; })) {
        return result;
    }
    // Timed out: ask the channel to close and give the close handshake a
    // grace window so the destruction contract (destroy only after onClose)
    // still holds for a healthy peer.
    result.ok = false;
    result.message = "timeout";
    lock.unlock();
    channel.close();
    lock.lock();
    cv.wait_for(lock, std::chrono::milliseconds(2000), [&] { return done; });
    if (!result.message.empty() && result.message != "timeout") {
        // The close landed during the grace window and overwrote nothing: the
        // timeout verdict stands.
    }
    result.ok = false;
    if (result.message.empty()) {
        result.message = "timeout";
    }
    return result;
}

// ---------------------------------------------------------------- stringification

const char* toString(ChannelState state)
{
    switch (state) {
    case ChannelState::Idle:          return "idle";
    case ChannelState::Opening:       return "opening";
    case ChannelState::RequestingPty: return "pty";
    case ChannelState::Starting:      return "starting";
    case ChannelState::Open:          return "open";
    case ChannelState::Closing:       return "closing";
    case ChannelState::Closed:        return "closed";
    }
    return "unknown";
}

const char* toString(ChannelCloseReason reason)
{
    switch (reason) {
    case ChannelCloseReason::PeerEof:    return "peer_eof";
    case ChannelCloseReason::ExitStatus: return "exit_status";
    case ChannelCloseReason::ExitSignal: return "exit_signal";
    case ChannelCloseReason::Error:      return "error";
    }
    return "unknown";
}

const char* toString(SshChannelError error)
{
    switch (error) {
    case SshChannelError::None:           return "none";
    case SshChannelError::NotEstablished: return "not_established";
    case SshChannelError::OpenFailed:     return "open_failed";
    case SshChannelError::PtyFailed:      return "pty_failed";
    case SshChannelError::StartupFailed:  return "startup_failed";
    case SshChannelError::SessionLost:    return "session_lost";
    }
    return "unknown";
}

} // namespace ssh
} // namespace sshclient
