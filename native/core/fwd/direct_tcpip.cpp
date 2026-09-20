#include "fwd/direct_tcpip.h"

#include "debug_log.h"
#include "io/SessionThread.h"
#include "ssh/session.h"

#include <libssh2.h>

#include <ws2tcpip.h>

#include <chrono>
#include <cstdio>
#include <utility>

#define FWD_LOG(...) sshclient::diagnostics::debugLog("fwd", __VA_ARGS__)

namespace sshclient {
namespace fwd {
namespace {

// Remote-half target connect timeout: mirrors SshSessionOptions defaults.
constexpr std::uint32_t kTargetConnectTimeoutMs = 15000;
// Bounded channel close-handshake drive (each pass is one libssh2 call;
// passes arrive on socket events, so this bounds work, not wall time —the
// session-loss path force-cleans regardless).
constexpr unsigned kMaxCloseAttempts = 200;

std::uint64_t steadyNowMs()
{
    return static_cast<std::uint64_t>(
        std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now().time_since_epoch())
            .count());
}

bool isConnectInProgress(int error)
{
    return error == WSAEWOULDBLOCK || error == WSAEINPROGRESS || error == WSAEALREADY;
}

void configureAcceptedSocket(SOCKET socket)
{
    const BOOL enabled = TRUE;
    ::setsockopt(socket, IPPROTO_TCP, TCP_NODELAY,
                 reinterpret_cast<const char*>(&enabled), sizeof(enabled));
}

} // namespace

// ---------------------------------------------------------------- endpoints (loop thread)

class ForwardedConnection::SocketEndpoint final : public IPumpEndpoint {
public:
    explicit SocketEndpoint(ForwardedConnection& owner) : owner_(owner) {}

    PumpReadResult read(size_t maxLen) override
    {
        if (owner_.socket_ == INVALID_SOCKET) {
            return PumpReadResult::stalled();
        }
        // Cap single pulls at the pump chunk size (the pump already bounds by
        // maxLen; this is belt-and-braces against a misbehaving caller).
        char buffer[BidirectionalPump::kReadChunkBytes];
        const size_t want = maxLen < sizeof(buffer) ? maxLen : sizeof(buffer);
        const int received =
            ::recv(owner_.socket_, buffer, static_cast<int>(want), 0);
        if (received > 0) {
            return PumpReadResult::data(
                std::string(buffer, static_cast<size_t>(received)));
        }
        if (received == 0) {
            return PumpReadResult::eof(); // peer FIN (graceful half-close)
        }
        const int error = ::WSAGetLastError();
        if (error == WSAEWOULDBLOCK) {
            return PumpReadResult::stalled();
        }
        char message[96]{};
        ::sprintf_s(message, sizeof(message), "socket recv failed (WSA %d)", error);
        return PumpReadResult::error(message);
    }

    PumpWriteResult write(const char* data, size_t len) override
    {
        if (owner_.socket_ == INVALID_SOCKET || data == nullptr || len == 0) {
            return PumpWriteResult::stalled();
        }
        const int sent =
            ::send(owner_.socket_, data, static_cast<int>(len), 0);
        if (sent > 0) {
            return PumpWriteResult::data(static_cast<size_t>(sent));
        }
        if (sent == 0) {
            return PumpWriteResult::stalled(); // no progress: retry on writable
        }
        const int error = ::WSAGetLastError();
        if (error == WSAEWOULDBLOCK) {
            return PumpWriteResult::stalled();
        }
        char message[96]{};
        ::sprintf_s(message, sizeof(message), "socket send failed (WSA %d)", error);
        return PumpWriteResult::error(message);
    }

    EofSendResult sendEof() override
    {
        if (owner_.socket_ == INVALID_SOCKET) {
            return EofSendResult::GiveUp;
        }
        // Best effort: even a failure means the peer is gone, and completion
        // proceeds to the full close either way.
        if (::shutdown(owner_.socket_, SD_SEND) != 0) {
            FWD_LOG("socket shutdown(SD_SEND) failed (WSA %d, best effort)",
                    ::WSAGetLastError());
        }
        return EofSendResult::Done;
    }

private:
    ForwardedConnection& owner_;
};

class ForwardedConnection::ChannelEndpoint final : public IPumpEndpoint {
public:
    explicit ChannelEndpoint(ForwardedConnection& owner) : owner_(owner) {}

    PumpReadResult read(size_t maxLen) override
    {
        if (owner_.channel_ == nullptr) {
            return PumpReadResult::stalled(); // channel not open yet
        }
        char buffer[BidirectionalPump::kReadChunkBytes];
        const size_t want = maxLen < sizeof(buffer) ? maxLen : sizeof(buffer);
        const ssize_t result = ::libssh2_channel_read_ex(owner_.channel_, 0, buffer,
                                                         want);
        if (result > 0) {
            return PumpReadResult::data(
                std::string(buffer, static_cast<size_t>(result)));
        }
        if (result == 0) {
            return PumpReadResult::eof();
        }
        if (result == LIBSSH2_ERROR_EAGAIN) {
            // An abrupt peer close sets EOF inside libssh2 without a final
            // 0-read (same observation as SshChannel::drainReads).
            if (::libssh2_channel_eof(owner_.channel_) != 0) {
                return PumpReadResult::eof();
            }
            return PumpReadResult::stalled();
        }
        return PumpReadResult::error("channel read failed: " + owner_.lastLibssh2Error());
    }

