#include "fwd/jump_transport.h"

#include <cerrno>
#include <cstring>
#include <algorithm>

#include "libssh2.h"

namespace sshclient {
namespace fwd {

// ============================================================================
// FakeJumpChannel
// ============================================================================

FakeJumpChannel::FakeJumpChannel() = default;

FakeJumpChannel::~FakeJumpChannel()
{
    close();
}

void FakeJumpChannel::feedInbound(const void* data, size_t length)
{
    if (data == nullptr || length == 0)
    {
        return;
    }
    const auto* ptr = static_cast<const uint8_t*>(data);
    std::lock_guard<std::mutex> lock(mutex_);
    inboundQueue_.insert(inboundQueue_.end(), ptr, ptr + length);
}

void FakeJumpChannel::feedInbound(const std::string& str)
{
    feedInbound(str.data(), str.size());
}

void FakeJumpChannel::setReadEagain(bool eagain)
{
    std::lock_guard<std::mutex> lock(mutex_);
    readEagain_ = eagain;
}

void FakeJumpChannel::setWriteEagain(bool eagain)
{
    std::lock_guard<std::mutex> lock(mutex_);
    writeEagain_ = eagain;
}

void FakeJumpChannel::setReadError(ssize_t err)
{
    std::lock_guard<std::mutex> lock(mutex_);
    readError_ = err < 0 ? err : -err;
}

void FakeJumpChannel::setWriteError(ssize_t err)
{
    std::lock_guard<std::mutex> lock(mutex_);
    writeError_ = err < 0 ? err : -err;
}

void FakeJumpChannel::setEof()
{
    std::lock_guard<std::mutex> lock(mutex_);
    eof_ = true;
}

std::string FakeJumpChannel::takeOutbound()
{
    std::lock_guard<std::mutex> lock(mutex_);
    std::string result = std::move(outbound_);
    outbound_.clear();
    return result;
}

const std::string& FakeJumpChannel::outbound() const
{
    std::lock_guard<std::mutex> lock(mutex_);
    return outbound_;
}

size_t FakeJumpChannel::outboundSize() const
{
    std::lock_guard<std::mutex> lock(mutex_);
    return outbound_.size();
}

ssize_t FakeJumpChannel::read(void* buffer, size_t length)
{
    if (buffer == nullptr || length == 0)
    {
        return 0;
    }

    std::lock_guard<std::mutex> lock(mutex_);
    if (closed_)
    {
        return -ECONNRESET;
    }
    if (readError_ != 0)
    {
        ssize_t err = readError_;
        readError_ = 0;
        return err;
    }
    if (readEagain_)
    {
        readEagain_ = false;
        return -EAGAIN;
    }
    if (inboundQueue_.empty())
    {
        if (eof_)
        {
            return 0;
        }
        return -EAGAIN;
    }

    size_t toCopy = (std::min)(length, inboundQueue_.size());
    auto* out = static_cast<uint8_t*>(buffer);
    for (size_t i = 0; i < toCopy; ++i)
    {
        out[i] = inboundQueue_.front();
        inboundQueue_.pop_front();
    }
    return static_cast<ssize_t>(toCopy);
}

ssize_t FakeJumpChannel::write(const void* buffer, size_t length)
{
    if (buffer == nullptr || length == 0)
    {
        return 0;
    }

    std::lock_guard<std::mutex> lock(mutex_);
    if (closed_)
    {
        return -EPIPE;
    }
    if (writeError_ != 0)
    {
        ssize_t err = writeError_;
        writeError_ = 0;
        return err;
    }
    if (writeEagain_)
    {
        writeEagain_ = false;
        return -EAGAIN;
    }

    const auto* ptr = static_cast<const char*>(buffer);
    outbound_.append(ptr, length);
    return static_cast<ssize_t>(length);
}

bool FakeJumpChannel::isEof() const
{
    std::lock_guard<std::mutex> lock(mutex_);
    return eof_ && inboundQueue_.empty();
}

void FakeJumpChannel::close()
{
    std::lock_guard<std::mutex> lock(mutex_);
    closed_ = true;
    eof_ = true;
}

// ============================================================================
// DirectTcpipJumpChannel
// ============================================================================

DirectTcpipJumpChannel::DirectTcpipJumpChannel(struct _LIBSSH2_CHANNEL* channel)
    : channel_(channel)
{
}

DirectTcpipJumpChannel::~DirectTcpipJumpChannel()
{
    close();
}

ssize_t DirectTcpipJumpChannel::read(void* buffer, size_t length)
{
    if (buffer == nullptr || length == 0)
    {
        return 0;
    }
    if (closed_)
    {
        return -ECONNRESET;
    }
    if (eof_)
    {
        return 0;
    }
    if (channel_ == nullptr)
    {
        return -ECONNRESET;
    }

    ssize_t rc = ::libssh2_channel_read_ex(channel_, 0, static_cast<char*>(buffer), length);
    if (rc == LIBSSH2_ERROR_EAGAIN)
    {
        return -EAGAIN;
    }
    if (rc == 0)
    {
        eof_ = true;
        return 0;
    }
    if (rc < 0)
    {
        return -ECONNRESET;
    }
    return rc;
}

ssize_t DirectTcpipJumpChannel::write(const void* buffer, size_t length)
{
    if (buffer == nullptr || length == 0)
    {
        return 0;
    }
    if (closed_)
    {
        return -EPIPE;
    }
    if (channel_ == nullptr)
    {
        return -ECONNRESET;
    }

    ssize_t rc = ::libssh2_channel_write_ex(channel_, 0, static_cast<const char*>(buffer), length);
    if (rc == LIBSSH2_ERROR_EAGAIN)
    {
        return -EAGAIN;
    }
    if (rc < 0)
    {
        return -EPIPE;
    }
    return rc;
}

bool DirectTcpipJumpChannel::isEof() const
{
    if (eof_ || closed_ || channel_ == nullptr)
    {
        return true;
    }
    return ::libssh2_channel_eof(channel_) != 0;
}

void DirectTcpipJumpChannel::close()
{
    closed_ = true;
    channel_ = nullptr;
}

// ============================================================================
// BufferedJumpPipe
// ============================================================================

BufferedJumpPipe::BufferedJumpPipe() = default;

BufferedJumpPipe::~BufferedJumpPipe()
{
    close();
}

ssize_t BufferedJumpPipe::pushInbound(const void* data, size_t length)
{
    if (data == nullptr || length == 0)
    {
        return 0;
    }
    std::function<void()> cb;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        if (closed_ || inboundEof_)
        {
            return -EPIPE;
        }
        const auto* ptr = static_cast<const uint8_t*>(data);
        inbound_.insert(inbound_.end(), ptr, ptr + length);
        cb = wakeupCb_;
    }
    if (cb)
    {
        cb();
    }
    return static_cast<ssize_t>(length);
}

ssize_t BufferedJumpPipe::pullOutbound(void* buffer, size_t length)
{
    if (buffer == nullptr || length == 0)
    {
        return 0;
    }
    std::lock_guard<std::mutex> lock(mutex_);
    if (outbound_.empty())
    {
        if (closed_)
        {
            return 0;
        }
        return -EAGAIN;
    }
    size_t toCopy = (std::min)(length, outbound_.size());
    auto* out = static_cast<uint8_t*>(buffer);
    for (size_t i = 0; i < toCopy; ++i)
    {
        out[i] = outbound_.front();
        outbound_.pop_front();
    }
    return static_cast<ssize_t>(toCopy);
}

void BufferedJumpPipe::setInboundEof()
{
    std::function<void()> cb;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        inboundEof_ = true;
        cb = wakeupCb_;
    }
    if (cb)
    {
        cb();
    }
}

