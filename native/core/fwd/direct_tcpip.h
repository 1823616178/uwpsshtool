#pragma once

// F04: one forwarded TCP connection over an established SSH session
// (01-DESIGN.md section 11.2).
//
// Two establishment halves converge on the same BidirectionalPump (pump.h):
//   - local forward (-L): the TCP socket is already accepted+connected; the
//     loop thread opens a direct-tcpip channel to destHost:destPort
//     (libssh2_channel_direct_tcpip_ex, EAGAIN-driven) and then pumps;
//   - remote forward (-R): the channel is already accepted (RemoteListener via
//     libssh2_channel_forward_accept); the loop thread non-blocking-connects
//     the TCP socket to targetHost:targetPort (getaddrinfo + WSAEWOULDBLOCK +
//     SO_ERROR, same pattern as SshSession::doConnect) and then pumps.
//
// libssh2 1.11 shares one channel-open state machine per session
// (session->open_state): concurrent direct-tcpip opens interleave-corrupt.
// The session therefore grants the channel-open drive to at most one
// ForwardedConnection at a time (SshSession::driveForwarded); the rest wait
// for the next socket event. (A forward open racing a shell/exec open has the
// same libssh2 limitation — opens are sub-second and tunnels are normally
// established while no shell open is in flight; documented, not gated.)
//
// Threading (SshChannel discipline): openLocal/openRemote/close/state are
// any-thread; admission posts to the session's loop thread (FIFO). All
// libssh2 calls, all socket calls and all callbacks run on the loop thread.
// Receivers must not destroy the connection inside a callback; destroy only
// after onOpen failure or onClose (the owning listener erases then).
//
// Callback contract (SshChannel style):
//   - onOpen fires exactly once: success -> {true}; failure -> {false} and no
//     onClose follows;
//   - onClose fires exactly once, only after a successful open;
//   - session loss notifies via onSessionLost (opening connections get an
//     onOpen failure instead).
//
// Ownership: the creating listener (local_listener.h / remote_listen.h) owns
// the connection and erases it in onClose. The session registry holds a raw
// non-owning pointer between register/unregister.
//
// Statistics: established pumps bump TunnelStats::activeConnections (down on
// close) and ::totalConnections; byte counters flow through the pump.

#include <winsock2.h>

#include <atomic>
#include <cstdint>
#include <functional>
#include <memory>
#include <string>

#include "fwd/pump.h"
#include "io/EventLoop.h"

// Forward declaration, keeps <libssh2.h> out of the public header (channel.h
// convention).
struct _LIBSSH2_CHANNEL;

namespace sshclient {
namespace ssh {
class SshSession;
} // namespace ssh

namespace fwd {

enum class ForwardState {
    Idle,
    Opening,    // local: direct-tcpip drive; remote: TCP connect drive
    Pumping,    // both halves ready, bidirectional pump running
    Closing,    // graceful teardown: close handshake -> free -> socket close
    Closed,     // terminal: unregistered, callbacks done
};

enum class ForwardOpenError {
    None,
    NotEstablished,     // session left Established before/while opening
    ChannelOpenFailed,  // direct-tcpip rejected (or accept-side misuse)
    TargetResolveFailed, // remote half: target host does not resolve
    TargetConnectFailed, // remote half: target refused/unreachable/timeout
    SessionLost,         // session dropped/closed while opening
};

struct ForwardOpenResult {
    bool success = false;
    ForwardOpenError error = ForwardOpenError::None; // None on success
    std::string message;                             // failure diagnostics
};

enum class ForwardCloseReason {
    FinishedEof, // both directions EOFed and drained (graceful)
    LocalClose,  // close() was requested (graceful)
    ChannelError, // channel hard error mid-pump
    SocketError,  // socket hard error mid-pump
    SessionLost,  // session dropped/closed underneath
};

struct ForwardCloseInfo {
    ForwardCloseReason reason = ForwardCloseReason::FinishedEof;
    std::string message; // diagnostics / supplementary note
};

struct ForwardedCallbacks {
    std::function<void(const ForwardOpenResult& result)> onOpen; // exactly once
    std::function<void(const ForwardCloseInfo& info)> onClose; // exactly once, after open
};

class ForwardedConnection {
public:
    // Local half: |accepted| must be a connected non-blocking TCP socket
    // (the listener accepts + configures it). Takes ownership immediately
    // (any thread); on open failure the socket is closed on the loop thread.
    ForwardedConnection(ssh::SshSession& session, SOCKET accepted,
                        std::shared_ptr<TunnelStats> stats, ForwardedCallbacks callbacks);
    // Remote half: |accepted| must be a channel from forward_accept (never
    // null). Takes ownership immediately; on open failure the channel is
    // freed on the loop thread.
    ForwardedConnection(ssh::SshSession& session, struct _LIBSSH2_CHANNEL* accepted,
                        std::shared_ptr<TunnelStats> stats, ForwardedCallbacks callbacks);
    ~ForwardedConnection();

    ForwardedConnection(const ForwardedConnection&) = delete;
    ForwardedConnection& operator=(const ForwardedConnection&) = delete;

