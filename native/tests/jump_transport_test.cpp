#include <gtest/gtest.h>

#include <cerrno>
#include <memory>
#include <string>

#include "fwd/jump_transport.h"
#include "libssh2.h"

using namespace sshclient::fwd;

TEST(JumpTransportTest, SendRecvBasicOverFakeChannel)
{
    auto fakeChannel = std::make_shared<FakeJumpChannel>();
    JumpTransport transport(fakeChannel);

    EXPECT_EQ(transport.bytesSent(), 0u);
    EXPECT_EQ(transport.bytesReceived(), 0u);
    EXPECT_FALSE(transport.isClosed());

    // Send
    const std::string testData = "SSH-2.0-OpenSSH_8.9\r\n";
    ssize_t written = transport.send(testData.data(), testData.size());
    EXPECT_EQ(written, static_cast<ssize_t>(testData.size()));
    EXPECT_EQ(transport.bytesSent(), testData.size());
    EXPECT_EQ(fakeChannel->outbound(), testData);

    // Inbound feed & recv
    const std::string serverBanner = "SSH-2.0-libssh2_1.11.0\r\n";
    fakeChannel->feedInbound(serverBanner);

    char recvBuf[64] = {};
    ssize_t readBytes = transport.recv(recvBuf, sizeof(recvBuf));
    EXPECT_EQ(readBytes, static_cast<ssize_t>(serverBanner.size()));
    EXPECT_EQ(transport.bytesReceived(), serverBanner.size());
    EXPECT_EQ(std::string(recvBuf, readBytes), serverBanner);
}

TEST(JumpTransportTest, EagainNonBlockingHandling)
{
    auto fakeChannel = std::make_shared<FakeJumpChannel>();
    JumpTransport transport(fakeChannel);

    // When empty and not EOF -> -EAGAIN
    char recvBuf[32];
    EXPECT_EQ(transport.recv(recvBuf, sizeof(recvBuf)), -EAGAIN);

    // Explicit read EAGAIN
    fakeChannel->feedInbound("hello");
    fakeChannel->setReadEagain(true);
    EXPECT_EQ(transport.recv(recvBuf, sizeof(recvBuf)), -EAGAIN);
    // Next read consumes the queued data
    EXPECT_EQ(transport.recv(recvBuf, sizeof(recvBuf)), 5);

    // Write EAGAIN
    fakeChannel->setWriteEagain(true);
    EXPECT_EQ(transport.send("abc", 3), -EAGAIN);
    // Next write succeeds
    EXPECT_EQ(transport.send("abc", 3), 3);
}

TEST(JumpTransportTest, EofAndErrorPropagation)
{
    auto fakeChannel = std::make_shared<FakeJumpChannel>();
    JumpTransport transport(fakeChannel);

    char buf[32];
    fakeChannel->setEof();
    EXPECT_EQ(transport.recv(buf, sizeof(buf)), 0);

    fakeChannel->setReadError(-ECONNRESET);
    EXPECT_EQ(transport.recv(buf, sizeof(buf)), -ECONNRESET);

    fakeChannel->setWriteError(-EPIPE);
    EXPECT_EQ(transport.send("x", 1), -EPIPE);
}

TEST(JumpTransportTest, CloseStopsTraffic)
{
    auto fakeChannel = std::make_shared<FakeJumpChannel>();
    JumpTransport transport(fakeChannel);

    transport.close();
    EXPECT_TRUE(transport.isClosed());

    char buf[32];
    EXPECT_EQ(transport.send("data", 4), -EPIPE);
    EXPECT_EQ(transport.recv(buf, sizeof(buf)), 0);
}

TEST(JumpTransportTest, Libssh2CallbackFunctions)
{
    auto fakeChannel = std::make_shared<FakeJumpChannel>();
    auto transport = std::make_shared<JumpTransport>(fakeChannel);

    // Null abstract check
    EXPECT_EQ(JumpTransport::sendCallback(0, "a", 1, 0, nullptr), -ECONNRESET);
    void* nullPtr = nullptr;
    EXPECT_EQ(JumpTransport::recvCallback(0, nullptr, 0, 0, &nullPtr), -ECONNRESET);

    // Valid abstract pointer
    void* abs = transport.get();
    ssize_t sent = JumpTransport::sendCallback(0, "ping", 4, 0, &abs);
    EXPECT_EQ(sent, 4);
    EXPECT_EQ(fakeChannel->outbound(), "ping");

    fakeChannel->feedInbound("pong");
    char inBuf[16] = {};
    ssize_t recvd = JumpTransport::recvCallback(0, inBuf, sizeof(inBuf), 0, &abs);
    EXPECT_EQ(recvd, 4);
    EXPECT_EQ(std::string(inBuf, recvd), "pong");
}

TEST(JumpTransportTest, AttachToSessionRegistersCallbacks)
{
    auto fakeChannel = std::make_shared<FakeJumpChannel>();
    auto transport = std::make_shared<JumpTransport>(fakeChannel);

    LIBSSH2_SESSION* session = ::libssh2_session_init_ex(nullptr, nullptr, nullptr, transport.get());
    ASSERT_NE(session, nullptr);

    bool attached = transport->attachToSession(session);
    EXPECT_TRUE(attached);

    // We verify attachment by checking sending through the session's callbacks
    fakeChannel->feedInbound("ok");
    char inBuf[16] = {};
    void* abs = transport.get();
    EXPECT_EQ(JumpTransport::recvCallback(0, inBuf, sizeof(inBuf), 0, &abs), 2);

    ::libssh2_session_free(session);
}

TEST(JumpTransportTest, BufferedJumpPipeFullDuplex)
{
    auto pipe = std::make_shared<BufferedJumpPipe>();
    bool notified = false;
    pipe->setWakeupCallback([&]() { notified = true; });

    // Push inbound
    pipe->pushInbound("downstream", 10);
    EXPECT_TRUE(notified);

    char buf[32] = {};
    ssize_t r = pipe->read(buf, sizeof(buf));
    EXPECT_EQ(r, 10);
    EXPECT_EQ(std::string(buf, r), "downstream");

    // Write outbound and pull
    pipe->write("upstream", 8);
    char outBuf[32] = {};
    ssize_t pulled = pipe->pullOutbound(outBuf, sizeof(outBuf));
    EXPECT_EQ(pulled, 8);
    EXPECT_EQ(std::string(outBuf, pulled), "upstream");

    // EOF and close
    pipe->setInboundEof();
    EXPECT_EQ(pipe->read(buf, sizeof(buf)), 0);
    EXPECT_TRUE(pipe->isEof());
}