    PumpWriteResult write(const char* data, size_t len) override
    {
        if (owner_.channel_ == nullptr || data == nullptr || len == 0) {
            return PumpWriteResult::stalled();
        }
        const ssize_t result =
            ::libssh2_channel_write_ex(owner_.channel_, 0, data, len);
        if (result > 0) {
            return PumpWriteResult::data(static_cast<size_t>(result));
        }
        if (result == LIBSSH2_ERROR_EAGAIN) {
            return PumpWriteResult::stalled();
        }
        return PumpWriteResult::error("channel write failed: " + owner_.lastLibssh2Error());
    }

    EofSendResult sendEof() override
    {
        if (owner_.channel_ == nullptr) {
            return EofSendResult::GiveUp;
        }
        const int rc = ::libssh2_channel_send_eof(owner_.channel_);
        if (rc == 0) {
            return EofSendResult::Done;
        }
        if (rc == LIBSSH2_ERROR_EAGAIN) {
            return EofSendResult::RetryLater;
        }
        // A failed EOF send is not fatal (the peer is likely broken anyway):
        // give up; the close handshake still runs at teardown.
        FWD_LOG("channel send_eof failed (best effort): %s",
                owner_.lastLibssh2Error().c_str());
        return EofSendResult::GiveUp;
    }

private:
    ForwardedConnection& owner_;
};

// ---------------------------------------------------------------- lifecycle

ForwardedConnection::ForwardedConnection(ssh::SshSession& session, SOCKET accepted,
                                         std::shared_ptr<TunnelStats> stats,
                                         ForwardedCallbacks callbacks)
    : session_(session)
    , callbacks_(std::move(callbacks))
    , stats_(std::move(stats))
    , half_(Half::Local)
    , socket_(accepted)
{
    if (socket_ != INVALID_SOCKET) {
        configureAcceptedSocket(socket_);
    }
}

ForwardedConnection::ForwardedConnection(ssh::SshSession& session,
                                         struct _LIBSSH2_CHANNEL* accepted,
                                         std::shared_ptr<TunnelStats> stats,
                                         ForwardedCallbacks callbacks)
    : session_(session)
    , callbacks_(std::move(callbacks))
    , stats_(std::move(stats))
    , half_(Half::Remote)
    , channel_(accepted)
{
}

ForwardedConnection::~ForwardedConnection()
{
    // An Idle connection never registered its socket on the EventLoop, so
    // closing it here is safe from any thread (the admission-refused path in
    // the listeners relies on this instead of leaking the accepted socket).
    // Anything beyond Idle must have gone through onOpen/onClose first.
    if (state_.load(std::memory_order_acquire) == ForwardState::Idle &&
        socket_ != INVALID_SOCKET) {
        ::closesocket(socket_);
        socket_ = INVALID_SOCKET;
    }
    if (channel_ != nullptr || socket_ != INVALID_SOCKET) {
        FWD_LOG("warning: ForwardedConnection destroyed with live handles "
                "(contract violation, leak)");
    }
    if (connectAddrs_ != nullptr) {
        ::freeaddrinfo(connectAddrs_);
    }
}

void ForwardedConnection::setCloseCallback(
    std::function<void(const ForwardCloseInfo& info)> callback)
{
    callbacks_.onClose = std::move(callback);
}

void ForwardedConnection::setOpenFailureCallback(std::function<void()> callback)
{
    openFailureCallback_ = std::move(callback);
}

// ---------------------------------------------------------------- admission (any thread)

bool ForwardedConnection::openLocal(std::string destHost, std::uint16_t destPort,
                                    std::string srcHost, std::uint16_t srcPort)
{
    if (destHost.empty() || destPort == 0 || socket_ == INVALID_SOCKET) {
        FWD_LOG("openLocal rejected: invalid args or no socket");
        return false;
    }
    return admitOpen(Half::Local, std::move(destHost), destPort, std::move(srcHost),
                     srcPort);
}

bool ForwardedConnection::openDynamic(std::string srcHost, std::uint16_t srcPort)
{
    if (socket_ == INVALID_SOCKET) {
        FWD_LOG("openDynamic rejected: no socket");
        return false;
    }
    // 目标未知（来自客户端的 CONNECT），admit 时 host_/port_ 留空。
    return admitOpen(Half::Local, std::string(), 0, std::move(srcHost), srcPort);
}

bool ForwardedConnection::openRemote(std::string targetHost, std::uint16_t targetPort)
{
    if (targetHost.empty() || targetPort == 0 || channel_ == nullptr) {
        FWD_LOG("openRemote rejected: invalid args or no accepted channel");
        return false;
    }
    return admitOpen(Half::Remote, std::move(targetHost), targetPort, "", 0);
}

bool ForwardedConnection::admitOpen(Half half, std::string host, std::uint16_t port,
                                    std::string srcHost, std::uint16_t srcPort)
{
    if (session_.state() != ssh::SshSessionState::Established) {
        FWD_LOG("forward open rejected: session state %s (only established admits)",
                ssh::toString(session_.state()));
        return false;
    }
    bool expected = false;
    if (!openAdmitted_.compare_exchange_strong(expected, true)) {
        FWD_LOG("forward open rejected: this connection was opened before");
        return false;
    }
    if (!stats_) {
        stats_ = std::make_shared<TunnelStats>(); // defensive: never null downstream
    }
    half_ = half;
    host_ = std::move(host);
    port_ = port;
    srcHost_ = std::move(srcHost);
    srcPort_ = srcPort;
    socksMode_ = half == Half::Local && host_.empty();
    socksParsed_ = false;
    socksLeftover_.clear();
    socksReplyPending_.clear();
    session_.thread_.post([this] { begin(); });
    return true;
}

void ForwardedConnection::close()
{
    if (!openAdmitted_.load(std::memory_order_acquire)) {
        return; // never opened: nothing to close (and no task posted, per contract)
    }
    bool expected = false;
    if (!closeRequested_.compare_exchange_strong(expected, true)) {
        return; // idempotent
    }
    session_.thread_.post([this] {
        const ForwardState state = state_.load(std::memory_order_acquire);
        if (state == ForwardState::Closed) {
            return;
        }
        if (state == ForwardState::Pumping) {
            enterClosing(ForwardCloseReason::LocalClose, "local close requested");
            return;
        }
        // Opening/Closing: the flag is honored when the open completes (or
        // the close handshake finishes); nothing to drive right now beyond a
        // pump nudge.
        pump();
        session_.updateSocketInterest();
    });
}

// ---------------------------------------------------------------- state machine (static pure)

bool ForwardedConnection::isLegalTransition(ForwardState from, ForwardState to)
{
    using S = ForwardState;
    switch (from) {
    case S::Idle:
        return to == S::Opening;
    case S::Opening:
        return to == S::Pumping || to == S::Closing || to == S::Closed;
    case S::Pumping:
        return to == S::Closing || to == S::Closed;
    case S::Closing:
        return to == S::Closed;
    case S::Closed:
        return false;
    }
    return false;
}

// ---------------------------------------------------------------- assembly and master pump (loop thread)

void ForwardedConnection::begin()
{
    transitionTo(ForwardState::Opening);
    if (session_.state() != ssh::SshSessionState::Established ||
        session_.session_ == nullptr) {
        // The session raced away after admission: terminate as an onOpen
        // failure (never registered).
        failOpen(ForwardOpenError::NotEstablished, "session left the established state");
        return;
    }
    socketEndpoint_ = std::make_unique<SocketEndpoint>(*this);
    channelEndpoint_ = std::make_unique<ChannelEndpoint>(*this);
    if (!socksMode_) {
        // 直连/远端半边：开泵即可；dynamic 半边在握手解析出目标后才建泵。
        pump_ = std::make_unique<BidirectionalPump>(*socketEndpoint_, *channelEndpoint_,
                                                    stats_.get());
    }
    session_.registerForwarded(this);
    if (half_ == Half::Local) {
        if (!session_.thread_.loop().addSocket(
                socket_, io::EventLoop::Readable,
                [this](SOCKET socket, short events) { onTcpEvent(socket, events); })) {
            failOpen(ForwardOpenError::NotEstablished, "failed to register socket");
            return;
        }
        socketRegistered_ = true;
        refreshSocketInterest();
        pump();
    } else {
        beginTargetConnect();
    }
    session_.updateSocketInterest();
}

void ForwardedConnection::pump()
{
    const ForwardState state = state_.load(std::memory_order_acquire);
    if (state == ForwardState::Closed || state == ForwardState::Idle) {
        return; // defensive no-op
    }
    if (session_.state() != ssh::SshSessionState::Established) {
        // Session teardown in progress (Closing/Closed/Disconnected/Error):
        // the struct may be freed already; onSessionLost owns the cleanup.
        return;
    }
    switch (state) {
    case ForwardState::Opening:
        if (socksMode_ && !socksParsed_) {
            driveSocks(); // SOCKS5 握手（含回复写回）；解析出目标后转入通道打开
        }
        if (!socksMode_ || socksParsed_) {
            if (half_ == Half::Local) {
                if (channel_ == nullptr) {
                    driveChannelOpen();
                } else {
                    // 通道已开但回复未写完（部分写等 Writable）：续推并转泵。
                    finishOpenToPumping();
                }
            } else {
                driveTargetConnect();
            }
        }
        if (state_.load(std::memory_order_acquire) == ForwardState::Pumping) {
            drivePumpOnce(); // opened just now: run the data plane on the same beat
        }
        break;
    case ForwardState::Pumping:
        drivePumpOnce();
        break;
    case ForwardState::Closing:
        driveClose();
        break;
    }
}

void ForwardedConnection::onTcpEvent(SOCKET socket, short events)
{
    (void)events;
    if (socket != socket_ || socket == INVALID_SOCKET) {
        return; // stale event from a replaced connect-attempt socket
    }
    if (session_.state() != ssh::SshSessionState::Established) {
        return; // session teardown: onSessionLost owns the cleanup
    }
    const ForwardState state = state_.load(std::memory_order_acquire);
    if (state == ForwardState::Opening && half_ == Half::Local && socksMode_) {
        pump(); // SOCKS 握手与回复写回（Readable/Writable 都可能推进）
        if (state_.load(std::memory_order_acquire) != ForwardState::Closed) {
            session_.updateSocketInterest();
        }
        return;
    }
    if (state == ForwardState::Opening && half_ == Half::Remote) {
        driveTargetConnectEvent();
        return;
    }
    if (state == ForwardState::Pumping || state == ForwardState::Closing) {
        // Any event (Readable/Writable/ERR/HUP) is a reason to drive: ERR/HUP
        // surfaces as EOF/error through recv on the next pull.
        pump();
        if (state_.load(std::memory_order_acquire) != ForwardState::Closed) {
            session_.updateSocketInterest();
        }
    }
}

bool ForwardedConnection::driveChannelOpen()
{
    // libssh2 shares one channel-open machine per session: wait our turn.
    if (!session_.tryAcquireFwdChannelOpen(this)) {
        return false;
    }
    struct _LIBSSH2_CHANNEL* channel = ::libssh2_channel_direct_tcpip_ex(
        session_.session_, host_.c_str(), static_cast<int>(port_),
        srcHost_.empty() ? "127.0.0.1" : srcHost_.c_str(), static_cast<int>(srcPort_));
    if (channel == nullptr) {
        if (::libssh2_session_last_errno(session_.session_) == LIBSSH2_ERROR_EAGAIN) {
            noteBlocked(); // gate kept: our continuation owns the machine
            return false;
        }
        session_.releaseFwdChannelOpen(this);
        if (socksMode_ && socksParsed_) {
            // F05 dynamic：direct-tcpip 被拒（目标不可达/服务器策略），尽力把
            // HostUnreachable 回给客户端再收尾（closesocket 会尝试送达缓冲）。
            sendBestEffort(socks5::buildReply(socks5::Reply::HostUnreachable));
        }
        failOpen(ForwardOpenError::ChannelOpenFailed, lastLibssh2Error());
        return true;
    }
    session_.releaseFwdChannelOpen(this);
    channel_ = channel;
    blockedOutbound_ = false;
    if (closeRequested_.load(std::memory_order_acquire)) {
        // close() raced the open: honor it immediately (SshChannel convention).
        // 统计/回调由 finishOpenToPumping 入账，enterClosing 路径的 finishClose 配平。
        finishOpenToPumping();
        enterClosing(ForwardCloseReason::LocalClose, "local close requested");
        return true;
    }
    finishOpenToPumping();
    return true;
}

// F05 dynamic：open 成功收尾（可多次进入：回复未写完时保持 Opening，Writable
// 事件续推）。统计与 onOpen 仅入账一次；dynamic 半边先把成功回复写回客户端，
// 写完才进入 Pumping 并注入请求后已到达的字节；直连半边直接进泵。
void ForwardedConnection::finishOpenToPumping()
{
    if (!openNotified_) {
        openNotified_ = true;
        countedActive_ = true;
        stats_->activeConnections.fetch_add(1, std::memory_order_relaxed);
        stats_->totalConnections.fetch_add(1, std::memory_order_relaxed);
        FWD_LOG("direct-tcpip open to %s:%u", host_.c_str(), port_);
        if (callbacks_.onOpen) {
            callbacks_.onOpen({true, ForwardOpenError::None, ""});
        }
    }
    if (socksMode_) {
        if (!socksReplyArmed_) {
            socksReplyArmed_ = true;
            socksReplyPending_ = socks5::buildReply(socks5::Reply::Success);
        }
        if (!flushSocksReply()) {
            return; // 回复未写完：保持 Opening，Writable 事件续推
        }
        if (socksReplyFailed_) {
            // 成功回复都没送出去：客户端多半已断开，按 socket 错误收尾。
            enterClosing(ForwardCloseReason::SocketError, "socks reply send failed");
            return; // enterClosing 可能同步回收本对象，之后不得触碰成员
        }
    }
    transitionTo(ForwardState::Pumping);
    if (pump_ != nullptr && !socksLeftover_.empty()) {
        pump_->injectAtoB(std::move(socksLeftover_));
        socksLeftover_.clear();
    }
    refreshSocketInterest();
    session_.updateSocketInterest();
}

// F05：写回挂起的握手帧；全部写完返回 true，WOULDBLOCK 返回 false（由
// refreshSocketInterest 挂 Writable 续推）。写失败只记账 socksReplyFailed_
// 并返回 true，由调用方检查后收尾（本函数绝不触碰状态机，避免回收路径
// 的重入）。
bool ForwardedConnection::flushSocksReply()
{
    socksReplyFailed_ = false;
    while (!socksReplyPending_.empty()) {
        const int sent = ::send(socket_, socksReplyPending_.data(),
                                static_cast<int>(socksReplyPending_.size()), 0);
        if (sent > 0) {
            socksReplyPending_.erase(0, static_cast<size_t>(sent));
            continue;
        }
        const int error = ::WSAGetLastError();
        if (sent == 0 || error == WSAEWOULDBLOCK) {
            return false; // retry on writable
        }
        socksReplyFailed_ = true;
        return true;
    }
    return true;
}

// F05：尽力一写（不重试、不收尾）——拒绝/失败路径在关闭前的最后送达机会，
// 失败由紧随其后的关闭路径处理。
void ForwardedConnection::sendBestEffort(const std::string& data)
{
    if (socket_ != INVALID_SOCKET && !data.empty()) {
        (void)::send(socket_, data.data(), static_cast<int>(data.size()), 0);
    }
}

// F05：SOCKS5 握手驱动（Opening、socksMode、解析完成前）。循环：先续推挂起
// 的回复帧，再读 socket 喂纯逻辑握手，按裁决回写/推进/失败收尾。与桌面端
// socks5.ts 的行为一致：失败先写裁决帧（可为空）再关闭；握手期间不建泵。
// 已知偏差（记录于 04-TASKS 进度日志）：握手无超时（对齐桌面端，卡住由
// tunnel stop 兜底拆除）。
bool ForwardedConnection::driveSocks()
{
    for (;;) {
        if (!flushSocksReply()) {
            return false; // 回复未写完，等 Writable
        }
        if (socksReplyFailed_) {
            enterClosing(ForwardCloseReason::SocketError, "socks reply send failed");
            return true; // enterClosing 可能同步回收本对象，之后不得触碰成员
        }
        if (socksParsed_) {
            return true; // 主泵路径接续 driveChannelOpen
        }
        if (closeRequested_.load(std::memory_order_acquire)) {
            enterClosing(ForwardCloseReason::LocalClose, "local close requested");
            return true;
        }
        char buffer[BidirectionalPump::kReadChunkBytes];
        const int received = ::recv(socket_, buffer, sizeof(buffer), 0);
        if (received < 0) {
            const int error = ::WSAGetLastError();
            if (error == WSAEWOULDBLOCK) {
                return false; // 等下一拍
            }
            char message[96]{};
            ::sprintf_s(message, sizeof(message), "socks handshake recv failed (WSA %d)",
                        error);
            enterClosing(ForwardCloseReason::SocketError, message);
            return true;
        }
        if (received == 0) {
            // 客户端在握手完成前就关了连接。
            enterClosing(ForwardCloseReason::SocketError, "socks handshake: client closed");
            return true;
        }
        const socks5::FeedResult outcome = socks_.feed(buffer, static_cast<size_t>(received));
        switch (outcome.kind) {
        case socks5::FeedKind::NeedMore:
            return false; // 等客户端后续字节（Readable 已挂）
        case socks5::FeedKind::Reply:
            socksReplyPending_ = outcome.reply; // 循环顶部先尽力写回
            continue;
        case socks5::FeedKind::Request:
            socksReplyPending_.clear();
            socksLeftover_ = outcome.leftover;
            host_ = outcome.host;
            port_ = outcome.port;
            socksParsed_ = true;
            pump_ = std::make_unique<BidirectionalPump>(*socketEndpoint_, *channelEndpoint_,
                                                        stats_.get());
            FWD_LOG("socks5 CONNECT %s:%u", host_.c_str(), static_cast<unsigned>(port_));
            return true; // 主泵路径接续 driveChannelOpen
        case socks5::FeedKind::Failure:
        default:
            // 失败帧尽力送达（可为空 = 不回复直接关），然后收尾。
            sendBestEffort(outcome.reply);
            enterClosing(ForwardCloseReason::SocketError,
                         "socks handshake failed: " + outcome.message);
            return true; // enterClosing 可能同步回收本对象，之后不得触碰成员
        }
    }
}

// ---------------------------------------------------------------- remote-half TCP connect (loop thread)

void ForwardedConnection::beginTargetConnect()
{
    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;
    const std::string service = std::to_string(port_);
    addrinfo* addresses = nullptr;
    if (::getaddrinfo(host_.c_str(), service.c_str(), &hints, &addresses) != 0) {
        failOpen(ForwardOpenError::TargetResolveFailed,
                 "target DNS resolution failed: " + host_);
        return;
    }
    connectAddrs_ = addresses;
    connectNext_ = addresses;
    connectDeadlineMs_ = steadyNowMs() + kTargetConnectTimeoutMs;
    connectTimerArmed_ = true;
    connectTimerId_ = session_.thread_.loop().runAfter(kTargetConnectTimeoutMs, [this] {
        connectTimerArmed_ = false;
        if (state_.load(std::memory_order_acquire) == ForwardState::Opening &&
            half_ == Half::Remote) {
            failOpen(ForwardOpenError::TargetConnectFailed, "target connect timed out");
        }
    });
    tryConnectNext();
}

void ForwardedConnection::tryConnectNext()
{
    cleanupSocket(); // drop the previous attempt's socket/registration, if any
    for (; connectNext_ != nullptr; connectNext_ = connectNext_->ai_next) {
        SOCKET candidate = ::socket(connectNext_->ai_family, connectNext_->ai_socktype,
                                    connectNext_->ai_protocol);
        if (candidate == INVALID_SOCKET) {
            continue;
        }
        u_long nonBlocking = 1;
        if (::ioctlsocket(candidate, FIONBIO, &nonBlocking) == SOCKET_ERROR) {
            ::closesocket(candidate);
            continue;
        }
        configureAcceptedSocket(candidate);
        if (::connect(candidate, connectNext_->ai_addr,
                      static_cast<int>(connectNext_->ai_addrlen)) == 0) {
            socket_ = candidate;
            onTargetConnected();
            return;
        }
        const int error = ::WSAGetLastError();
        if (isConnectInProgress(error)) {
            socket_ = candidate;
            if (!session_.thread_.loop().addSocket(
                    socket_, io::EventLoop::Writable,
                    [this](SOCKET socket, short events) { onTcpEvent(socket, events); })) {
                ::closesocket(socket_);
                socket_ = INVALID_SOCKET;
                continue;
            }
            socketRegistered_ = true;
            return; // completion arrives via onTcpEvent -> driveTargetConnectEvent
        }
        ::closesocket(candidate); // refused/unreachable on this address: try next
    }
    failOpen(ForwardOpenError::TargetConnectFailed,
             "target connect failed: " + host_ + ":" + std::to_string(port_));
}

void ForwardedConnection::driveTargetConnectEvent()
{
    // Writable (or ERR) on the connecting socket: verdict via SO_ERROR.
    int socketError = 0;
    int length = sizeof(socketError);
    if (::getsockopt(socket_, SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&socketError),
                     &length) == SOCKET_ERROR) {
        socketError = ::WSAGetLastError();
    }
    if (socketError == 0) {
        // Writable may also fire spuriously; confirm the connect really
        // completed? SO_ERROR==0 IS the confirmation (same as SshSession).
        onTargetConnected();
        return;
    }
    // This address failed: advance and try the next one.
    connectNext_ = connectNext_ != nullptr ? connectNext_->ai_next : nullptr;
    tryConnectNext();
}

