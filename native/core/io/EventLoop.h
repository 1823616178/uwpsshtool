#pragma once

#include <winsock2.h>

#include <atomic>
#include <chrono>
#include <cstdint>
#include <functional>
#include <map>
#include <mutex>
#include <unordered_map>
#include <vector>

#include "WinsockInit.h"

namespace sshclient {
namespace io {

// Per-session non-blocking I/O loop. Socket callbacks and posted work execute
// on run()'s thread. Socket registration is loop-thread-only; tasks, timers,
// wakeup and stop are thread-safe.
class EventLoop final {
public:
    using Socket = SOCKET;
    using SocketCallback = std::function<void(Socket socket, short events)>;
    using Task = std::function<void()>;
    using Clock = std::chrono::steady_clock;
    using TimerId = std::uint64_t;

    static constexpr short Readable = POLLRDNORM;
    static constexpr short Writable = POLLWRNORM;

    explicit EventLoop(bool disableSocketWakeupForTesting = false);
    ~EventLoop();

    EventLoop(const EventLoop&) = delete;
    EventLoop& operator=(const EventLoop&) = delete;

    bool isValid() const { return winsock_.isValid(); }
    bool usesSocketWakeup() const { return wakeReceiver_ != INVALID_SOCKET; }

    bool addSocket(Socket socket, short events, SocketCallback callback);
    bool modifySocket(Socket socket, short events);
    bool removeSocket(Socket socket);

    void post(Task task);
    TimerId runAfter(std::uint64_t delayMs, Task task);
    TimerId runEvery(std::uint64_t intervalMs, Task task);
    void cancelTimer(TimerId id);

    void wakeup();
    void run();
    void rearm() { stopRequested_.store(false); }
    void stop();
    void clearPendingTasks();

private:
    struct Registration {
        short events;
        SocketCallback callback;
    };

    struct Timer {
        TimerId id;
        std::uint64_t intervalMs;
        Task task;
    };

    bool createWakeupPair();
    void closeWakeupPair();
    void drainWakeup();
    void runPostedTasks();
    void runDueTimers();
    int computeTimeoutMs();

    static constexpr int FallbackPollMs = 50;

    WinsockInit winsock_;
    Socket wakeReceiver_ = INVALID_SOCKET;
    Socket wakeSender_ = INVALID_SOCKET;
    std::atomic<bool> stopRequested_{false};

    // Loop thread only.
    std::unordered_map<Socket, Registration> registrations_;
    // O10：poll 描述符表复用。原先是 run() 循环体内的局部 vector，每次唤醒
    // （每个事件、每次定时器到期）都重新构造一次堆分配；N 条会话线程各一份。
    // 提升为成员后容量收敛到稳态 socket 数，之后 clear() 不再分配。
    std::vector<WSAPOLLFD> pollDescriptors_;

    std::mutex taskMutex_;
    std::vector<Task> pendingTasks_;

    std::mutex timerMutex_;
    std::multimap<Clock::time_point, Timer> timers_;
    std::unordered_map<TimerId, std::multimap<Clock::time_point, Timer>::iterator> timerIndex_;
    std::atomic<TimerId> nextTimerId_{1};
};

} // namespace io
} // namespace sshclient
