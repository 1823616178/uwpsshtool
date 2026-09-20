#include "fwd/remote_listen.h"

#include "debug_log.h"
#include "io/SessionThread.h"
#include "ssh/session.h"

#include <libssh2.h>

#include <utility>
#include <vector>

#define FWD_LOG(...) sshclient::diagnostics::debugLog("fwd", __VA_ARGS__)

namespace sshclient {
namespace fwd {
namespace {

// Bounded cancel drive: forward_cancel EAGAINs while the session send buffer
// is full; retry on a short timer so an idle session still completes the
// cancel (session-socket events alone may never arrive when idle).
constexpr std::uint64_t kCancelRetryMs = 100;
constexpr unsigned kMaxCancelAttempts = 30; // ~3 s, then drop best effort

} // namespace

RemoteListener::RemoteListener(ssh::SshSession& session, std::string targetHost,
                               std::uint16_t targetPort,
                               std::shared_ptr<TunnelStats> stats,
                               RemoteListenerCallbacks callbacks)
    : session_(session)
    , targetHost_(std::move(targetHost))
    , targetPort_(targetPort)
    , stats_(std::move(stats))
    , callbacks_(std::move(callbacks))
{
    if (!stats_) {
        stats_ = std::make_shared<TunnelStats>();
    }
}

RemoteListener::~RemoteListener()
{
    if (listener_ != nullptr) {
        FWD_LOG("warning: RemoteListener destroyed with a live remote bind "
                "(contract violation, leak)");
    }
    if (!connections_.empty()) {
        FWD_LOG("warning: RemoteListener destroyed with %u live connections "
                "(contract violation, leak)",
                static_cast<unsigned>(connections_.size()));
    }
}

bool RemoteListener::isLegalTransition(RemoteListenState from, RemoteListenState to)
{
    using S = RemoteListenState;
    switch (from) {
    case S::Idle:
        return to == S::Opening;
    case S::Opening:
        return to == S::Listening || to == S::Cancelling || to == S::Stopped;
    case S::Listening:
        return to == S::Cancelling || to == S::Stopped;
    case S::Cancelling:
        return to == S::Stopped;
    case S::Stopped:
        return false;
    }
    return false;
}

bool RemoteListener::start(std::string bindHost, std::uint16_t bindPort)
{
    if (targetHost_.empty() || targetPort_ == 0) {
        FWD_LOG("remote listen rejected: no tunnel target configured");
        return false;
    }
    if (session_.state() != ssh::SshSessionState::Established) {
        FWD_LOG("remote listen rejected: session state %s (only established admits)",
                ssh::toString(session_.state()));
        return false;
    }
    bool expected = false;
    if (!startAdmitted_.compare_exchange_strong(expected, true)) {
        FWD_LOG("remote listen rejected: already started");
        return false;
    }
    bindHost_ = std::move(bindHost);
    bindPort_ = bindPort;
    session_.thread_.post([this] { begin(); });
    return true;
}

void RemoteListener::stop()
{
    bool expected = false;
    if (!stopRequested_.compare_exchange_strong(expected, true)) {
        return; // idempotent
    }
    session_.thread_.post([this] {
        const RemoteListenState state = state_.load(std::memory_order_acquire);
        if (state == RemoteListenState::Listening) {
            transitionTo(RemoteListenState::Cancelling);
            drive();
            session_.updateSocketInterest();
            return;
        }
        if (state == RemoteListenState::Opening) {
            // The open finishes first; the flag redirects it into Cancelling.
            drive();
            return;
        }
        // Idle (begin never ran / start failed) or already terminal: nothing
        // to cancel, and no onStopped (contract: only after a start success).
    });
}

void RemoteListener::begin()
{
    transitionTo(RemoteListenState::Opening);
    session_.registerRemoteListener(this);
    if (session_.state() != ssh::SshSessionState::Established ||
        session_.session_ == nullptr) {
        startAdmitted_.store(false, std::memory_order_release); // allow a retry
        failStart(RemoteListenError::NotEstablished, "session left the established state");
        return;
    }
    drive();
    session_.updateSocketInterest();
    if (stopRequested_.load(std::memory_order_acquire) &&
        state_.load(std::memory_order_acquire) == RemoteListenState::Listening) {
        transitionTo(RemoteListenState::Cancelling);
        drive();
    }
}

void RemoteListener::drive()
{
    const RemoteListenState state = state_.load(std::memory_order_acquire);
    switch (state) {
    case RemoteListenState::Opening:
        driveListenOpen();
        if (state_.load(std::memory_order_acquire) == RemoteListenState::Listening) {
            driveAccept(); // a connection may already be queued
        } else if (state_.load(std::memory_order_acquire) ==
                   RemoteListenState::Cancelling) {
            driveCancel(); // stop() raced the bind: cancel on the same pass
                           // (no session event may arrive while idle)
        }
        break;
    case RemoteListenState::Listening:
        driveAccept();
        break;
    case RemoteListenState::Cancelling:
        driveCancel();
        break;
    default:
        break; // Idle/Stopped: defensive no-op
    }
}

bool RemoteListener::driveListenOpen()
{
    // libssh2 shares one forward-listen machine per session: wait our turn.
    if (!session_.tryAcquireFwdListenOpen(this)) {
        return false;
    }
    int boundPort = 0;
    struct _LIBSSH2_LISTENER* listener = ::libssh2_channel_forward_listen_ex(
        session_.session_, bindHost_.empty() ? nullptr : bindHost_.c_str(),
        static_cast<int>(bindPort_), &boundPort, 16);
    if (listener == nullptr) {
        if (::libssh2_session_last_errno(session_.session_) == LIBSSH2_ERROR_EAGAIN) {
            // Gate kept: our continuation owns the machine (noteBlocked
            // equivalent: the session re-arms from block directions below).
            return false;
        }
        session_.releaseFwdListenOpen(this);
        char message[192]{};
        ::sprintf_s(message, sizeof(message), "remote bind failed: %s",
                    session_.lastLibssh2ErrorForFwd().c_str());
        failStart(RemoteListenError::BindFailed, message);
        return true;
    }
    session_.releaseFwdListenOpen(this);
    listener_ = listener;
    boundPort_ = static_cast<std::uint16_t>(boundPort != 0 ? boundPort : bindPort_);
    transitionTo(RemoteListenState::Listening);
    FWD_LOG("remote listening on %s:%u -> %s:%u",
            bindHost_.empty() ? "*" : bindHost_.c_str(), boundPort_,
            targetHost_.c_str(), targetPort_);
    if (callbacks_.onListening) {
        RemoteListenResult result;
        result.success = true;
        result.boundPort = boundPort_;
        callbacks_.onListening(result);
    }
    if (stopRequested_.load(std::memory_order_acquire)) {
        // stop() raced the bind: redirect into Cancelling (SshChannel-style
        // close-races-open convention).
        transitionTo(RemoteListenState::Cancelling);
    }
    return true;
}

void RemoteListener::driveAccept()
{
    if (listener_ == nullptr) {
        return;
    }
    struct _LIBSSH2_CHANNEL* accepted = ::libssh2_channel_forward_accept(listener_);
    if (accepted == nullptr) {
        const int lastErrno = ::libssh2_session_last_errno(session_.session_);
        if (lastErrno != LIBSSH2_ERROR_EAGAIN) {
            // Transient accept error (not fatal): the next session event
            // retries. Logged, never fatal (same spirit as setenv refusal).
            FWD_LOG("forward accept stalled (%s)", session_.lastLibssh2ErrorForFwd().c_str());
        }
        return;
    }
    spawnConnection(accepted);
}

void RemoteListener::spawnConnection(struct _LIBSSH2_CHANNEL* accepted)
{
    ForwardedCallbacks callbacks;
    callbacks.onOpen = [](const ForwardOpenResult&) {}; // stats flow regardless
    auto owned =
        std::make_unique<ForwardedConnection>(session_, accepted, stats_,
                                              std::move(callbacks));
    ForwardedConnection* raw = owned.get();
    owned->setCloseCallback([this, raw](const ForwardCloseInfo&) {
        for (auto it = connections_.begin(); it != connections_.end(); ++it) {
            if (it->get() == raw) {
                connections_.erase(it); // destroys post-onClose: contract holds
                break;
            }
        }
    });
    if (!owned->openRemote(targetHost_, targetPort_)) {
        // Admission refused (session raced away): the Idle destructor frees
        // the accepted-but-unused channel? No —the destructor only warns on
        // live channels (freeing needs the loop thread, and we ARE on the
        // loop thread here, so free it explicitly for a leak-free refusal).
        ::libssh2_channel_free(accepted);
        return;
    }
    connections_.insert(std::move(owned));
}

bool RemoteListener::driveCancel()
{
    if (listener_ == nullptr) {
        finishStop();
        return true;
    }
    const int rc = ::libssh2_channel_forward_cancel(listener_);
    if (rc == LIBSSH2_ERROR_EAGAIN) {
        // Keep the session Writable-armed through the shared gate below, and
        // additionally retry on a short timer so an idle session still
        // completes (no session-socket events may arrive while idle).
        if (!cancelTimerArmed_) {
            cancelTimerArmed_ = true;
            cancelTimerId_ = session_.thread_.loop().runAfter(kCancelRetryMs, [this] {
                cancelTimerArmed_ = false;
                if (state_.load(std::memory_order_acquire) ==
                    RemoteListenState::Cancelling) {
                    drive();
                    session_.updateSocketInterest();
                }
            });
        }
        if (++cancelAttempts_ >= kMaxCancelAttempts) {
            FWD_LOG("forward cancel stalled, dropping best effort (server port "
                    "releases when the session ends)");
            listener_ = nullptr; // do NOT touch after a dropped cancel: the
                                 // session free covers it (onSessionLost skips
                                 // cancel on a null handle)
            finishStop();
            return true;
        }
        return false;
    }
    if (rc != 0) {
        FWD_LOG("forward cancel failed (best effort): %s",
                session_.lastLibssh2ErrorForFwd().c_str());
    }
    listener_ = nullptr; // cancel success FREES the listener (channel.c)
    finishStop();
    return true;
}

void RemoteListener::finishStop()
{
    if (cancelTimerArmed_) {
        session_.thread_.loop().cancelTimer(cancelTimerId_);
        cancelTimerArmed_ = false;
    }
    session_.unregisterRemoteListener(this);
    // Graceful per-connection close (snapshot: onClose mutates the set).
    std::vector<ForwardedConnection*> snapshot;
    for (const auto& owned : connections_) {
        snapshot.push_back(owned.get());
    }
    for (ForwardedConnection* connection : snapshot) {
        connection->close();
    }
    transitionTo(RemoteListenState::Stopped);
    if (!startFailed_ && !stopFired_ && callbacks_.onStopped) {
        stopFired_ = true;
        callbacks_.onStopped();
    }
}

void RemoteListener::failStart(RemoteListenError error, const std::string& message)
{
    if (state_.load(std::memory_order_acquire) == RemoteListenState::Stopped) {
        return; // first verdict wins (timer/drive duality cannot double-fire
                // here, but session-loss races can)
    }
    FWD_LOG("remote listen failed (%s): %s",
            error == RemoteListenError::BindFailed ? "bind_failed"
            : error == RemoteListenError::SessionLost ? "session_lost"
                                                      : "not_established",
            message.c_str());
    startFailed_ = true; // finishStop suppresses onStopped on this path
    session_.releaseFwdListenOpen(this);
    session_.unregisterRemoteListener(this);
    if (callbacks_.onListening) {
        RemoteListenResult result;
        result.error = error;
        result.message = message;
        callbacks_.onListening(result);
    }
    transitionTo(RemoteListenState::Stopped);
}

void RemoteListener::onSessionLost()
{
    // Loop thread from SshSession::releaseResources. The session struct is
    // still allocated but dying: skip forward_cancel (it would send on a dead
    // transport), drop the handle, and let session_free cover the server side.
    // Connections were already force-cleaned (notify order: connections via
    // their own path first —see SshSession::notifyForwardedSessionLost).
    session_.releaseFwdListenOpen(this);
    if (cancelTimerArmed_) {
        session_.thread_.loop().cancelTimer(cancelTimerId_);
        cancelTimerArmed_ = false;
    }
    listener_ = nullptr;
    if (!connections_.empty()) {
        FWD_LOG("warning: %u connections survived session loss (leak)",
                static_cast<unsigned>(connections_.size()));
        connections_.clear();
    }
    const RemoteListenState state = state_.load(std::memory_order_acquire);
    if (state == RemoteListenState::Opening) {
        failStart(RemoteListenError::SessionLost,
                  "session lost/closed, listener force-cleaned");
        return;
    }
    if (state == RemoteListenState::Listening || state == RemoteListenState::Cancelling) {
        transitionTo(RemoteListenState::Stopped);
        // No onStopped: the session is gone and no reconnect reuses this
        // listener (F05 recreates it).
    }
}

void RemoteListener::transitionTo(RemoteListenState to)
{
    // Local helper (no legality-table rejection noise: transitions are
    // internal; assert legality in tests through isLegalTransition).
    const RemoteListenState from = state_.load(std::memory_order_acquire);
    if (!isLegalTransition(from, to)) {
        FWD_LOG("illegal remote-listen transition %s -> %s rejected", toString(from),
                toString(to));
        return;
    }
    state_.store(to, std::memory_order_release);
    FWD_LOG("remote listen state %s -> %s", toString(from), toString(to));
}

const char* toString(RemoteListenState state)
{
    switch (state) {
    case RemoteListenState::Idle:      return "idle";
    case RemoteListenState::Opening:   return "opening";
    case RemoteListenState::Listening: return "listening";
    case RemoteListenState::Cancelling: return "cancelling";
    case RemoteListenState::Stopped:   return "stopped";
    }
    return "unknown";
}

const char* toString(RemoteListenError error)
{
    switch (error) {
    case RemoteListenError::None:           return "none";
    case RemoteListenError::NotEstablished: return "not_established";
    case RemoteListenError::BindFailed:     return "bind_failed";
    case RemoteListenError::SessionLost:    return "session_lost";
    }
    return "unknown";
}

} // namespace fwd
} // namespace sshclient
