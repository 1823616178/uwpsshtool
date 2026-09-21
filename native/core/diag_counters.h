#pragma once

// Q02：native 资源生命周期计数器。Debug 与 Release 都编译——真机 Release ARM
// 采数时同样要能读数；每次增减是一次原子 fetch_add/fetch_sub，开销可忽略。
//
// 计数口径（连接/断开循环结束后应全部回到循环前基线，回不去的项即泄漏点，
// 见 doc/PERF-REPORT.md Q02 节）：
//   sshSessions      —— ssh::SshSession 存活数（构造 +1 / 析构 -1）
//   sessionThreads   —— io::SessionThread 存活数
//   eventLoopSockets —— EventLoop 当前注册的 socket 数（析构时扣除残留）
//   nativeScreens    —— Bridge TerminalScreen 存活数（终端网格对象）

#include <atomic>
#include <cstdint>
#include <string>

namespace sshclient {
namespace diagnostics {

struct DiagCounters final {
    std::atomic<std::int64_t> sshSessions{0};
    std::atomic<std::int64_t> sessionThreads{0};
    std::atomic<std::int64_t> eventLoopSockets{0};
    std::atomic<std::int64_t> nativeScreens{0};

    std::int64_t SshSessions() const { return sshSessions.load(std::memory_order_relaxed); }
    std::int64_t SessionThreads() const { return sessionThreads.load(std::memory_order_relaxed); }
    std::int64_t EventLoopSockets() const { return eventLoopSockets.load(std::memory_order_relaxed); }
    std::int64_t NativeScreens() const { return nativeScreens.load(std::memory_order_relaxed); }
};

DiagCounters& GlobalDiagCounters();

// "sessions=N threads=N sockets=N screens=N"（键序恒定，供调试页直接展示/落盘）
std::string DiagCountersSnapshot();

} // namespace diagnostics
} // namespace sshclient
