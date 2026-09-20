// F05：SOCKS5 服务端握手（native/core/fwd/socks5，桌面端 socks5.ts 的 C++ 移植）
// 与 Core 侧 Socks5Parser 共用同源语义：分片到达、非法版本、不支持命令、
// 地址类型、回复帧字节、流水线 leftover。
#include "fwd/socks5.h"

#include <gtest/gtest.h>

#include <string>
#include <vector>

using namespace sshclient::fwd::socks5;

namespace {

std::string greeting(const std::vector<std::uint8_t>& methods)
{
    std::string out;
    out.push_back('\x05');
    out.push_back(static_cast<char>(methods.size()));
    for (std::uint8_t method : methods) {
        out.push_back(static_cast<char>(method));
    }
    return out;
}

std::string request(std::uint8_t atyp, const std::string& address, int port)
{
    std::string out;
    out.push_back('\x05');
    out.push_back('\x01'); // CONNECT
    out.push_back('\x00');
    out.push_back(static_cast<char>(atyp));
    out += address;
    out.push_back(static_cast<char>(port >> 8));
    out.push_back(static_cast<char>(port & 0xFF));
    return out;
}

std::string domain(const std::string& host)
{
    return std::string(1, static_cast<char>(host.size())) + host;
}

} // namespace

TEST(Socks5HandshakeTest, GreetingNoAuthSelectsMethod)
{
    Handshake handshake;
    const FeedResult result = handshake.feed(greeting({0x00}).data(), 3);
    ASSERT_EQ(FeedKind::Reply, result.kind);
    ASSERT_EQ(std::string("\x05\x00", 2), result.reply);
    EXPECT_FALSE(handshake.done());
}

TEST(Socks5HandshakeTest, GreetingFragmentsReachReplyIdentically)
{
    const std::string greetingFrame = greeting({0x00, 0x01});
    Handshake handshake;
    ASSERT_EQ(FeedKind::NeedMore, handshake.feed(greetingFrame.data(), 1).kind);
    ASSERT_EQ(FeedKind::NeedMore, handshake.feed(greetingFrame.data() + 1, 1).kind);
    const FeedResult result = handshake.feed(greetingFrame.data() + 2, 2);
    ASSERT_EQ(FeedKind::Reply, result.kind);
    ASSERT_EQ(std::string("\x05\x00", 2), result.reply);
}

TEST(Socks5HandshakeTest, GreetingWrongVersionFailsWithoutReply)
{
    Handshake handshake;
    const char frame[] = "\x04\x01\x00";
    const FeedResult result = handshake.feed(frame, 3);
    ASSERT_EQ(FeedKind::Failure, result.kind);
    EXPECT_TRUE(result.reply.empty());
    EXPECT_NE(std::string::npos, result.message.find("版本"));
    EXPECT_TRUE(handshake.done());
}

TEST(Socks5HandshakeTest, GreetingAuthOnlyRepliesNoAcceptableAndFails)
{
    Handshake handshake;
    const FeedResult result = handshake.feed(greeting({0x02}).data(), 3);
    ASSERT_EQ(FeedKind::Failure, result.kind);
    ASSERT_EQ(std::string("\x05\xFF", 2), result.reply);
    EXPECT_TRUE(handshake.done());
}

TEST(Socks5HandshakeTest, GreetingZeroMethodsFailsWithNoAcceptableReply)
{
    Handshake handshake;
    const FeedResult result = handshake.feed(greeting({}).data(), 2);
    ASSERT_EQ(FeedKind::Failure, result.kind);
    ASSERT_EQ(std::string("\x05\xFF", 2), result.reply);
}

TEST(Socks5HandshakeTest, ConnectIpv4ParsesHostPort)
{
    Handshake handshake;
    handshake.feed(greeting({0x00}).data(), 3);
    const std::string frame = request(0x01, std::string("\x01\x02\x03\x04", 4), 80);
    const FeedResult result = handshake.feed(frame.data(), frame.size());
    ASSERT_EQ(FeedKind::Request, result.kind);
    EXPECT_EQ("1.2.3.4", result.host);
    EXPECT_EQ(80, result.port);
    EXPECT_TRUE(result.leftover.empty());
    EXPECT_TRUE(handshake.done());
}

TEST(Socks5HandshakeTest, ConnectDomainParsesHostPort)
{
    Handshake handshake;
    handshake.feed(greeting({0x00}).data(), 3);
    const std::string frame = request(0x03, domain("example.com"), 443);
    const FeedResult result = handshake.feed(frame.data(), frame.size());
    ASSERT_EQ(FeedKind::Request, result.kind);
    EXPECT_EQ("example.com", result.host);
    EXPECT_EQ(443, result.port);
}

TEST(Socks5HandshakeTest, ConnectIpv6ParsesLowercaseHexGroups)
{
    Handshake handshake;
    handshake.feed(greeting({0x00}).data(), 3);
    const std::string address = std::string(
        "\x20\x01\x0d\xb8\x00\x01\x00\x00\x00\x00\x00\x00\x00\x00\x00\x01", 16);
    const std::string frame = request(0x04, address, 8080);
    const FeedResult result = handshake.feed(frame.data(), frame.size());
    ASSERT_EQ(FeedKind::Request, result.kind);
    EXPECT_EQ("2001:db8:1:0:0:0:0:1", result.host);
    EXPECT_EQ(8080, result.port);
}

