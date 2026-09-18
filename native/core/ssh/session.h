#pragma once

#include <winsock2.h>

#include <atomic>
#include <cstdint>
#include <functional>
#include <mutex>
#include <optional>
#include <string>

#include "hostkey.h"
#include "io/EventLoop.h"

struct _LIBSSH2_SESSION;

namespace sshclient {
namespace io {
class SessionThread;
}
namespace ssh {

enum class SshSessionState {
    Idle,
    Connecting,
    Handshaking,
    Authenticating,
    Established,
    Closing,
    Closed,
    Disconnected,
    Error,
};

// Values already match the public error contract where N03 needs a concrete
// code. N08 supplies the complete shared table and libssh2 mapping.
enum class SshSessionError : int {
    None = 0,
    DnsResolutionFailed = 101,
    ConnectTimeout = 102,
    ConnectionRefused = 103,
    NetworkUnreachable = 104,
    AlgorithmNegotiationFailed = 301,
    HostKeyMismatch = 303,
    HandshakeFailed = 304,
    HandshakeTimeout = 305,
    RemoteClosed = 401,
    SocketError = 403,
    InternalError = 500,
};

struct SshSessionOptions {
    std::uint32_t connectTimeoutMs = 15000;
    std::uint32_t handshakeTimeoutMs = 15000;
    std::uint32_t closeFlushTimeoutMs = 2000;
    // Invoked on the loop thread after the handshake, before Authenticating.
    // Empty = accept (TOFU first-connect semantics; the Core layer supplies
    // the real known_hosts comparison). Must not block.
    HostKeyCallback hostKeyCallback;
};

class SshSession final {
public:
    using StateCallback = std::function<void(SshSessionState from, SshSessionState to)>;

    SshSession(io::SessionThread& thread, SshSessionOptions options, StateCallback callback);
    ~SshSession();

    SshSession(const SshSession&) = delete;
    SshSession& operator=(const SshSession&) = delete;

    // Only Idle accepts connect. Parameters are copied before work is posted
    // to the session thread.
    bool connect(std::string host, std::uint16_t port, std::string username);
    void close();

    SshSessionState state() const { return state_.load(); }
    SshSessionError lastError() const;
    std::string lastErrorMessage() const;

    // Host key presented by the peer; set once the handshake completes and
    // kept after a rejection so the UI can show actual vs expected.
    std::optional<HostKeyInfo> hostKeyInfo() const;

    static bool isLegalTransition(SshSessionState from, SshSessionState to);

private:
    void doConnect();
    void onSocketEvent(SOCKET socket, short events);
    void beginHandshake();
    void driveHandshake();
    void verifyHostKey();
    void rejectHostKey(std::string message);
    void updateSocketInterest();
    void doClose();
    void driveClose();
    void completeClose();

    bool transitionTo(SshSessionState to);
    void fail(SshSessionError error, std::string message);
    void peerDisconnected(SshSessionError error, std::string message);
    void releaseResources();
    void cancelTimers();

    io::SessionThread& thread_;
    SshSessionOptions options_;
    StateCallback stateCallback_;

    std::atomic<SshSessionState> state_{SshSessionState::Idle};
    std::atomic<bool> connectAdmitted_{false};
    std::atomic<bool> closeAdmitted_{false};

    std::string host_;
    std::uint16_t port_ = 0;
    std::string username_;

    SOCKET socket_ = INVALID_SOCKET;
    bool socketRegistered_ = false;
    _LIBSSH2_SESSION* session_ = nullptr;

    io::EventLoop::TimerId connectTimer_ = 0;
    io::EventLoop::TimerId handshakeTimer_ = 0;
    io::EventLoop::TimerId closeTimer_ = 0;

    mutable std::mutex errorMutex_;
    SshSessionError error_ = SshSessionError::None;
    std::string errorMessage_;

    mutable std::mutex hostKeyMutex_;
    std::optional<HostKeyInfo> hostKeyInfo_;
    bool hostKeyRejected_ = false;
};

const char* toString(SshSessionState state);
const char* toString(SshSessionError error);

} // namespace ssh
} // namespace sshclient