void ForwardedConnection::onTargetConnected()
{
    if (connectTimerArmed_) {
        session_.thread_.loop().cancelTimer(connectTimerId_);
        connectTimerArmed_ = false;
    }
    if (connectAddrs_ != nullptr) {
        ::freeaddrinfo(connectAddrs_);
        connectAddrs_ = nullptr;
        connectNext_ = nullptr;
    }
    if (!socketRegistered_) {
        if (!session_.thread_.loop().addSocket(
                socket_, io::EventLoop::Readable,
                [this](SOCKET socket, short events) { onTcpEvent(socket, events); })) {
            failOpen(ForwardOpenError::TargetConnectFailed, "failed to register socket");
            return;
        }
        socketRegistered_ = true;
    }
    transitionTo(ForwardState::Pumping);
    countedActive_ = true;
    stats_->activeConnections.fetch_add(1, std::memory_order_relaxed);
    stats_->totalConnections.fetch_add(1, std::memory_order_relaxed);
    FWD_LOG("remote-forward target connected: %s:%u", host_.c_str(), port_);
    if (callbacks_.onOpen) {
        callbacks_.onOpen({true, ForwardOpenError::None, ""});
    }
    refreshSocketInterest();
    if (closeRequested_.load(std::memory_order_acquire)) {
        enterClosing(ForwardCloseReason::LocalClose, "local close requested");
    }
}

