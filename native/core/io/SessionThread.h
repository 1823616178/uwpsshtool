#pragma once

#include <mutex>
#include <thread>

#include "EventLoop.h"

namespace sshclient {
namespace io {

class SessionThread final {
public:
    using Task = EventLoop::Task;

    SessionThread() = default;
    ~SessionThread();

    SessionThread(const SessionThread&) = delete;
    SessionThread& operator=(const SessionThread&) = delete;

    bool start();
    // Idempotent. Must be called outside the session thread because it joins
    // that thread synchronously; callbacks that need to exit call loop().stop().
    void stop();
    bool isRunning() const;

    void post(Task task) { loop_.post(std::move(task)); }
    EventLoop& loop() { return loop_; }

private:
    EventLoop loop_;
    mutable std::mutex mutex_;
    std::thread thread_;
};

} // namespace io
} // namespace sshclient