void BufferedJumpPipe::setInboundError(ssize_t err)
{
    std::function<void()> cb;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        inboundError_ = err < 0 ? err : -err;
        cb = wakeupCb_;
    }
    if (cb)
    {
        cb();
    }
}

void BufferedJumpPipe::setWakeupCallback(std::function<void()> cb)
{
    std::lock_guard<std::mutex> lock(mutex_);
    wakeupCb_ = std::move(cb);
}

ssize_t BufferedJumpPipe::read(void* buffer, size_t length)
{
    if (buffer == nullptr || length == 0)
    {
        return 0;
    }
    std::lock_guard<std::mutex> lock(mutex_);
    if (inboundError_ != 0)
    {
        ssize_t err = inboundError_;
        inboundError_ = 0;
        return err;
    }
    if (inbound_.empty())
    {
        if (inboundEof_ || closed_)
        {
            return 0;
        }
        return -EAGAIN;
    }
    size_t toCopy = (std::min)(length, inbound_.size());
    auto* out = static_cast<uint8_t*>(buffer);
    for (size_t i = 0; i < toCopy; ++i)
    {
        out[i] = inbound_.front();
        inbound_.pop_front();
    }
    return static_cast<ssize_t>(toCopy);
}

ssize_t BufferedJumpPipe::write(const void* buffer, size_t length)
{
    if (buffer == nullptr || length == 0)
    {
        return 0;
    }
    std::lock_guard<std::mutex> lock(mutex_);
    if (closed_)
    {
        return -EPIPE;
    }
    const auto* ptr = static_cast<const uint8_t*>(buffer);
    outbound_.insert(outbound_.end(), ptr, ptr + length);
    return static_cast<ssize_t>(length);
}