void ForwardedConnection::driveTargetConnect()
{
    // Session-socket events also nudge the connect drive (deadline expiry is
    // timer-driven; nothing to do here otherwise).
    if (steadyNowMs() >= connectDeadlineMs_) {
        failOpen(ForwardOpenError::TargetConnectFailed, "target connect timed out");
    }
}

// ---------------------------------------------------------------- data plane (loop thread)

bool ForwardedConnection::drivePumpOnce()
{
    if (!pump_) {
        return false;
    }
    const PumpDriveReport report = pump_->drive();
    if (report.readStalledB || report.writeStalledB) {
        noteBlocked(); // channel-side EAGAIN: record the true direction
    }
    if (!pump_->wantWriteB()) {
        // Nothing pending toward the channel: any earlier outbound stall has
        // drained, so the session need not arm Writable for us.
        blockedOutbound_ = false;
    }
    if (pump_->finished()) {
        if (pump_->finishReason() == PumpFinishReason::GracefulEof) {
            enterClosing(ForwardCloseReason::FinishedEof, "both directions EOFed");
        } else {
            const std::string message = pump_->errorMessage();
            const bool channelSide = message.compare(0, 7, "channel") == 0;
            enterClosing(channelSide ? ForwardCloseReason::ChannelError
                                     : ForwardCloseReason::SocketError,
                         message);
        }
        return true;
    }
    refreshSocketInterest();
    return report.progress;
}