TEST(Socks5HandshakeTest, ConnectFragmentsWaitForFullRequest)
{
    Handshake handshake;
    handshake.feed(greeting({0x00}).data(), 3);
    const std::string frame = request(0x01, std::string("\x09\x08\x07\x06", 4), 1234);
    for (size_t i = 0; i < 6; ++i) {
        ASSERT_EQ(FeedKind::NeedMore, handshake.feed(frame.data() + i, 1).kind);
    }
    const FeedResult result = handshake.feed(frame.data() + 6, frame.size() - 6);
    ASSERT_EQ(FeedKind::Request, result.kind);
    EXPECT_EQ("9.8.7.6", result.host);
    EXPECT_EQ(1234, result.port);
}

TEST(Socks5HandshakeTest, PipelinedApplicationDataReturnsAsLeftover)
{
    Handshake handshake;
    handshake.feed(greeting({0x00}).data(), 3);
    std::string frame = request(0x01, std::string("\x7F\x00\x00\x01", 4), 995);
    frame += std::string("\x16\x03\x01", 3);
    const FeedResult result = handshake.feed(frame.data(), frame.size());
    ASSERT_EQ(FeedKind::Request, result.kind);
    ASSERT_EQ(std::string("\x16\x03\x01", 3), result.leftover);
}

TEST(Socks5HandshakeTest, BindCommandIsRejectedWithReplyThenClose)
{
    Handshake handshake;
    handshake.feed(greeting({0x00}).data(), 3);
    std::string frame = request(0x01, std::string("\x01\x02\x03\x04", 4), 80);
    frame[1] = '\x02'; // BIND
    const FeedResult result = handshake.feed(frame.data(), frame.size());
    ASSERT_EQ(FeedKind::Failure, result.kind);
    ASSERT_EQ(buildReply(Reply::CommandNotSupported), result.reply);
    EXPECT_TRUE(handshake.done());
}

TEST(Socks5HandshakeTest, UdpAssociateCommandIsRejected)
{
    Handshake handshake;
    handshake.feed(greeting({0x00}).data(), 3);
    std::string frame = request(0x01, std::string("\x00\x00\x00\x00", 4), 53);
    frame[1] = '\x03'; // UDP ASSOCIATE
    const FeedResult result = handshake.feed(frame.data(), frame.size());
    ASSERT_EQ(FeedKind::Failure, result.kind);
    ASSERT_EQ(buildReply(Reply::CommandNotSupported), result.reply);
}

TEST(Socks5HandshakeTest, UnknownAddressTypeFailsWithGeneralFailure)
{
    Handshake handshake;
    handshake.feed(greeting({0x00}).data(), 3);
    const std::string frame = request(0x05, std::string("\x01\x02\x03\x04\x05\x06", 6), 80);
    const FeedResult result = handshake.feed(frame.data(), frame.size());
    ASSERT_EQ(FeedKind::Failure, result.kind);
    ASSERT_EQ(buildReply(Reply::GeneralFailure), result.reply);
    EXPECT_TRUE(handshake.done());
}

TEST(Socks5HandshakeTest, RequestWrongVersionFailsWithoutReply)
{
    Handshake handshake;
    handshake.feed(greeting({0x00}).data(), 3);
    std::string frame = request(0x01, std::string("\x01\x02\x03\x04", 4), 80);
    frame[0] = '\x04';
    const FeedResult result = handshake.feed(frame.data(), frame.size());
    ASSERT_EQ(FeedKind::Failure, result.kind);
    EXPECT_TRUE(result.reply.empty());
    EXPECT_TRUE(handshake.done());
}

TEST(Socks5HandshakeTest, FeedAfterDoneReturnsNeedMore)
{
    Handshake handshake;
    handshake.feed(greeting({0x00}).data(), 3);
    const std::string frame = request(0x01, std::string("\x01\x01\x01\x01", 4), 22);
    ASSERT_EQ(FeedKind::Request, handshake.feed(frame.data(), frame.size()).kind);
    const char extra = '\x01';
    const FeedResult result = handshake.feed(&extra, 1);
    EXPECT_EQ(FeedKind::NeedMore, result.kind);
}

TEST(Socks5HandshakeTest, ReplyFrameShapeIsFixed)
{
    const std::string success = buildReply(Reply::Success);
    ASSERT_EQ(10u, success.size());
    EXPECT_EQ('\x05', success[0]);
    EXPECT_EQ('\x00', success[1]);
    EXPECT_EQ('\x00', success[2]);
    EXPECT_EQ('\x01', success[3]);
    for (size_t i = 4; i < success.size(); ++i) {
        EXPECT_EQ('\x00', success[i]);
    }
    const std::string refused = buildReply(Reply::ConnectionRefused);
    EXPECT_EQ('\x05', refused[0]);
    EXPECT_EQ('\x05', refused[1]);
    EXPECT_EQ(std::string("\x05\x00", 2), buildMethodSelection(true));
    EXPECT_EQ(std::string("\x05\xFF", 2), buildMethodSelection(false));
}
