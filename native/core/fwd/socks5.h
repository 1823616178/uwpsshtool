#pragma once

// F05：SOCKS5 服务端握手解析（RFC 1928 子集；01-DESIGN.md §11.2 dynamic 分支）。
//
// 桌面端 src/main/tunnel/socks5.ts 的 C++ 移植：仅无认证 + CONNECT，地址支持
// IPv4 / 域名 / IPv6。纯逻辑（标准库 only），字节级行为与桌面端一致：
//   - 问候：VER≠5 → 直接失败不回复；方法表不含 0x00 → 回 [5,0xFF] 再失败；
//   - 请求：VER≠5 → 不回复直接失败；非 CONNECT → 回 0x07 再失败；未知 ATYP →
//     回 0x01 再失败；
//   - 请求后客户端已先行到达的应用数据（如 TLS ClientHello）从 leftover 返还，
//     由调用方在通道打通后先行注入泵（等效桌面端的 socket.unshift）。
//
// 纯逻辑：标准库 only，loop 线程外的单测可直接驱动。每条连接一个实例；
// Request/Failure 之后实例终止（done()），继续 feed 只会得到 NeedMore。

#include <cstddef>
#include <cstdint>
#include <string>

namespace sshclient {
namespace fwd {
namespace socks5 {

enum class Reply : std::uint8_t {
    Success = 0x00,
    GeneralFailure = 0x01,
    NetworkUnreachable = 0x03,
    HostUnreachable = 0x04,
    ConnectionRefused = 0x05,
    CommandNotSupported = 0x07,
};

// BND.ADDR/BND.PORT 填 0 的固定回复帧（VER, REP, RSV, ATYP=IPv4, 0,0,0,0, port=0）。
std::string buildReply(Reply code);

// 方法选择帧：ok=false 即「无可接受方法」（0xFF）。
std::string buildMethodSelection(bool ok);

enum class FeedKind { NeedMore, Reply, Request, Failure };

struct FeedResult {
    FeedKind kind = FeedKind::NeedMore;
    std::string reply;    // Reply/Failure：需写回客户端的字节（Failure 可为空 = 直接关闭）
    std::string host;     // Request：目标主机（IPv4 点分 / 域名 UTF-8 / IPv6 冒分小写）
    std::uint16_t port = 0; // Request：目标端口（主机序）
    std::string leftover; // Request：请求帧之后已到达的应用数据
    std::string message;  // Failure：诊断文本
};

class Handshake {
public:
    // 喂入一片到达的数据（可任意分片）；按裁决回写/推进/终止。
    FeedResult feed(const char* data, size_t len);

    bool done() const { return stage_ == Stage::Done; }

private:
    enum class Stage { Greeting, Request, Done };

    FeedResult parseGreeting();
    FeedResult parseRequest();

    Stage stage_ = Stage::Greeting;
    std::string buffer_;
};

} // namespace socks5
} // namespace fwd
} // namespace sshclient