bool BufferedJumpPipe::isEof() const
{
    std::lock_guard<std::mutex> lock(mutex_);
    return (inboundEof_ || closed_) && inbound_.empty();
}

void BufferedJumpPipe::close()
{
    std::function<void()> cb;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        closed_ = true;
        inboundEof_ = true;
        cb = wakeupCb_;
    }
    if (cb)
    {
        cb();
    }
}

// ============================================================================
// JumpTransport
// ============================================================================

JumpTransport::JumpTransport(std::shared_ptr<IJumpChannel> channel)
    : channel_(std::move(channel))
{
}

JumpTransport::~JumpTransport()
{
    close();
}

ssize_t JumpTransport::send(const void* buffer, size_t length, int flags)
{
    (void)flags;
    if (closed_.load(std::memory_order_acquire))
    {
        return -EPIPE;
    }
    if (!channel_)
    {
        return -ECONNRESET;
    }
    if (length == 0)
    {
        return 0;
    }

    ssize_t rc = channel_->write(buffer, length);
    if (rc > 0)
    {
        bytesSent_.fetch_add(static_cast<uint64_t>(rc), std::memory_order_relaxed);
    }
    return rc;
}

ssize_t JumpTransport::recv(void* buffer, size_t length, int flags)
{
    (void)flags;
    if (closed_.load(std::memory_order_acquire))
    {
        return 0; // EOF
    }
    if (!channel_)
    {
        return -ECONNRESET;
    }
    if (length == 0)
    {
        return 0;
    }

    ssize_t rc = channel_->read(buffer, length);
    if (rc > 0)
    {
        bytesReceived_.fetch_add(static_cast<uint64_t>(rc), std::memory_order_relaxed);
    }
    return rc;
}

ssize_t JumpTransport::sendCallback(libssh2_socket_t sock, const void* buffer, size_t length,
                                    int flags, void** abstract)
{
    (void)sock;
    if (abstract == nullptr || *abstract == nullptr)
    {
        return -ECONNRESET;
    }
    auto* transport = static_cast<JumpTransport*>(*abstract);
    return transport->send(buffer, length, flags);
}

ssize_t JumpTransport::recvCallback(libssh2_socket_t sock, void* buffer, size_t length,
                                    int flags, void** abstract)
{
    (void)sock;
    if (abstract == nullptr || *abstract == nullptr)
    {
        return -ECONNRESET;
    }
    auto* transport = static_cast<JumpTransport*>(*abstract);
    return transport->recv(buffer, length, flags);
}

bool JumpTransport::attachToSession(struct _LIBSSH2_SESSION* session)
{
    if (session == nullptr)
    {
        return false;
    }
    ::libssh2_session_callback_set2(session, LIBSSH2_CALLBACK_SEND,
                                   reinterpret_cast<libssh2_cb_generic*>(sendCallback));
    ::libssh2_session_callback_set2(session, LIBSSH2_CALLBACK_RECV,
                                   reinterpret_cast<libssh2_cb_generic*>(recvCallback));
    return true;
}

void JumpTransport::close()
{
    if (!closed_.exchange(true))
    {
        if (channel_)
        {
            channel_->close();
        }
    }
}

void JumpTransport::setWakeupCallback(std::function<void()> cb)
{
    // Forward to the underlying channel if it supports wakeup notification.
    // BufferedJumpPipe is the standard channel used in jump sessions;
    // DirectTcpipJumpChannel and FakeJumpChannel use other notification paths.
    if (auto* pipe = dynamic_cast<BufferedJumpPipe*>(channel_.get()))
    {
        pipe->setWakeupCallback(std::move(cb));
    }
}

} // namespace fwd
} // namespace sshclient