void ForwardedConnection::refreshSocketInterest()
{
    if (socket_ == INVALID_SOCKET || !socketRegistered_) {
        return;
    }
    short events = 0;
    const ForwardState state = state_.load(std::memory_order_acquire);
    if (state == ForwardState::Opening) {
        if (half_ == Half::Remote) {
            events = io::EventLoop::Writable; // connect in progress
        } else if (socksMode_ && !socksParsed_) {
            events = io::EventLoop::Readable; // SOCKS 握手：等客户端字节
            if (!socksReplyPending_.empty()) {
                events |= io::EventLoop::Writable; // 回复未写完：续推
            }
        } else if (socksMode_ && socksParsed_ && pump_ == nullptr) {
            events = io::EventLoop::Readable; // 目标已解析、通道未开：吸收余量
            if (!socksReplyPending_.empty()) {
                events |= io::EventLoop::Writable;
            }
        }
        // 非 dynamic 的本地半边：通道打开驱动跟会话 socket，本地无需兴趣。
    } else if (state == ForwardState::Pumping && pump_) {
        if (pump_->wantReadA()) {
            events |= io::EventLoop::Readable;
        }
        if (pump_->wantWriteA()) {
            events |= io::EventLoop::Writable;
        }
        // NOTE: no fallback Readable. A FIN-saturated socket stays readable
        // forever —arming it without wantReadA would spin the loop (the same
        // level-triggered hazard SshSession documents for Writable).
    }
    // Closing: the channel handshake rides the session socket; the TCP socket
    // needs no interest (0 = registered but never fires).
    session_.thread_.loop().modifySocket(socket_, events); // best effort: teardown ignores failure
}

