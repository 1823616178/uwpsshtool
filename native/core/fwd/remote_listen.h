#pragma once

// F04: remote-forward listener (-R): bind a port on the SSH server
// (libssh2_channel_forward_listen_ex), accept one channel per inbound remote
// connection (forward_accept, polled from the session's socket-event drive),
// and connect each accepted channel to the tunnel target over TCP
// (ForwardedConnection::openRemote) (01-DESIGN.md section 11.2).
//
// Ownership mirrors LocalListener: accepted connections are owned (erased in
// onClose); stop() cancels the remote bind AND closes every live connection.
// TunnelStats is shared between the listener and its connections.
//
// libssh2 shares one forward-listen state machine per session
// (session->fwdLstn_state): concurrent forward_listen opens interleave-
// corrupt, so the session grants the listen-open drive to at most one
// RemoteListener at a time (same gate pattern as the direct-tcpip opens).
//
// Threading (SshChannel discipline): start/stop/state are any-thread;
// admission posts to the session's loop thread. All libssh2 calls and all
// callbacks run on the loop thread. Destroy only after onStopped (or after a
// start failure, which never posts).
//
// start() admits only while the session is Established; one listener binds
// one SshSession (F05 recreates it after reconnect).

#include <atomic>
#include <cstdint>
#include <functional>
#include <memory>
#include <set>
#include <string>
#include <vector>

#include "fwd/direct_tcpip.h"
#include "fwd/pump.h"
#include "io/EventLoop.h"

// Forward declaration, keeps <libssh2.h> out of the public header.
struct _LIBSSH2_LISTENER;

namespace sshclient {
namespace ssh {
class SshSession;
} // namespace ssh

namespace fwd {

enum class RemoteListenState { Idle, Opening, Listening, Cancelling, Stopped };

enum class RemoteListenError {
    None,
    NotEstablished, // session left Established before/while opening
    BindFailed,     // server refused the bind (in use / denied)
    SessionLost,    // session dropped/closed while opening
};

struct RemoteListenResult {
    bool success = false;
    RemoteListenError error = RemoteListenError::None; // None on success
    std::uint16_t boundPort = 0; // actual port (echoes the request, or the
                                 // server allocation when 0 was asked)
    std::string message;         // failure diagnostics
};

struct RemoteListenerCallbacks {
    std::function<void(const RemoteListenResult& result)> onListening; // exactly once
    std::function<void()> onStopped; // exactly once, after a successful start
};

class RemoteListener {
public:
    // targetHost/targetPort: where accepted channels connect (as this machine
    // sees it — typically 127.0.0.1:<local service>).
    // stats: shared counters (created internally when null).
    RemoteListener(ssh::SshSession& session, std::string targetHost,
                   std::uint16_t targetPort, std::shared_ptr<TunnelStats> stats,
                   RemoteListenerCallbacks callbacks);
    ~RemoteListener();

    RemoteListener(const RemoteListener&) = delete;
    RemoteListener& operator=(const RemoteListener&) = delete;

    // ---- start (any thread; only Idle admits, true = admitted) ----
    // bindHost: address the server binds ("127.0.0.1" / "0.0.0.0" / "" =
    // server default); bindPort: 0 = server-allocated (actual in onListening).
    bool start(std::string bindHost, std::uint16_t bindPort);
    // Stop (any thread, idempotent): cancels the remote bind (bounded EAGAIN
    // drive) and closes every live connection (each drains gracefully through
    // its own onClose). onStopped fires on the loop thread once the bind is
    // down; live connections finish independently.
    void stop();

    RemoteListenState state() const { return state_.load(std::memory_order_acquire); }
    const std::shared_ptr<TunnelStats>& stats() const { return stats_; }

    // Step-machine legality table (static pure function, for unit tests).
    static bool isLegalTransition(RemoteListenState from, RemoteListenState to);

private:
    friend class ssh::SshSession; // event dispatch (accept drive) and session-loss cleanup

    // ---- loop thread only below ----
    void begin();
    void drive(); // master drive: opening / accept polling / cancelling
    bool driveListenOpen();   // forward_listen_ex EAGAIN continuation (gated serially)
    void driveAccept();       // one forward_accept attempt per pass
    bool driveCancel();       // forward_cancel EAGAIN continuation (bounded)
    void spawnConnection(struct _LIBSSH2_CHANNEL* accepted);
    void eraseConnection(ForwardedConnection* raw); // onClose / open-failure 回收
    void finishStop(); // terminal Stopped + onStopped (exactly once)
    void failStart(RemoteListenError error, const std::string& message);
    void onSessionLost(); // forced cleanup when the session drops/closes
    void transitionTo(RemoteListenState to);

    ssh::SshSession& session_;
    std::string targetHost_;
    std::uint16_t targetPort_ = 0;
    std::shared_ptr<TunnelStats> stats_;
    RemoteListenerCallbacks callbacks_;

    // ---- cross-thread visible state ----
    std::atomic<RemoteListenState> state_{RemoteListenState::Idle};
    std::atomic<bool> startAdmitted_{false};
    std::atomic<bool> stopRequested_{false};

    // ---- start staging (admitting thread writes before post) ----
    std::string bindHost_;
    std::uint16_t bindPort_ = 0;

    // ---- loop thread only ----
    struct _LIBSSH2_LISTENER* listener_ = nullptr;
    std::uint16_t boundPort_ = 0;
    bool startFailed_ = false; // cleanup after a failed start: suppress onStopped
    bool stopFired_ = false;   // onStopped exactly once
    unsigned cancelAttempts_ = 0; // bounded cancel drive
    io::EventLoop::TimerId cancelTimerId_ = 0;
    bool cancelTimerArmed_ = false;
    std::set<std::unique_ptr<ForwardedConnection>> connections_; // owned
};

const char* toString(RemoteListenState state);
const char* toString(RemoteListenError error);

} // namespace fwd
} // namespace sshclient
