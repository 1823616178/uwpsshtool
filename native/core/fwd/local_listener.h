#pragma once

// F04: local-forward listener (-L): Winsock bind/listen on the session's I/O
// thread, one direct-tcpip ForwardedConnection per accepted client
// (01-DESIGN.md section 11.2).
//
// Each accepted socket is configured non-blocking + TCP_NODELAY, its peer
// name feeds the direct-tcpip shost/sport, and the connection is owned by the
// listener: onClose erases it; stop() closes the listen socket (no new
// accepts) AND closes every live connection (each drains gracefully through
// its own Closing path). TunnelStats is shared between the listener and its
// connections (F05 polls it for rates).
//
// Threading (SshChannel discipline): start/stop/state are any-thread;
// admission posts to the session's loop thread. All socket calls and all
// callbacks run on the loop thread. Destroy only after onStopped (or after a
// start failure, which never posts).
//
// start() admits only while the session is Established (tunnel startup is
// ordered after connect by the F05 TunnelManager); accepts landing after a
// session loss are closed immediately until the listener is stopped and
// recreated against the new session (one listener binds one SshSession).

#include <winsock2.h>

#include <atomic>
#include <cstdint>
#include <functional>
#include <memory>
#include <set>
#include <string>

#include "fwd/direct_tcpip.h"
#include "fwd/pump.h"

namespace sshclient {
namespace ssh {
class SshSession;
} // namespace ssh

namespace fwd {

enum class LocalListenState { Idle, Listening, Stopped };

struct LocalListenResult {
    bool success = false;
    std::uint16_t boundPort = 0; // actual port (echoes the request, or the
                                 // ephemeral allocation when 0 was asked)
    std::string message;         // failure diagnostics
};

struct LocalListenerCallbacks {
    std::function<void(const LocalListenResult& result)> onListening; // exactly once
    std::function<void()> onStopped; // exactly once, after a successful start
};

class LocalListener {
public:
    // destHost/destPort: the direct-tcpip target (as the SSH server sees it).
    // stats: shared counters (created internally when null).
    LocalListener(ssh::SshSession& session, std::string destHost, std::uint16_t destPort,
                  std::shared_ptr<TunnelStats> stats, LocalListenerCallbacks callbacks);
    ~LocalListener();

    LocalListener(const LocalListener&) = delete;
    LocalListener& operator=(const LocalListener&) = delete;

    // ---- start (any thread; only Idle admits, true = admitted) ----
    // listenHost: bind address ("127.0.0.1" / "0.0.0.0" / "" = any);
    // listenPort: 0 = ephemeral (actual port in onListening).
    bool start(std::string listenHost, std::uint16_t listenPort);
    // Stop (any thread, idempotent): closes the listen socket (no new
    // accepts) and closes every live connection (each drains gracefully
    // through its own onClose). onStopped fires on the loop thread once the
    // listen socket is down; live connections finish independently.
    void stop();

    LocalListenState state() const { return state_.load(std::memory_order_acquire); }
    const std::shared_ptr<TunnelStats>& stats() const { return stats_; }

private:
    friend class ssh::SshSession; // session-loss cleanup

    // ---- loop thread only below ----
    void begin();
    void onListenEvent(SOCKET socket, short events);
    void acceptBurst();
    void spawnConnection(SOCKET accepted);
    void teardown(const char* reason); // close listen socket + all connections
    void onSessionLost();              // forced cleanup when the session drops/closes

    ssh::SshSession& session_;
    std::string destHost_;
    std::uint16_t destPort_ = 0;
    std::shared_ptr<TunnelStats> stats_;
    LocalListenerCallbacks callbacks_;

    // ---- cross-thread visible state ----
    std::atomic<LocalListenState> state_{LocalListenState::Idle};
    std::atomic<bool> startAdmitted_{false};
    std::atomic<bool> stopRequested_{false};

    // ---- start staging (admitting thread writes before post) ----
    std::string listenHost_;
    std::uint16_t listenPort_ = 0;

    // ---- loop thread only ----
    SOCKET listenSocket_ = INVALID_SOCKET;
    bool listenRegistered_ = false;
    std::uint16_t boundPort_ = 0;
    std::set<std::unique_ptr<ForwardedConnection>> connections_; // owned
};

} // namespace fwd
} // namespace sshclient