// ---------------------------------------------------------------- close path (loop thread)

void ForwardedConnection::enterClosing(ForwardCloseReason reason, std::string message)
{
    const ForwardState state = state_.load(std::memory_order_acquire);
    if (state != ForwardState::Opening && state != ForwardState::Pumping) {
        return; // Closing/Closed/Idle: nothing to enter
    }
    closeReason_ = reason;
    closeMessage_ = std::move(message);
    transitionTo(ForwardState::Closing);
    refreshSocketInterest(); // drop TCP interests; handshake rides the session socket
    driveClose();            // synchronous first attempt (usually instant on a live peer)
    session_.updateSocketInterest();
}

bool ForwardedConnection::driveClose()
{
    if (channel_ != nullptr && !closeCompleted_) {
        const int rc = ::libssh2_channel_close(channel_);
        if (rc == LIBSSH2_ERROR_EAGAIN) {
            noteBlocked();
            if (++closeAttempts_ >= kMaxCloseAttempts) {
                FWD_LOG("channel close handshake stalled, freeing best effort");
                cleanupChannel();
                finishClose();
                return true;
            }
            return false;
        }
        if (rc != 0) {
            FWD_LOG("channel close failed (best effort): %s", lastLibssh2Error().c_str());
        }
        closeCompleted_ = true;
    }
    if (channel_ != nullptr) {
        const int rc = ::libssh2_channel_free(channel_);
        if (rc == LIBSSH2_ERROR_EAGAIN) {
            // Defensive: with close completed, free sends nothing and should
            // never EAGAIN (SshChannel convention).
            noteBlocked();
            return false;
        }
        channel_ = nullptr;
    }
    finishClose();
    return true;
}

