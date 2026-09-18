#include "session.h"

#include "io/SessionThread.h"

#include <libssh2.h>

#include <ws2tcpip.h>

#include <cstdio>
#include <mutex>
#include <utility>

#define SSH_LOG(...)                                  \
    do {                                              \
        std::fprintf(stderr, "[ssh] " __VA_ARGS__);  \
        std::fprintf(stderr, "\n");                  \
    } while (0)

namespace sshclient {
namespace ssh {
namespace {

std::once_flag g_libssh2InitOnce;
bool g_libssh2InitSucceeded = false;

void EnsureLibssh2Initialized()
{
    std::call_once(g_libssh2InitOnce, [] {
        g_libssh2InitSucceeded = ::libssh2_init(0) == 0;
    });
}

SOCKET CreateTcpSocket(int family, int protocol)
{
    const SOCKET socket = ::socket(family, SOCK_STREAM, protocol);
    if (socket == INVALID_SOCKET) {
        return INVALID_SOCKET;
    }

    u_long nonBlocking = 1;
    if (::ioctlsocket(socket, FIONBIO, &nonBlocking) == SOCKET_ERROR) {
        ::closesocket(socket);
        return INVALID_SOCKET;
    }

    const BOOL enabled = TRUE;
    ::setsockopt(socket, IPPROTO_TCP, TCP_NODELAY,
                 reinterpret_cast<const char*>(&enabled), sizeof(enabled));
    return socket;
}

bool IsConnectInProgress(int error)
{
    return error == WSAEWOULDBLOCK || error == WSAEINPROGRESS || error == WSAEALREADY;
}

SshSessionError ConnectErrorFromWsa(int error)
{
    switch (error) {
    case WSAETIMEDOUT:
        return SshSessionError::ConnectTimeout;
    case WSAENETUNREACH:
    case WSAEHOSTUNREACH:
    case WSAEHOSTDOWN:
        return SshSessionError::NetworkUnreachable;
    default:
        return SshSessionError::ConnectionRefused;
    }
}

bool IsAlgorithmError(int error)
{
    return error == LIBSSH2_ERROR_KEX_FAILURE ||
           error == LIBSSH2_ERROR_KEY_EXCHANGE_FAILURE ||
           error == LIBSSH2_ERROR_METHOD_NOT_SUPPORTED ||
           error == LIBSSH2_ERROR_ALGO_UNSUPPORTED;
}

std::string WsaMessage(const char* prefix, int error)
{
    return std::string(prefix) + " (WSA " + std::to_string(error) + ")";
}

} // namespace

SshSession::SshSession(io::SessionThread& thread,
                       SshSessionOptions options,
                       StateCallback callback)
    : thread_(thread), options_(options), stateCallback_(std::move(callback))
{
    EnsureLibssh2Initialized();
}

SshSession::~SshSession()
{
    // The owner stops the session thread or waits for a terminal state before
    // destruction. Terminal transitions already release on the loop thread.
    if (session_ != nullptr) {
        ::libssh2_session_free(session_);
        session_ = nullptr;
    }
    if (socket_ != INVALID_SOCKET) {
        ::closesocket(socket_);
        socket_ = INVALID_SOCKET;
    }
}

bool SshSession::connect(std::string host, std::uint16_t port, std::string username)
{
    bool expected = false;
    if (!connectAdmitted_.compare_exchange_strong(expected, true)) {
        return false;
    }
    host_ = std::move(host);
    port_ = port;
    username_ = std::move(username);
    thread_.post([this] { doConnect(); });
    return true;
}

void SshSession::close()
{
    const SshSessionState current = state();
    // connect() admits work before the loop publishes Connecting. Preserve a
    // close issued in that small window by queueing it behind doConnect().
    if ((current == SshSessionState::Idle && !connectAdmitted_.load()) ||
        current == SshSessionState::Closing || current == SshSessionState::Closed ||
        current == SshSessionState::Disconnected || current == SshSessionState::Error) {
        return;
    }
    bool expected = false;
    if (closeAdmitted_.compare_exchange_strong(expected, true)) {
        thread_.post([this] { doClose(); });
    }
}

SshSessionError SshSession::lastError() const
{
    std::lock_guard<std::mutex> lock(errorMutex_);
    return error_;
}

std::string SshSession::lastErrorMessage() const
{
    std::lock_guard<std::mutex> lock(errorMutex_);
    return errorMessage_;
}

std::optional<HostKeyInfo> SshSession::hostKeyInfo() const
{
    std::lock_guard<std::mutex> lock(hostKeyMutex_);
    return hostKeyInfo_;
}

bool SshSession::isLegalTransition(SshSessionState from, SshSessionState to)
{
    using State = SshSessionState;
    switch (from) {
    case State::Idle:
        return to == State::Connecting;
    case State::Connecting:
        return to == State::Handshaking || to == State::Closing || to == State::Error;
    case State::Handshaking:
        return to == State::Authenticating || to == State::Closing || to == State::Error;
    case State::Authenticating:
        return to == State::Established || to == State::Closing ||
               to == State::Disconnected || to == State::Error;
    case State::Established:
        return to == State::Closing || to == State::Disconnected || to == State::Error;
    case State::Closing:
        return to == State::Closed;
    case State::Closed:
    case State::Disconnected:
    case State::Error:
        return false;
    }
    return false;
}

void SshSession::doConnect()
{
    if (!transitionTo(SshSessionState::Connecting)) {
        return;
    }
    if (!g_libssh2InitSucceeded) {
        fail(SshSessionError::InternalError, "libssh2_init failed");
        return;
    }

    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;
    addrinfo* addresses = nullptr;
    const std::string service = std::to_string(port_);
    const int lookup = ::getaddrinfo(host_.c_str(), service.c_str(), &hints, &addresses);
    if (lookup != 0) {
        fail(SshSessionError::DnsResolutionFailed,
             std::string("DNS resolution failed: ") + ::gai_strerrorA(lookup));
        return;
    }

    int lastError = WSAECONNREFUSED;
    bool connectedImmediately = false;
    for (addrinfo* address = addresses; address != nullptr && socket_ == INVALID_SOCKET;
         address = address->ai_next) {
        SOCKET candidate = CreateTcpSocket(address->ai_family, address->ai_protocol);
        if (candidate == INVALID_SOCKET) {
            lastError = ::WSAGetLastError();
            continue;
        }
        if (::connect(candidate, address->ai_addr, static_cast<int>(address->ai_addrlen)) == 0) {
            socket_ = candidate;
            connectedImmediately = true;
        } else {
            lastError = ::WSAGetLastError();
            if (IsConnectInProgress(lastError)) {
                socket_ = candidate;
            } else {
                ::closesocket(candidate);
            }
        }
    }
    ::freeaddrinfo(addresses);

    if (socket_ == INVALID_SOCKET) {
        fail(ConnectErrorFromWsa(lastError), WsaMessage("TCP connect failed", lastError));
        return;
    }

    if (!thread_.loop().addSocket(socket_, io::EventLoop::Writable,
                                  [this](SOCKET socket, short events) {
                                      onSocketEvent(socket, events);
                                  })) {
        fail(SshSessionError::InternalError, "failed to register socket with event loop");
        return;
    }
    socketRegistered_ = true;
    connectTimer_ = thread_.loop().runAfter(options_.connectTimeoutMs, [this] {
        if (state() == SshSessionState::Connecting) {
            fail(SshSessionError::ConnectTimeout, "TCP connect timed out");
        }
    });

    if (connectedImmediately) {
        thread_.loop().cancelTimer(connectTimer_);
        connectTimer_ = 0;
        beginHandshake();
    }
}

void SshSession::onSocketEvent(SOCKET, short events)
{
    if (state() == SshSessionState::Closing) {
        if ((events & (POLLERR | POLLHUP | POLLNVAL)) != 0) {
            completeClose();
        } else {
            driveClose();
        }
        return;
    }

    switch (state()) {
    case SshSessionState::Connecting: {
        int socketError = 0;
        int length = sizeof(socketError);
        if (::getsockopt(socket_, SOL_SOCKET, SO_ERROR,
                         reinterpret_cast<char*>(&socketError), &length) == SOCKET_ERROR) {
            socketError = ::WSAGetLastError();
        }
        if (socketError != 0) {
            fail(ConnectErrorFromWsa(socketError), WsaMessage("TCP connect failed", socketError));
            return;
        }
        if ((events & io::EventLoop::Writable) != 0) {
            thread_.loop().cancelTimer(connectTimer_);
            connectTimer_ = 0;
            beginHandshake();
        } else if ((events & (POLLERR | POLLHUP | POLLNVAL)) != 0) {
            fail(SshSessionError::SocketError, "socket failed while connecting");
        }
        return;
    }
    case SshSessionState::Handshaking:
        driveHandshake();
        if (state() == SshSessionState::Handshaking &&
            (events & (POLLERR | POLLHUP | POLLNVAL)) != 0) {
            fail(SshSessionError::HandshakeFailed, "peer closed during SSH handshake");
        }
        return;
    case SshSessionState::Authenticating:
    case SshSessionState::Established:
        if ((events & (POLLERR | POLLHUP | POLLNVAL)) != 0) {
            peerDisconnected(SshSessionError::SocketError, "SSH socket failed");
            return;
        }
        if ((events & io::EventLoop::Readable) != 0) {
            char byte = 0;
            const int received = ::recv(socket_, &byte, 1, MSG_PEEK);
            if (received == 0) {
                peerDisconnected(SshSessionError::RemoteClosed, "SSH peer closed the connection");
            } else if (received == SOCKET_ERROR) {
                const int error = ::WSAGetLastError();
                if (error != WSAEWOULDBLOCK) {
                    peerDisconnected(SshSessionError::SocketError,
                                     WsaMessage("SSH socket read failed", error));
                }
            }
        }
        return;
    default:
        return;
    }
}

void SshSession::beginHandshake()
{
    session_ = ::libssh2_session_init_ex(nullptr, nullptr, nullptr, this);
    if (session_ == nullptr) {
        fail(SshSessionError::InternalError, "libssh2_session_init failed");
        return;
    }
    ::libssh2_session_set_blocking(session_, 0);
    transitionTo(SshSessionState::Handshaking);
    handshakeTimer_ = thread_.loop().runAfter(options_.handshakeTimeoutMs, [this] {
        if (state() == SshSessionState::Handshaking) {
            fail(SshSessionError::HandshakeTimeout, "SSH handshake timed out");
        }
    });
    driveHandshake();
}

void SshSession::driveHandshake()
{
    const int result = ::libssh2_session_handshake(session_, socket_);
    if (result == 0) {
        thread_.loop().cancelTimer(handshakeTimer_);
        handshakeTimer_ = 0;
        verifyHostKey();
        return;
    }
    if (result == LIBSSH2_ERROR_EAGAIN) {
        updateSocketInterest();
        return;
    }

    char* message = nullptr;
    int messageLength = 0;
    ::libssh2_session_last_error(session_, &message, &messageLength, 0);
    fail(IsAlgorithmError(result) ? SshSessionError::AlgorithmNegotiationFailed
                                  : SshSessionError::HandshakeFailed,
         std::string("SSH handshake failed: ") +
             (message == nullptr ? std::to_string(result)
                                 : std::string(message, static_cast<std::size_t>(messageLength))));
}

void SshSession::verifyHostKey()
{
    std::optional<HostKeyInfo> info = extractHostKey(session_);
    {
        std::lock_guard<std::mutex> lock(hostKeyMutex_);
        hostKeyInfo_ = info;
    }
    if (!info.has_value()) {
        // Fail-closed: a session that cannot show its host key never
        // authenticates, with or without a callback.
        rejectHostKey("host key unavailable after handshake");
        return;
    }
    HostKeyDecision decision = HostKeyDecision::Accept;
    if (options_.hostKeyCallback) {
        try {
            decision = options_.hostKeyCallback(*info);
        } catch (...) {
            SSH_LOG("host key callback threw; rejecting");
            decision = HostKeyDecision::Reject;
        }
    }
    if (decision == HostKeyDecision::Accept) {
        transitionTo(SshSessionState::Authenticating);
        updateSocketInterest();
        return;
    }
    rejectHostKey(std::string("host key rejected: ") + info->fingerprintSha256);
}

void SshSession::rejectHostKey(std::string message)
{
    {
        std::lock_guard<std::mutex> lock(errorMutex_);
        error_ = SshSessionError::HostKeyMismatch;
        errorMessage_ = std::move(message);
    }
    hostKeyRejected_ = true;
    if (!transitionTo(SshSessionState::Closing)) {
        return;
    }
    closeTimer_ = thread_.loop().runAfter(options_.closeFlushTimeoutMs, [this] {
        if (state() == SshSessionState::Closing) {
            completeClose();
        }
    });
    driveClose();
}

void SshSession::updateSocketInterest()
{
    short events = 0;
    const int directions = session_ == nullptr ? 0 : ::libssh2_session_block_directions(session_);
    if ((directions & LIBSSH2_SESSION_BLOCK_INBOUND) != 0) {
        events |= io::EventLoop::Readable;
    }
    if ((directions & LIBSSH2_SESSION_BLOCK_OUTBOUND) != 0) {
        events |= io::EventLoop::Writable;
    }
    if (events == 0) {
        events = io::EventLoop::Readable | io::EventLoop::Writable;
    }
    if (!thread_.loop().modifySocket(socket_, events)) {
        // Closing has only one legal terminal transition. A failed interest
        // update must therefore fall back to local cleanup instead of trying
        // the illegal Closing -> Error edge and leaving the session stranded.
        if (state() == SshSessionState::Closing) {
            completeClose();
        } else {
            fail(SshSessionError::InternalError, "failed to update socket interest");
        }
    }
}

void SshSession::doClose()
{
    const SshSessionState current = state();
    if (current == SshSessionState::Closed || current == SshSessionState::Disconnected ||
        current == SshSessionState::Error || current == SshSessionState::Idle ||
        current == SshSessionState::Closing) {
        return;
    }
    if (!transitionTo(SshSessionState::Closing)) {
        return;
    }
    if (session_ == nullptr) {
        completeClose();
        return;
    }

    closeTimer_ = thread_.loop().runAfter(options_.closeFlushTimeoutMs, [this] {
        if (state() == SshSessionState::Closing) {
            completeClose();
        }
    });
    driveClose();
}

void SshSession::driveClose()
{
    if (session_ == nullptr) {
        completeClose();
        return;
    }
    const int reason = hostKeyRejected_ ? SSH_DISCONNECT_HOST_KEY_NOT_VERIFIABLE
                                        : SSH_DISCONNECT_BY_APPLICATION;
    const char* description = hostKeyRejected_ ? "host key verification failed"
                                               : "client closed session";
    const int result = ::libssh2_session_disconnect_ex(session_, reason, description, "");
    if (result == LIBSSH2_ERROR_EAGAIN) {
        updateSocketInterest();
        return;
    }
    completeClose();
}

void SshSession::completeClose()
{
    releaseResources();
    transitionTo(SshSessionState::Closed);
}

bool SshSession::transitionTo(SshSessionState to)
{
    const SshSessionState from = state();
    if (!isLegalTransition(from, to)) {
        SSH_LOG("illegal state transition %s -> %s", toString(from), toString(to));
        return false;
    }
    state_.store(to);
    if (stateCallback_) {
        try {
            stateCallback_(from, to);
        } catch (...) {
            SSH_LOG("state callback threw during %s -> %s", toString(from), toString(to));
        }
    }
    return true;
}

void SshSession::fail(SshSessionError error, std::string message)
{
    {
        std::lock_guard<std::mutex> lock(errorMutex_);
        error_ = error;
        errorMessage_ = std::move(message);
    }
    releaseResources();
    transitionTo(SshSessionState::Error);
}

void SshSession::peerDisconnected(SshSessionError error, std::string message)
{
    {
        std::lock_guard<std::mutex> lock(errorMutex_);
        error_ = error;
        errorMessage_ = std::move(message);
    }
    releaseResources();
    transitionTo(SshSessionState::Disconnected);
}

void SshSession::releaseResources()
{
    cancelTimers();
    if (socketRegistered_) {
        thread_.loop().removeSocket(socket_);
        socketRegistered_ = false;
    }
    if (session_ != nullptr) {
        if (::libssh2_session_free(session_) == LIBSSH2_ERROR_EAGAIN) {
            if (socket_ != INVALID_SOCKET) {
                ::closesocket(socket_);
                socket_ = INVALID_SOCKET;
            }
            ::libssh2_session_free(session_);
        }
        session_ = nullptr;
    }
    if (socket_ != INVALID_SOCKET) {
        ::closesocket(socket_);
        socket_ = INVALID_SOCKET;
    }
}

void SshSession::cancelTimers()
{
    for (io::EventLoop::TimerId* timer : {&connectTimer_, &handshakeTimer_, &closeTimer_}) {
        if (*timer != 0) {
            thread_.loop().cancelTimer(*timer);
            *timer = 0;
        }
    }
}

const char* toString(SshSessionState state)
{
    switch (state) {
    case SshSessionState::Idle: return "idle";
    case SshSessionState::Connecting: return "connecting";
    case SshSessionState::Handshaking: return "handshaking";
    case SshSessionState::Authenticating: return "authenticating";
    case SshSessionState::Established: return "established";
    case SshSessionState::Closing: return "closing";
    case SshSessionState::Closed: return "closed";
    case SshSessionState::Disconnected: return "disconnected";
    case SshSessionState::Error: return "error";
    }
    return "unknown";
}

const char* toString(SshSessionError error)
{
    switch (error) {
    case SshSessionError::None: return "none";
    case SshSessionError::DnsResolutionFailed: return "dns_resolution_failed";
    case SshSessionError::ConnectTimeout: return "connect_timeout";
    case SshSessionError::ConnectionRefused: return "connection_refused";
    case SshSessionError::NetworkUnreachable: return "network_unreachable";
    case SshSessionError::AlgorithmNegotiationFailed: return "algorithm_negotiation_failed";
    case SshSessionError::HostKeyMismatch: return "host_key_mismatch";
    case SshSessionError::HandshakeFailed: return "handshake_failed";
    case SshSessionError::HandshakeTimeout: return "handshake_timeout";
    case SshSessionError::RemoteClosed: return "remote_closed";
    case SshSessionError::SocketError: return "socket_error";
    case SshSessionError::InternalError: return "internal_error";
    }
    return "unknown";
}

} // namespace ssh
} // namespace sshclient
