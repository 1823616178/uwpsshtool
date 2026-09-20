#include "fwd/socks5.h"

#include <cstdio>

namespace sshclient {
namespace fwd {
namespace socks5 {
namespace {

constexpr std::uint8_t kVersion = 0x05;
constexpr std::uint8_t kCommandConnect = 0x01;
constexpr std::uint8_t kAddressIpv4 = 0x01;
constexpr std::uint8_t kAddressDomain = 0x03;
constexpr std::uint8_t kAddressIpv6 = 0x04;
constexpr std::uint8_t kMethodNoAuth = 0x00;

std::string toDec(std::uint32_t value)
{
    char text[16]{};
    ::sprintf_s(text, sizeof(text), "%u", static_cast<unsigned>(value));
    return std::string(text);
}

std::string toHex(std::uint32_t value)
{
    char text[16]{};
    ::sprintf_s(text, sizeof(text), "%x", static_cast<unsigned>(value));
    return std::string(text);
}

} // namespace

std::string buildReply(Reply code)
{
    std::string frame;
    frame.reserve(10);
    frame.push_back(static_cast<char>(kVersion));
    frame.push_back(static_cast<char>(code));
    frame.push_back('\x00');
    frame.push_back(static_cast<char>(kAddressIpv4));
    frame.append(6, '\x00'); // BND.ADDR 4 字节 + BND.PORT 2 字节
    return frame;
}

std::string buildMethodSelection(bool ok)
{
    std::string frame;
    frame.push_back(static_cast<char>(kVersion));
    frame.push_back(static_cast<char>(ok ? kMethodNoAuth : 0xFF));
    return frame;
}

FeedResult Handshake::feed(const char* data, size_t len)
{
    if (stage_ == Stage::Done) {
        return FeedResult{}; // NeedMore（调用方在 Request 后应直接中继）
    }
    if (data != nullptr && len > 0) {
        buffer_.append(data, len);
    }
    FeedResult result = stage_ == Stage::Greeting ? parseGreeting() : parseRequest();
    if (result.kind == FeedKind::Failure) {
        // 失败同样终止本连接的握手（桌面端 fail() 置 stage='done'）。
        stage_ = Stage::Done;
        buffer_.clear();
    }
    return result;
}

FeedResult Handshake::parseGreeting()
{
    const std::string& buffer = buffer_;
    if (buffer.size() < 2) {
        return FeedResult{}; // NeedMore
    }
    const std::uint8_t version = static_cast<std::uint8_t>(buffer[0]);
    if (version != kVersion) {
        FeedResult failure;
        failure.kind = FeedKind::Failure;
        failure.message = "不支持的 SOCKS 版本 " + toDec(version);
        return failure;
    }
    const std::uint8_t methodCount = static_cast<std::uint8_t>(buffer[1]);
    if (buffer.size() < 2 + static_cast<size_t>(methodCount)) {
        return FeedResult{}; // NeedMore
    }
    bool hasNoAuth = false;
    for (size_t i = 2; i < 2 + static_cast<size_t>(methodCount); ++i) {
        if (static_cast<std::uint8_t>(buffer[i]) == kMethodNoAuth) {
            hasNoAuth = true;
            break;
        }
    }
    buffer_.erase(0, 2 + static_cast<size_t>(methodCount));
    if (!hasNoAuth) {
        FeedResult failure;
        failure.kind = FeedKind::Failure;
        failure.reply = buildMethodSelection(false);
        failure.message = "客户端要求认证，本代理仅支持无认证";
        return failure;
    }
    stage_ = Stage::Request;
    FeedResult selected;
    selected.kind = FeedKind::Reply;
    selected.reply = buildMethodSelection(true);
    return selected;
}

FeedResult Handshake::parseRequest()
{
    const std::string& buffer = buffer_;
    if (buffer.size() < 4) {
        return FeedResult{}; // NeedMore
    }
    const std::uint8_t version = static_cast<std::uint8_t>(buffer[0]);
    if (version != kVersion) {
        FeedResult failure;
        failure.kind = FeedKind::Failure;
        failure.message = "不支持的 SOCKS 版本 " + toDec(version);
        return failure;
    }
    const std::uint8_t command = static_cast<std::uint8_t>(buffer[1]);
    if (command != kCommandConnect) {
        FeedResult failure;
        failure.kind = FeedKind::Failure;
        failure.reply = buildReply(Reply::CommandNotSupported);
        failure.message = "仅支持 CONNECT 命令（不支持 BIND / UDP）";
        return failure;
    }

    const std::uint8_t atyp = static_cast<std::uint8_t>(buffer[3]);
    std::string host;
    size_t addressEnd = 0; // 请求帧内地址字段之后的偏移（不含端口前）
    if (atyp == kAddressIpv4) {
        if (buffer.size() < 10) {
            return FeedResult{}; // NeedMore
        }
        host = toDec(static_cast<std::uint8_t>(buffer[4])) + "." +
               toDec(static_cast<std::uint8_t>(buffer[5])) + "." +
               toDec(static_cast<std::uint8_t>(buffer[6])) + "." +
               toDec(static_cast<std::uint8_t>(buffer[7]));
        addressEnd = 8;
    } else if (atyp == kAddressDomain) {
        const std::uint8_t domainLength = static_cast<std::uint8_t>(buffer[4]);
        if (buffer.size() < 5 + static_cast<size_t>(domainLength) + 2) {
            return FeedResult{}; // NeedMore
        }
        host.assign(buffer, 5, static_cast<size_t>(domainLength));
        addressEnd = 5 + static_cast<size_t>(domainLength);
    } else if (atyp == kAddressIpv6) {
        if (buffer.size() < 22) {
            return FeedResult{}; // NeedMore
        }
        // 与桌面端一致：逐组小写十六进制、冒分、不压缩零组。
        for (int i = 0; i < 16; i += 2) {
            const std::uint16_t group = (static_cast<std::uint8_t>(buffer[4 + i]) << 8) |
                                         static_cast<std::uint8_t>(buffer[5 + i]);
            if (i > 0) {
                host.push_back(':');
            }
            host += toHex(group);
        }
        addressEnd = 20;
    } else {
        FeedResult failure;
        failure.kind = FeedKind::Failure;
        failure.reply = buildReply(Reply::GeneralFailure);
        failure.message = "不支持的地址类型 " + toDec(atyp);
        return failure;
    }

    const std::uint16_t port = static_cast<std::uint16_t>(
        (static_cast<std::uint8_t>(buffer[addressEnd]) << 8) |
        static_cast<std::uint8_t>(buffer[addressEnd + 1]));
    stage_ = Stage::Done;
    FeedResult parsed;
    parsed.kind = FeedKind::Request;
    parsed.host = std::move(host);
    parsed.port = port;
    parsed.leftover = buffer_.substr(addressEnd + 2);
    buffer_.clear();
    return parsed;
}

} // namespace socks5
} // namespace fwd
} // namespace sshclient