    // ---- open (any thread; only Idle admits, true = admitted) ----
    // Local: open a direct-tcpip channel to dest; shost/sport identify the
    // originator (from the accepted socket's peer name; "127.0.0.1"/0 fallback).
    bool openLocal(std::string destHost, std::uint16_t destPort, std::string srcHost,
                   std::uint16_t srcPort);
    // Remote: connect the socket to the tunnel target.
    bool openRemote(std::string targetHost, std::uint16_t targetPort);

    // Close (any thread, idempotent): an in-flight open finishes first; a
    // pumping connection best-effort flushes both pump buffers before the
    // channel close handshake. The result arrives via onClose.
    void close();

    ForwardState state() const { return state_.load(std::memory_order_acquire); }

    // Replace the onClose callback (loop thread only; must precede the open —
    // lets the owning listener bind an erasure keyed on the heap address).
    void setCloseCallback(std::function<void(const ForwardCloseInfo& info)> callback);

    // Step-machine legality table (static pure function, for unit tests).
    static bool isLegalTransition(ForwardState from, ForwardState to);

private:
    friend class ssh::SshSession; // event dispatch (pump) and session-loss cleanup

    enum class Half { Local, Remote };

    // Common admission path (any thread): CAS slot -> stage args -> post.
    bool admitOpen(Half half, std::string host, std::uint16_t port, std::string srcHost,
                   std::uint16_t srcPort);

    // ---- loop thread only below ----
    void begin();
    void pump(); // master pump: opening drive / bidirectional pump / closing
    void onTcpEvent(SOCKET socket, short events);
    bool driveChannelOpen(); // direct-tcpip EAGAIN continuation (gated serially)
    void beginTargetConnect();   // remote half: resolve + first connect attempt
    void tryConnectNext();       // remote half: next addrinfo attempt
    void driveTargetConnectEvent(); // remote half: connecting-socket verdict
    void onTargetConnected();    // remote half: socket ready -> pumping
    void driveTargetConnect();   // remote half: deadline nudge from session events
    bool drivePumpOnce();      // one BidirectionalPump pass + interest refresh
    bool driveClose();         // close handshake -> free -> socket close -> finish
    void enterClosing(ForwardCloseReason reason, std::string message);
    void failOpen(ForwardOpenError error, const std::string& message);
    void finishClose();
    void onSessionLost(); // forced cleanup when the session drops/closes
    bool wantsOutboundBlocked() const { return blockedOutbound_; }
    void noteBlocked(); // record the EAGAIN direction per libssh2's declaration
    void transitionTo(ForwardState to);
    void cleanupSocket(); // removeSocket + closesocket, idempotent
    void cleanupChannel(); // best-effort free without the close handshake
    std::string lastLibssh2Error();
    void refreshSocketInterest();

    ssh::SshSession& session_;
    ForwardedCallbacks callbacks_;
    std::shared_ptr<TunnelStats> stats_; // never null (admission copies the share)

    // ---- cross-thread visible state ----
    std::atomic<ForwardState> state_{ForwardState::Idle};
    std::atomic<bool> openAdmitted_{false};
    std::atomic<bool> closeRequested_{false};

    // ---- open staging (admitting thread writes before post; the post lock
    // forms the happens-before edge, same convention as SshChannel::admitOpen) ----
    Half half_ = Half::Local;
    std::string host_;
    std::uint16_t port_ = 0;
    std::string srcHost_;
    std::uint16_t srcPort_ = 0;

    // ---- loop thread only ----
    SOCKET socket_ = INVALID_SOCKET;
    bool socketRegistered_ = false;
    struct _LIBSSH2_CHANNEL* channel_ = nullptr;
    bool blockedOutbound_ = false; // last channel EAGAIN stalled outbound
    bool openFailed_ = false;      // cleanup after a failed open: suppress onClose
    bool countedActive_ = false;   // TunnelStats::activeConnections bumped (paired dec)
    bool closeCompleted_ = false;  // libssh2_channel_close returned 0
    unsigned closeAttempts_ = 0;   // bounded close-handshake drive
    ForwardCloseReason closeReason_ = ForwardCloseReason::FinishedEof;
    std::string closeMessage_;

    // Remote-half TCP connect drive (loop thread only).
    struct addrinfo* connectAddrs_ = nullptr;
    struct addrinfo* connectNext_ = nullptr;
    std::uint64_t connectDeadlineMs_ = 0;
    bool connectTimerArmed_ = false;
    io::EventLoop::TimerId connectTimerId_ = 0;

    // Pump wiring (loop thread only; endpoints borrow socket_/channel_).
    class SocketEndpoint;
    class ChannelEndpoint;
    std::unique_ptr<SocketEndpoint> socketEndpoint_;
    std::unique_ptr<ChannelEndpoint> channelEndpoint_;
    std::unique_ptr<BidirectionalPump> pump_;
};

const char* toString(ForwardState state);
const char* toString(ForwardCloseReason reason);
const char* toString(ForwardOpenError error);

} // namespace fwd
} // namespace sshclient
