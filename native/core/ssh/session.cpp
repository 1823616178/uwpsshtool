#include "session.h"

#include "channel.h"
#include "debug_log.h"
#include "fwd/direct_tcpip.h"
#include "fwd/local_listener.h"
#include "fwd/remote_listen.h"
#include "fwd/jump_transport.h"
#include "io/SessionThread.h"

#include <libssh2.h>

#include <ws2tcpip.h>

#include <algorithm>
#include <cerrno>
#include <chrono>
#include <cstdio>
#include <mutex>
#include <utility>

#define SSH_LOG(...) sshclient::diagnostics::debugLog("ssh", __VA_ARGS__)

namespace sshclient {
namespace ssh {
namespace {

// F07：跳板模式的轮询驱动间隔（毫秒）。DirectTcpipJumpChannel 无 wakeup 通知，
// 见 beginHandshakeJump 内注释。
constexpr std::uint64_t kJumpPollIntervalMs = 50;

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

// N07: monotonic clock milliseconds (only for the keepalive send-interval
// self-estimate, see doProbeNow).
std::uint64_t steadyNowMs()
{
    return static_cast<std::uint64_t>(
        std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now().time_since_epoch())
            .count());
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

bool SshSession::connectJump(std::shared_ptr<fwd::JumpTransport> transport,
                             std::string host, std::uint16_t port, std::string username)
{
    if (!transport) {
        return false;
    }
    bool expected = false;
    if (!connectAdmitted_.compare_exchange_strong(expected, true)) {
        return false;
    }
    jumpTransport_ = std::move(transport);
    host_ = std::move(host);
    port_ = port;
    username_ = std::move(username);
    thread_.post([this] { doConnectJump(); });
    return true;
}

// ---- F07 jump helpers ------------------------------------------------

// Creates a loopback socket pair (server, client) for event-loop wake-up.
// Returns false and closes both on failure.
static bool MakeLoopbackPair(SOCKET& server, SOCKET& client)
{
    server = INVALID_SOCKET;
    client = INVALID_SOCKET;

    // Bind a TCP listener on 127.0.0.1:0
    SOCKET listener = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (listener == INVALID_SOCKET) {
        return false;
    }
    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = ::htonl(INADDR_LOOPBACK);
    addr.sin_port = 0;
    if (::bind(listener, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) != 0 ||
        ::listen(listener, 1) != 0) {
        ::closesocket(listener);
        return false;
    }
    int addrLen = sizeof(addr);
    if (::getsockname(listener, reinterpret_cast<sockaddr*>(&addr), &addrLen) != 0) {
        ::closesocket(listener);
        return false;
    }

    // Non-blocking client connect
    client = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (client == INVALID_SOCKET) {
        ::closesocket(listener);
        return false;
    }
    // Make client non-blocking temporarily so connect doesn't block
    u_long nb = 1;
    ::ioctlsocket(client, FIONBIO, &nb);
    ::connect(client, reinterpret_cast<sockaddr*>(&addr), sizeof(addr));

    // Accept the server side (listener is blocking → completes immediately after client connect)
    server = ::accept(listener, nullptr, nullptr);
    ::closesocket(listener);
    if (server == INVALID_SOCKET) {
        ::closesocket(client);
        client = INVALID_SOCKET;
        return false;
    }
    // Make both non-blocking
    ::ioctlsocket(server, FIONBIO, &nb);
    return true;
}

void SshSession::doConnectJump()
{
    if (!transitionTo(SshSessionState::Connecting)) {
        return;
    }
    if (!g_libssh2InitSucceeded) {
        fail(SshSessionError::InternalError, "libssh2_init failed");
        return;
    }
    // Skip DNS and TCP socket creation — jump transport provides the channel.
    // Transition directly to handshake using the jump callbacks.
    beginHandshakeJump();
}

void SshSession::beginHandshakeJump()
{
    // Create loopback wake-up socket pair: the BufferedJumpPipe wakeup
    // callback writes a byte to jumpWakePeer_; the event loop monitors
    // jumpWakeSock_ and calls onSocketEvent → driveHandshake / driveAuth /
    // driveChannels as appropriate.
    if (!MakeLoopbackPair(jumpWakeSock_, jumpWakePeer_)) {
        fail(SshSessionError::InternalError, "failed to create jump wake-up socket pair");
        return;
    }

    // Wire the jump transport wakeup to signal jumpWakePeer_.
    jumpTransport_->setWakeupCallback([peer = jumpWakePeer_] {
        char byte = 1;
        ::send(peer, &byte, 1, 0);
    });

    session_ = ::libssh2_session_init_ex(nullptr, nullptr, nullptr, this);
    if (session_ == nullptr) {
        fail(SshSessionError::InternalError, "libssh2_session_init failed (jump)");
        return;
    }
    // Register jump transport send/recv callbacks so libssh2 uses our channel.
    ::libssh2_session_callback_set2(session_, LIBSSH2_CALLBACK_SEND,
        reinterpret_cast<libssh2_cb_generic*>(jumpSendCb));
    ::libssh2_session_callback_set2(session_, LIBSSH2_CALLBACK_RECV,
        reinterpret_cast<libssh2_cb_generic*>(jumpRecvCb));
    ::libssh2_session_set_blocking(session_, 0);

    transitionTo(SshSessionState::Handshaking);
    handshakeTimer_ = thread_.loop().runAfter(options_.handshakeTimeoutMs, [this] {
        if (state() == SshSessionState::Handshaking) {
            fail(SshSessionError::HandshakeTimeout, "SSH handshake timed out (jump)");
        }
    });

    // Register the wake-up socket with the event loop. Use jumpWakeSock_ as the
    // socket_ alias so that the standard onSocketEvent path is reused.
    // We store it in socket_ so that addSocket / updateSocketInterest / removeSocket
    // all use the same variable. jumpWakePeer_ is purely the write-end.
    socket_ = jumpWakeSock_;
    if (!thread_.loop().addSocket(socket_, io::EventLoop::Readable,
                                  [this](SOCKET socket, short events) {
                                      // Drain any wakeup bytes, then drive.
                                      char buf[64];
                                      while (::recv(socket, buf, sizeof(buf), 0) > 0) {}
                                      onSocketEvent(socket, events | io::EventLoop::Readable);
                                  })) {
        fail(SshSessionError::InternalError, "failed to register jump wake socket with event loop");
        return;
    }
    socketRegistered_ = true;

    // F07：DirectTcpipJumpChannel 没有 wakeup 通知路径（JumpTransport::
    // setWakeupCallback 仅对 BufferedJumpPipe 生效），上级通道的数据到达无法
    // 唤醒本会话的事件循环。用轮询兜底周期驱动状态机：recv 无数据时 EAGAIN
    // 空转，代价可接受。真机验收通过后如需省电可再接 hop 会话的 I/O 进度回调。
    jumpPollTimer_ = thread_.loop().runEvery(kJumpPollIntervalMs, [this] {
        const SshSessionState current = state();
        if (current != SshSessionState::Handshaking &&
            current != SshSessionState::Authenticating &&
            current != SshSessionState::Established) {
            return;
        }
        if (jumpWakeSock_ == INVALID_SOCKET) {
            return;
        }
        char buf[64];
        while (::recv(jumpWakeSock_, buf, sizeof(buf), 0) > 0) {}
        onSocketEvent(jumpWakeSock_, io::EventLoop::Readable);
    });

    // Drive the first handshake attempt immediately (may complete with data
    // already in the pipe from upstream).
    driveHandshake();
}

SSIZE_T SshSession::jumpSendCb(SOCKET /*sock*/, const void* buffer,
                                size_t length, int flags, void** abstract)
{
    if (!abstract || !*abstract) { return static_cast<SSIZE_T>(-ECONNRESET); }
    auto* self = static_cast<SshSession*>(*abstract);
    if (!self->jumpTransport_) { return static_cast<SSIZE_T>(-ECONNRESET); }
    return self->jumpTransport_->send(buffer, length, flags);
}

SSIZE_T SshSession::jumpRecvCb(SOCKET /*sock*/, void* buffer,
                                size_t length, int flags, void** abstract)
{
    if (!abstract || !*abstract) { return static_cast<SSIZE_T>(-ECONNRESET); }
    auto* self = static_cast<SshSession*>(*abstract);
    if (!self->jumpTransport_) { return static_cast<SSIZE_T>(-ECONNRESET); }
    return self->jumpTransport_->recv(buffer, length, flags);
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

bool SshSession::setKeepaliveConfig(std::uint32_t intervalSec, std::uint32_t maxMisses)
{
    // Only Idle admits: the parameters are assembled once in armKeepalive when
    // entering Established (libssh2_keepalive_config + timer); changing them
    // after connect is meaningless — the caller's contract is pre-connect.
    if (state() != SshSessionState::Idle) {
        return false;
    }
    options_.keepaliveIntervalSec = intervalSec;
    options_.keepaliveMaxMisses = maxMisses;
    // The tracker only carries the threshold; rebuilding resets it (Idle has
    // no loop-thread activity yet, no concurrency).
    keepaliveMissTracker_ = KeepaliveMissTracker(maxMisses);
    return true;
}

bool SshSession::probeNow(std::uint32_t timeoutSec)
{
    // Only Established admits: other states either have no connection to probe
    // or the disconnect/reconnect chain is already running.
    if (state() != SshSessionState::Established) {
        return false;
    }
    const std::uint32_t timeout =
        timeoutSec == 0 ? kDefaultKeepaliveProbeTimeoutSec : timeoutSec;
    thread_.post([this, timeout] { doProbeNow(timeout); });
    return true;
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
        // N07: inbound-activity observation (signal 1 of the keepalive
        // blackhole judgement, see keepalive.h). Only meaningful across
        // Established periods; marking it in Authenticating too is harmless.
        if ((events & io::EventLoop::Readable) != 0) {
            keepaliveInboundSeen_ = true;
        }
        // N05: an in-flight auth operation re-arms per libssh2's declared
        // block direction (see updateSocketInterest); the event drives it.
        if (state() == SshSessionState::Authenticating && hasAuthPending()) {
            if (authMethodsCallback_) {
                driveAuthMethodsQuery();
            } else {
                driveAuth();
            }
            return;
        }
        // N06: socket events re-drive the channel pumps (EAGAIN continuations,
        // inbound data, close handshakes).
        if (!channels_.empty() &&
            (events & (io::EventLoop::Readable | io::EventLoop::Writable)) != 0) {
            driveChannels();
        }
        // F04: same drive for forwarded streams (direct-tcpip EAGAIN
        // continuations, pump passes, close handshakes) and remote accept
        // polling. Kept separate from driveChannels: different registries,
        // same event gate.
        if ((!fwdStreams_.empty() || !fwdRemoteListeners_.empty()) &&
            (events & (io::EventLoop::Readable | io::EventLoop::Writable)) != 0) {
            driveForwarded();
        }
        if (state() == SshSessionState::Established ||
            state() == SshSessionState::Authenticating) {
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
    if (state() == SshSessionState::Established) {
        // N06: channels consume inbound data on demand; keep Readable armed so
        // their pumps run. Writable only while a channel declared an outbound
        // stall (a connected socket is virtually always writable and WSAPoll
        // is level-triggered — arming it unconditionally would spin the loop).
        events |= io::EventLoop::Readable;
        for (SshChannel* channel : channels_) {
            if (channel->wantsOutboundBlocked()) {
                events |= io::EventLoop::Writable;
                break;
            }
        }
        // F04: forwarded streams declare outbound stalls the same way (a
        // channel write EAGAINed while pump bytes are pending, or an open /
        // close handshake waiting on the outbound direction).
        for (fwd::ForwardedConnection* stream : fwdStreams_) {
            if (stream->wantsOutboundBlocked()) {
                events |= io::EventLoop::Writable;
                break;
            }
        }
    }
    if (events == 0) {
        // Nothing blocked (e.g. idle Authenticating/Established): arm read-only.
        // Arming Writable here would spin the loop — a connected socket is
        // virtually always writable and WSAPoll is level-triggered.
        events = io::EventLoop::Readable;
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
    if (to == SshSessionState::Established) {
        armKeepalive(); // N07: assemble keepalive (interval 0 = no-op inside)
    }
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
    // N05: clear auth state first — disarms the auth timers and wipes the
    // in-memory credential copies (auth.cpp); must precede session_ free
    // because the auth context references it.
    clearAuthState();
    // N06: force-clean every registered channel before the libssh2 session
    // goes away; a channel free that EAGAINs is covered by the session_free
    // retry below (channel.cpp onSessionLost).
    notifyChannelsSessionLost();
    // F04: force-clean forwarded streams and listeners the same way. Streams
    // first (their onClose erases them from the owning listeners), then the
    // listeners themselves (drop the remote bind; the server side releases
    // when the session dies).
    notifyForwardedSessionLost();
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
        jumpWakeSock_ = INVALID_SOCKET; // socket_ == jumpWakeSock_ in jump mode
    }
    // F07: close the write-end of the jump wake-up pair and release transport.
    if (jumpWakePeer_ != INVALID_SOCKET) {
        ::closesocket(jumpWakePeer_);
        jumpWakePeer_ = INVALID_SOCKET;
    }
    jumpTransport_.reset();
}

void SshSession::cancelTimers()
{
    for (io::EventLoop::TimerId* timer :
         {&connectTimer_, &handshakeTimer_, &closeTimer_, &keepaliveTimer_, &jumpPollTimer_}) {
        if (*timer != 0) {
            thread_.loop().cancelTimer(*timer);
            *timer = 0;
        }
    }
}

// ---------------------------------------------------------------- N06 channel registry (loop thread)

void SshSession::registerChannel(SshChannel* channel)
{
    channels_.push_back(channel);
}

void SshSession::unregisterChannel(SshChannel* channel)
{
    channels_.erase(std::remove(channels_.begin(), channels_.end(), channel),
                    channels_.end());
}

void SshSession::driveChannels()
{
    // pump() may unregister a finished channel and mutate channels_; walk a
    // snapshot and skip entries that unregistered mid-round.
    const std::vector<SshChannel*> snapshot = channels_;
    for (SshChannel* channel : snapshot) {
        if (std::find(channels_.begin(), channels_.end(), channel) != channels_.end()) {
            channel->pump();
        }
    }
    // Re-arm per the fresh block directions (a stall may have moved between
    // inbound/outbound during the pumps).
    if (socketRegistered_ && state() == SshSessionState::Established) {
        updateSocketInterest();
    }
}

void SshSession::notifyChannelsSessionLost()
{
    // onSessionLost() does not unregister (the whole table is dropped here).
    const std::vector<SshChannel*> snapshot = channels_;
    for (SshChannel* channel : snapshot) {
        channel->onSessionLost();
    }
    channels_.clear();
}

// ---------------------------------------------------------------- F04 forwarding registry (loop thread)

void SshSession::registerForwarded(fwd::ForwardedConnection* connection)
{
    fwdStreams_.push_back(connection);
}

void SshSession::unregisterForwarded(fwd::ForwardedConnection* connection)
{
    fwdStreams_.erase(std::remove(fwdStreams_.begin(), fwdStreams_.end(), connection),
                      fwdStreams_.end());
    if (fwdChannelOpening_ == connection) {
        fwdChannelOpening_ = nullptr;
    }
}

void SshSession::registerRemoteListener(fwd::RemoteListener* listener)
{
    fwdRemoteListeners_.push_back(listener);
}

void SshSession::unregisterRemoteListener(fwd::RemoteListener* listener)
{
    fwdRemoteListeners_.erase(
        std::remove(fwdRemoteListeners_.begin(), fwdRemoteListeners_.end(), listener),
        fwdRemoteListeners_.end());
    if (fwdListenOpening_ == listener) {
        fwdListenOpening_ = nullptr;
    }
}

void SshSession::registerLocalListener(fwd::LocalListener* listener)
{
    fwdLocalListeners_.push_back(listener);
}

void SshSession::unregisterLocalListener(fwd::LocalListener* listener)
{
    fwdLocalListeners_.erase(
        std::remove(fwdLocalListeners_.begin(), fwdLocalListeners_.end(), listener),
        fwdLocalListeners_.end());
}

void SshSession::driveForwarded()
{
    // Accept polling first (new arrivals), then every stream's pump. Both may
    // unregister finished entries mid-round: walk snapshots and skip entries
    // that unregistered meanwhile (same convention as driveChannels).
    const std::vector<fwd::RemoteListener*> listenerSnapshot = fwdRemoteListeners_;
    for (fwd::RemoteListener* listener : listenerSnapshot) {
        if (std::find(fwdRemoteListeners_.begin(), fwdRemoteListeners_.end(), listener) !=
            fwdRemoteListeners_.end()) {
            listener->drive();
        }
    }
    const std::vector<fwd::ForwardedConnection*> streamSnapshot = fwdStreams_;
    for (fwd::ForwardedConnection* stream : streamSnapshot) {
        if (std::find(fwdStreams_.begin(), fwdStreams_.end(), stream) != fwdStreams_.end()) {
            stream->pump();
        }
    }
    // Re-arm per the fresh block directions (a stall may have moved between
    // inbound/outbound during the pumps).
    if (socketRegistered_ && state() == SshSessionState::Established) {
        updateSocketInterest();
    }
}

void SshSession::notifyForwardedSessionLost()
{
    // onSessionLost() does not unregister (both tables are dropped here).
    // Streams first: their onClose erases them from the owning listeners, so
    // the listeners see an empty set when their own onSessionLost runs.
    const std::vector<fwd::ForwardedConnection*> streamSnapshot = fwdStreams_;
    for (fwd::ForwardedConnection* stream : streamSnapshot) {
        stream->onSessionLost();
    }
    fwdStreams_.clear();
    fwdChannelOpening_ = nullptr;
    const std::vector<fwd::RemoteListener*> listenerSnapshot = fwdRemoteListeners_;
    for (fwd::RemoteListener* listener : listenerSnapshot) {
        listener->onSessionLost();
    }
    fwdRemoteListeners_.clear();
    fwdListenOpening_ = nullptr;
    // Local listeners self-drive (no polling); they only need the socket
    // cleanup + Stopped transition. Their connections were force-cleaned
    // above and erased themselves via onClose.
    const std::vector<fwd::LocalListener*> localSnapshot = fwdLocalListeners_;
    for (fwd::LocalListener* listener : localSnapshot) {
        listener->onSessionLost();
    }
    fwdLocalListeners_.clear();
}

bool SshSession::tryAcquireFwdChannelOpen(fwd::ForwardedConnection* owner)
{
    if (fwdChannelOpening_ != nullptr && fwdChannelOpening_ != owner) {
        return false; // another stream owns the open machine: wait
    }
    fwdChannelOpening_ = owner;
    return true;
}

void SshSession::releaseFwdChannelOpen(fwd::ForwardedConnection* owner)
{
    if (fwdChannelOpening_ == owner) {
        fwdChannelOpening_ = nullptr;
    }
}

bool SshSession::tryAcquireFwdListenOpen(fwd::RemoteListener* owner)
{
    if (fwdListenOpening_ != nullptr && fwdListenOpening_ != owner) {
        return false; // another listener owns the listen machine: wait
    }
    fwdListenOpening_ = owner;
    return true;
}

void SshSession::releaseFwdListenOpen(fwd::RemoteListener* owner)
{
    if (fwdListenOpening_ == owner) {
        fwdListenOpening_ = nullptr;
    }
}

std::string SshSession::lastLibssh2ErrorForFwd()
{
    char* errmsg = nullptr;
    int errmsgLen = 0;
    ::libssh2_session_last_error(session_, &errmsg, &errmsgLen, 0);
    return errmsg != nullptr ? std::string(errmsg, static_cast<size_t>(errmsgLen))
                             : std::string("unknown error");
}

// ---------------------------------------------------------------- N07 keepalive (loop thread)

void SshSession::armKeepalive()
{
    // Hook on entering Established (called from transitionTo); interval 0 =
    // disabled, return right away.
    if (options_.keepaliveIntervalSec == 0 || session_ == nullptr) {
        return;
    }
    // want_reply=1: SSH_MSG_GLOBAL_REQUEST demanding an answer (OpenSSH
    // replies SSH_MSG_REQUEST_SUCCESS). libssh2 does no unanswered counting;
    // the count/verdict lives in onKeepaliveTick (semantics in keepalive.h).
    // Note libssh2 promotes interval=1 to 2 (1.11.1 keepalive.c anti-spin
    // clause) — this layer's period timing is unaffected; after the first
    // tick we re-arm per seconds_to_next.
    ::libssh2_keepalive_config(session_, 1, options_.keepaliveIntervalSec);

    keepaliveMissTracker_ = KeepaliveMissTracker(options_.keepaliveMaxMisses);
    keepaliveSendCount_.store(0, std::memory_order_release);
    keepaliveMissCount_.store(0, std::memory_order_release);
    keepaliveProbeCount_.store(0, std::memory_order_release);
    keepaliveProbe_.disarm();
    keepaliveLastSentMs_ = 0;
    // First-period grace: reaching Established at all (the auth success reply)
    // is inbound evidence of a live peer; also record the FIONREAD baseline so
    // handshake/auth leftover bytes are not mistaken for "new inbound".
    keepaliveInboundSeen_ = true;
    u_long pending = 0;
    if (::ioctlsocket(socket_, FIONREAD, &pending) == 0) {
        keepalivePendingBaseline_ = static_cast<long>(pending);
    }

    keepaliveTimer_ = thread_.loop().runAfter(
        static_cast<std::uint64_t>(options_.keepaliveIntervalSec) * 1000,
        [this] { onKeepaliveTick(); });
}

void SshSession::onKeepaliveTick()
{
    keepaliveTimer_ = 0; // this tick's timer fired and is consumed; re-armed below as needed
    if (state() != SshSessionState::Established || session_ == nullptr ||
        socket_ == INVALID_SOCKET) {
        return; // left Established (close/disconnect teardown): the chain ends here
    }

    // Observe the last period's inbound activity: Readable event flag OR
    // pending-byte growth (semantics and approximation in keepalive.h); reset
    // right after observing, entering the next period.
    u_long pending = 0;
    if (::ioctlsocket(socket_, FIONREAD, &pending) != 0) {
        pending = static_cast<u_long>(keepalivePendingBaseline_); // query failed: treat as
                                                                  // "no growth"; a truly dead
                                                                  // socket is caught by fd events
    }
    const bool hadInbound = keepaliveInboundObserved(keepaliveInboundSeen_,
                                                     static_cast<long>(pending),
                                                     keepalivePendingBaseline_);
    keepaliveInboundSeen_ = false;
    keepalivePendingBaseline_ = static_cast<long>(pending);

    if (keepaliveMissTracker_.tick(hadInbound)) {
        keepaliveMissCount_.store(keepaliveMissTracker_.misses(), std::memory_order_release);
        peerDisconnected(SshSessionError::KeepaliveTimeout,
                         "keepalive silent blackhole: no inbound data for " +
                             std::to_string(options_.keepaliveMaxMisses) +
                             " consecutive periods");
        return;
    }
    keepaliveMissCount_.store(keepaliveMissTracker_.misses(), std::memory_order_release);

    // Send this tick's keepalive. libssh2 semantics (verified against 1.11.1
    // keepalive.c): it decides whether to actually emit a packet by
    // keepalive_last_sent + interval; EAGAIN is swallowed into a 0 return
    // (send buffer full -> pretends sent; the missing reply counts as a miss
    // this period, self-consistent); a non-zero return = the socket write is
    // broken (LIBSSH2_ERROR_SOCKET_SEND) -> judge a blackhole directly.
    int secondsToNext = static_cast<int>(options_.keepaliveIntervalSec);
    const int rc = ::libssh2_keepalive_send(session_, &secondsToNext);
    if (rc != 0) {
        peerDisconnected(SshSessionError::KeepaliveTimeout,
                         "keepalive send failed (socket write error, libssh2 rc=" +
                             std::to_string(rc) + ")");
        return;
    }
    keepaliveSendCount_.fetch_add(1, std::memory_order_acq_rel);
    // This tick's send time (approximation: if libssh2 skipped the send due to
    // last_sent + interval > now, this records slightly early. Only used by
    // doProbeNow to decide whether a forced send would be gated; conservative,
    // harmless).
    keepaliveLastSentMs_ = steadyNowMs();

    // Re-arm per libssh2's seconds_to_next (the recommended usage) instead of
    // a fixed period: with time()'s 1 s truncation a fixed period can make
    // libssh2 occasionally skip a send, and the resulting "no request in
    // flight" window would record a spurious miss; re-arming per s2n
    // guarantees every tick actually emits.
    const std::uint64_t delayMs =
        static_cast<std::uint64_t>(secondsToNext > 0 ? secondsToNext
                                                     : options_.keepaliveIntervalSec) *
        1000;
    keepaliveTimer_ = thread_.loop().runAfter(delayMs, [this] { onKeepaliveTick(); });
}

// ---------------------------------------------------------------- N07 active probe (loop thread)

void SshSession::doProbeNow(std::uint32_t timeoutSec)
{
    if (state() != SshSessionState::Established || session_ == nullptr ||
        socket_ == INVALID_SOCKET) {
        return; // left Established between admission and execution: the
                // disconnect/reconnect chain takes over; the probe is void
    }
    if (keepaliveProbe_.active()) {
        return; // a window is already running (dedup when network events arrive
                // in bursts); do not re-arm or reset the baseline
    }

    // libssh2_keepalive_send gates the actual send on last_sent + interval <=
    // now (verified against 1.11.1 keepalive.c): under a normal 30 s period a
    // probe right after a tick would be silently skipped and degrade to
    // "asked nothing but waited for a reply". So press the interval down to
    // libssh2's minimum to force the send, then restore the original config
    // right away (restoring to 0 keeps periodic keepalive off).
    const std::uint64_t now = steadyNowMs();
    const bool willSend =
        keepaliveLastSentMs_ == 0 ||
        now - keepaliveLastSentMs_ >=
            static_cast<std::uint64_t>(kLibssh2MinKeepaliveIntervalSec) * 1000;

    u_long pending = 0;
    if (::ioctlsocket(socket_, FIONREAD, &pending) != 0) {
        pending = static_cast<u_long>(keepalivePendingBaseline_);
    }
    if (willSend) {
        // Really sending: the window starts now; earlier inbound evidence is
        // unrelated to this probe.
        keepaliveInboundSeen_ = false;
        keepalivePendingBaseline_ = static_cast<long>(pending);
        keepaliveProbe_.arm(static_cast<long>(pending));
    } else {
        // Cannot send (just sent): the previous tick's reply is likely in
        // flight or just landed — keep the old baseline and the inbound flag
        // already observed, or that liveness evidence is erased and
        // misjudged (see KeepaliveProbe::arm).
        keepaliveProbe_.arm(keepalivePendingBaseline_);
    }

    ::libssh2_keepalive_config(session_, 1, kLibssh2MinKeepaliveIntervalSec);
    int secondsToNext = 0;
    const int rc = ::libssh2_keepalive_send(session_, &secondsToNext);
    ::libssh2_keepalive_config(session_, 1, options_.keepaliveIntervalSec);
    if (rc != 0) {
        keepaliveProbe_.disarm();
        peerDisconnected(SshSessionError::KeepaliveTimeout,
                         "network-change probe send failed (socket write error, libssh2 rc=" +
                             std::to_string(rc) + ")");
        return;
    }
    if (willSend) {
        keepaliveLastSentMs_ = now;
        keepaliveSendCount_.fetch_add(1, std::memory_order_acq_rel);
    }
    keepaliveProbeCount_.fetch_add(1, std::memory_order_acq_rel);

    // The deadline timer reuses the keepaliveTimer_ slot: the periodic chain
    // yields during the window and onProbeDeadline resumes it (cancelTimers
    // covers this timer on disconnect/close, so it is never leaked).
    if (keepaliveTimer_ != 0) {
        thread_.loop().cancelTimer(keepaliveTimer_);
        keepaliveTimer_ = 0;
    }
    SSH_LOG("active probe sent (%u s verdict window)", timeoutSec);
    keepaliveTimer_ = thread_.loop().runAfter(static_cast<std::uint64_t>(timeoutSec) * 1000,
                                              [this, timeoutSec] { onProbeDeadline(timeoutSec); });
}

void SshSession::onProbeDeadline(std::uint32_t timeoutSec)
{
    keepaliveTimer_ = 0; // this window's timer fired and is consumed
    if (state() != SshSessionState::Established || session_ == nullptr ||
        socket_ == INVALID_SOCKET) {
        keepaliveProbe_.disarm();
        return; // disconnected/closed inside the window: verdict and resume are moot
    }

    u_long pending = 0;
    if (::ioctlsocket(socket_, FIONREAD, &pending) != 0) {
        pending = static_cast<u_long>(keepaliveProbe_.baseline()); // query failed: "no growth"
    }
    const bool alive =
        keepaliveProbe_.verdictAlive(keepaliveInboundSeen_, static_cast<long>(pending));
    keepaliveProbe_.disarm();

    if (!alive) {
        peerDisconnected(SshSessionError::KeepaliveTimeout,
                         "network-change probe unanswered: no inbound data within " +
                             std::to_string(timeoutSec) + " s");
        return;
    }

    // Alive: this probe's reply is itself a successful inbound observation —
    // reset the miss counter; resume the periodic chain at the original
    // interval.
    keepaliveMissTracker_.reset();
    keepaliveMissCount_.store(0, std::memory_order_release);
    keepaliveInboundSeen_ = false;
    keepalivePendingBaseline_ = static_cast<long>(pending);
    if (options_.keepaliveIntervalSec == 0) {
        return; // periodic keepalive was off: this probe was one-shot, do not
                // invent a periodic chain
    }
    keepaliveTimer_ = thread_.loop().runAfter(
        static_cast<std::uint64_t>(options_.keepaliveIntervalSec) * 1000,
        [this] { onKeepaliveTick(); });
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
    case SshSessionError::AuthFailedPassword: return "auth_failed_password";
    case SshSessionError::AuthFailedKey: return "auth_failed_key";
    case SshSessionError::AuthFailedInteractive: return "auth_failed_interactive";
    case SshSessionError::AuthFailedPassphrase: return "auth_failed_passphrase";
    case SshSessionError::AuthTimeout: return "auth_timeout";
    case SshSessionError::NoLocalCredential: return "no_local_credential";
    case SshSessionError::AlgorithmNegotiationFailed: return "algorithm_negotiation_failed";
    case SshSessionError::HostKeyMismatch: return "host_key_mismatch";
    case SshSessionError::HandshakeFailed: return "handshake_failed";
    case SshSessionError::HandshakeTimeout: return "handshake_timeout";
    case SshSessionError::RemoteClosed: return "remote_closed";
    case SshSessionError::SocketError: return "socket_error";
    case SshSessionError::KeepaliveTimeout: return "keepalive_timeout";
    case SshSessionError::InternalError: return "internal_error";
    }
    return "unknown";
}

bool isAutoReconnectable(SshSessionState terminalState, SshSessionError error)
{
    switch (terminalState) {
    case SshSessionState::Disconnected:
        // Peer close / socket error / keepalive blackhole: all may be
        // transient network trouble.
        return true;
    case SshSessionState::Error:
        switch (error) {
        case SshSessionError::DnsResolutionFailed:
        case SshSessionError::ConnectTimeout:
        case SshSessionError::ConnectionRefused:
        case SshSessionError::NetworkUnreachable: // mostly transient
        case SshSessionError::HandshakeFailed:
        case SshSessionError::HandshakeTimeout:
        case SshSessionError::SocketError:
        case SshSessionError::AuthTimeout: // peer silent during auth: link-class
            return true;
        default:
            // Credential class (201-206), algorithm negotiation, host key
            // mismatch, internal errors: reconnecting does not help.
            return false;
        }
    default:
        return false;
    }
}

} // namespace ssh
} // namespace sshclient
