#pragma once

// F07: ProxyJump 跳板传输层（01-DESIGN.md §11.3；04-TASKS F07）。
//
// 本组件实现 LIBSSH2_CALLBACK_SEND / LIBSSH2_CALLBACK_RECV 回调，
// 将上级会话的 direct-tcpip 通道作为目标会话的底层传输层。
// 同时提供 IJumpChannel 抽象接口与 FakeJumpChannel 供单元测试脱机验证。

#include <winsock2.h>
#include <BaseTsd.h>
#include <cstddef>
#include <cstdint>
#include <atomic>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

#if (!defined(HAVE_SSIZE_T) && !defined(ssize_t))
typedef SSIZE_T ssize_t;
#define HAVE_SSIZE_T
#endif

struct _LIBSSH2_SESSION;
struct _LIBSSH2_CHANNEL;

#ifndef libssh2_socket_t
#ifdef _WIN32
typedef SOCKET libssh2_socket_t;
#else
typedef int libssh2_socket_t;
#endif
#endif

namespace sshclient {
namespace fwd {

// ----------------------------------------------------------------------------
// IJumpChannel: 通道双向流抽象
// ----------------------------------------------------------------------------
class IJumpChannel {
public:
    virtual ~IJumpChannel() = default;

    // 读取至多 length 字节：
    // > 0: 实际读取字节数
    // = 0: EOF（对端已关闭该通道）
    // = -EAGAIN: 非阻塞当前无可用数据
    // < 0: 错误码（负数，如 -ECONNRESET）
    virtual ssize_t read(void* buffer, size_t length) = 0;

    // 写入至多 length 字节：
    // > 0: 实际写入字节数
    // = -EAGAIN: 发送缓冲区满/当前阻塞
    // < 0: 错误码（负数，如 -EPIPE）
    virtual ssize_t write(const void* buffer, size_t length) = 0;

    virtual bool isEof() const = 0;
    virtual void close() = 0;
};

// ----------------------------------------------------------------------------
// FakeJumpChannel: 单元测试专用的假通道
// ----------------------------------------------------------------------------
class FakeJumpChannel final : public IJumpChannel {
public:
    FakeJumpChannel();
    ~FakeJumpChannel() override;

    // 注入供 read() 消费的数据
    void feedInbound(const void* data, size_t length);
    void feedInbound(const std::string& str);

    // 模拟控制
    void setReadEagain(bool eagain);
    void setWriteEagain(bool eagain);
    void setReadError(ssize_t err);
    void setWriteError(ssize_t err);
    void setEof();

    // 提取 write() 写出的数据
    std::string takeOutbound();
    const std::string& outbound() const;
    size_t outboundSize() const;

    ssize_t read(void* buffer, size_t length) override;
    ssize_t write(const void* buffer, size_t length) override;
    bool isEof() const override;
    void close() override;

private:
    mutable std::mutex mutex_;
    std::deque<uint8_t> inboundQueue_;
    std::string outbound_;
    bool readEagain_ = false;
    bool writeEagain_ = false;
    ssize_t readError_ = 0;
    ssize_t writeError_ = 0;
    bool eof_ = false;
    bool closed_ = false;
};

// ----------------------------------------------------------------------------
// DirectTcpipJumpChannel: libssh2 direct-tcpip 真实通道包装器
// ----------------------------------------------------------------------------
class DirectTcpipJumpChannel final : public IJumpChannel {
public:
    explicit DirectTcpipJumpChannel(struct _LIBSSH2_CHANNEL* channel);
    ~DirectTcpipJumpChannel() override;

    ssize_t read(void* buffer, size_t length) override;
    ssize_t write(const void* buffer, size_t length) override;
    bool isEof() const override;
    void close() override;

    struct _LIBSSH2_CHANNEL* rawChannel() const { return channel_; }

private:
    struct _LIBSSH2_CHANNEL* channel_ = nullptr;
    bool eof_ = false;
    bool closed_ = false;
};

// ----------------------------------------------------------------------------
// BufferedJumpPipe: 线程安全的全双工内存管道
// ----------------------------------------------------------------------------
class BufferedJumpPipe final : public IJumpChannel {
public:
    BufferedJumpPipe();
    ~BufferedJumpPipe() override;

    ssize_t pushInbound(const void* data, size_t length);
    ssize_t pullOutbound(void* buffer, size_t length);

    void setInboundEof();
    void setInboundError(ssize_t err);
    void setWakeupCallback(std::function<void()> cb);

    ssize_t read(void* buffer, size_t length) override;
    ssize_t write(const void* buffer, size_t length) override;
    bool isEof() const override;
    void close() override;

private:
    mutable std::mutex mutex_;
    std::deque<uint8_t> inbound_;
    std::deque<uint8_t> outbound_;
    std::function<void()> wakeupCb_;
    ssize_t inboundError_ = 0;
    bool inboundEof_ = false;
    bool closed_ = false;
};

// ----------------------------------------------------------------------------
// JumpTransport: ProxyJump 传输层管理器
// ----------------------------------------------------------------------------
class JumpTransport final {
public:
    explicit JumpTransport(std::shared_ptr<IJumpChannel> channel);
    ~JumpTransport();

    JumpTransport(const JumpTransport&) = delete;
    JumpTransport& operator=(const JumpTransport&) = delete;

    ssize_t send(const void* buffer, size_t length, int flags = 0);
    ssize_t recv(void* buffer, size_t length, int flags = 0);

    // libssh2 回调函数（LIBSSH2_SEND_FUNC / LIBSSH2_RECV_FUNC）
    // 当 session->abstract 指向 JumpTransport* 时使用：
    static ssize_t sendCallback(libssh2_socket_t sock, const void* buffer, size_t length,
                                int flags, void** abstract);
    static ssize_t recvCallback(libssh2_socket_t sock, void* buffer, size_t length,
                                int flags, void** abstract);

    // 挂载到 libssh2 会话（注册 LIBSSH2_CALLBACK_SEND / LIBSSH2_CALLBACK_RECV）
    bool attachToSession(struct _LIBSSH2_SESSION* session);

    uint64_t bytesSent() const { return bytesSent_.load(std::memory_order_relaxed); }
    uint64_t bytesReceived() const { return bytesReceived_.load(std::memory_order_relaxed); }
    bool isClosed() const { return closed_.load(std::memory_order_acquire); }
    void close();

    // F07: register a wakeup callback forwarded to the underlying channel
    // (works when channel_ is a BufferedJumpPipe or any channel that supports it).
    void setWakeupCallback(std::function<void()> cb);

    std::shared_ptr<IJumpChannel> channel() const { return channel_; }

private:
    std::shared_ptr<IJumpChannel> channel_;
    std::atomic<uint64_t> bytesSent_{0};
    std::atomic<uint64_t> bytesReceived_{0};
    std::atomic<bool> closed_{false};
};

} // namespace fwd
} // namespace sshclient