void ForwardedConnection::finishClose()
{
    if (connectTimerArmed_) {
        session_.thread_.loop().cancelTimer(connectTimerId_);
        connectTimerArmed_ = false;
    }
    if (connectAddrs_ != nullptr) {
        ::freeaddrinfo(connectAddrs_);
        connectAddrs_ = nullptr;
        connectNext_ = nullptr;
    }
    cleanupSocket();
    session_.unregisterForwarded(this);
    pump_.reset();
    socketEndpoint_.reset();
    channelEndpoint_.reset();
    if (countedActive_) {
        countedActive_ = false;
        if (stats_) {
            stats_->activeConnections.fetch_sub(1, std::memory_order_relaxed);
        }
    }
    transitionTo(ForwardState::Closed);
    if (!openFailed_ && callbacks_.onClose) {
        ForwardCloseInfo info;
        info.reason = closeReason_;
        info.message = closeMessage_;
        callbacks_.onClose(info);
    }
}

void ForwardedConnection::failOpen(ForwardOpenError error, const std::string& message)
{
    if (state_.load(std::memory_order_acquire) == ForwardState::Closed) {
        return; // timer and drive paths both fire on failure: first one wins
    }
    FWD_LOG("forward open failed (%s): %s", toString(error), message.c_str());
    openFailed_ = true; // finishClose suppresses onClose on this path
    session_.releaseFwdChannelOpen(this);
    if (callbacks_.onOpen) {
        callbacks_.onOpen({false, error, message});
    }
    if (half_ == Half::Remote) {
        cleanupChannel(); // free the accepted-but-unused channel
    }
    if (connectTimerArmed_) {
        session_.thread_.loop().cancelTimer(connectTimerId_);
        connectTimerArmed_ = false;
    }
    if (connectAddrs_ != nullptr) {
        ::freeaddrinfo(connectAddrs_);
        connectAddrs_ = nullptr;
        connectNext_ = nullptr;
    }
    cleanupSocket();
    session_.unregisterForwarded(this); // no-op when begin() never registered
    pump_.reset();
    socketEndpoint_.reset();
    channelEndpoint_.reset();
    transitionTo(ForwardState::Closed);
    // 回收钩子最后触发：erase 会析构本对象，返回后不得再触碰任何成员
    //（与 finishClose 里 onClose 的「destroys post-onClose」同款纪律）。
    std::function<void()> reclaim = std::move(openFailureCallback_);
    openFailureCallback_ = nullptr;
    if (reclaim) {
        reclaim();
    }
}

void ForwardedConnection::onSessionLost()
{
    // Loop thread, called from SshSession::releaseResources. The session
    // struct is still allocated (freed after notify), so channel_free follows
    // the SshChannel precedent; no close handshake, no interest updates, no
    // unregister (the whole table is dropped by the session).
    session_.releaseFwdChannelOpen(this);
    if (connectTimerArmed_) {
        session_.thread_.loop().cancelTimer(connectTimerId_);
        connectTimerArmed_ = false;
    }
    if (connectAddrs_ != nullptr) {
        ::freeaddrinfo(connectAddrs_);
        connectAddrs_ = nullptr;
        connectNext_ = nullptr;
    }
    if (channel_ != nullptr) {
        ::libssh2_channel_free(channel_);
        channel_ = nullptr;
    }
    cleanupSocket();
    pump_.reset();
    socketEndpoint_.reset();
    channelEndpoint_.reset();
    const ForwardState state = state_.load(std::memory_order_acquire);
    if (state == ForwardState::Pumping || state == ForwardState::Closing) {
        transitionTo(ForwardState::Closed);
        if (countedActive_) {
            countedActive_ = false;
            if (stats_) {
                stats_->activeConnections.fetch_sub(1, std::memory_order_relaxed);
            }
        }
        if (!openFailed_ && callbacks_.onClose) {
            ForwardCloseInfo info;
            info.reason = ForwardCloseReason::SessionLost;
            info.message = "session lost/closed, connection force-cleaned";
            callbacks_.onClose(info);
        }
        return;
    }
    if (state == ForwardState::Opening) {
        // Session lost mid-open: report as an onOpen failure (contract: a
        // failed open is never followed by onClose).
        openFailed_ = true;
        transitionTo(ForwardState::Closed);
        if (callbacks_.onOpen) {
            callbacks_.onOpen(
                {false, ForwardOpenError::SessionLost,
                 "session lost/closed, connection force-cleaned"});
        }
    }
    // Idle (never registered, cannot happen) / Closed (already terminal): no-op.
}

// ---------------------------------------------------------------- internals (loop thread)

void ForwardedConnection::noteBlocked()
{
    const int dirs = ::libssh2_session_block_directions(session_.session_);
    blockedOutbound_ = (dirs & LIBSSH2_SESSION_BLOCK_OUTBOUND) != 0;
}

void ForwardedConnection::transitionTo(ForwardState to)
{
    const ForwardState from = state_.load(std::memory_order_acquire);
    if (!isLegalTransition(from, to)) {
        FWD_LOG("illegal forward transition %s -> %s rejected", toString(from),
                toString(to));
        return;
    }
    if (to == ForwardState::Closing || to == ForwardState::Closed) {
        // No new opens past teardown (openAdmitted_ stays taken: one-shot).
    }
    state_.store(to, std::memory_order_release);
    FWD_LOG("forward state %s -> %s", toString(from), toString(to));
}

void ForwardedConnection::cleanupSocket()
{
    if (socketRegistered_) {
        session_.thread_.loop().removeSocket(socket_);
        socketRegistered_ = false;
    }
    if (socket_ != INVALID_SOCKET) {
        ::closesocket(socket_);
        socket_ = INVALID_SOCKET;
    }
}

void ForwardedConnection::cleanupChannel()
{
    if (channel_ != nullptr) {
        ::libssh2_channel_free(channel_); // best effort; session_free covers the rest
        channel_ = nullptr;
    }
}

std::string ForwardedConnection::lastLibssh2Error()
{
    char* errmsg = nullptr;
    int errmsgLen = 0;
    ::libssh2_session_last_error(session_.session_, &errmsg, &errmsgLen, 0);
    return errmsg != nullptr ? std::string(errmsg, static_cast<size_t>(errmsgLen))
                             : std::string("unknown error");
}

// ---------------------------------------------------------------- stringification

const char* toString(ForwardState state)
{
    switch (state) {
    case ForwardState::Idle:    return "idle";
    case ForwardState::Opening: return "opening";
    case ForwardState::Pumping: return "pumping";
    case ForwardState::Closing: return "closing";
    case ForwardState::Closed:  return "closed";
    }
    return "unknown";
}

const char* toString(ForwardCloseReason reason)
{
    switch (reason) {
    case ForwardCloseReason::FinishedEof: return "finished_eof";
    case ForwardCloseReason::LocalClose:  return "local_close";
    case ForwardCloseReason::ChannelError: return "channel_error";
    case ForwardCloseReason::SocketError:  return "socket_error";
    case ForwardCloseReason::SessionLost:  return "session_lost";
    }
    return "unknown";
}

const char* toString(ForwardOpenError error)
{
    switch (error) {
    case ForwardOpenError::None:               return "none";
    case ForwardOpenError::NotEstablished:     return "not_established";
    case ForwardOpenError::ChannelOpenFailed:  return "channel_open_failed";
    case ForwardOpenError::TargetResolveFailed: return "target_resolve_failed";
    case ForwardOpenError::TargetConnectFailed: return "target_connect_failed";
    case ForwardOpenError::SessionLost:        return "session_lost";
    }
    return "unknown";
}

} // namespace fwd
} // namespace sshclient
