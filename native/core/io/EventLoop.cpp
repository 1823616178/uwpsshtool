#include "EventLoop.h"
#include "debug_log.h"
#include "diag_counters.h"

#include <ws2tcpip.h>
#include <windows.h>

#include <algorithm>
#include <climits>
#include <cstdio>
#include <thread>
#include <utility>

#define IO_LOG(...) sshclient::diagnostics::debugLog("io", __VA_ARGS__)

namespace sshclient {
namespace io {

EventLoop::EventLoop(bool disableSocketWakeupForTesting)
{
    if (winsock_.isValid() && !disableSocketWakeupForTesting) {
        createWakeupPair();
    }
}

EventLoop::~EventLoop()
{
    // Q02：把未显式 removeSocket 的残留注册从计数器中扣除，保证计数可回归基线。
    diagnostics::GlobalDiagCounters().eventLoopSockets.fetch_sub(
        static_cast<std::int64_t>(registrations_.size()), std::memory_order_relaxed);
    closeWakeupPair();
}

bool EventLoop::addSocket(Socket socket, short events, SocketCallback callback)
{
    if (!isValid() || socket == INVALID_SOCKET || !callback || registrations_.count(socket) != 0) {
        return false;
    }
    registrations_.emplace(socket, Registration{events, std::move(callback)});
    diagnostics::GlobalDiagCounters().eventLoopSockets.fetch_add(1, std::memory_order_relaxed);
    return true;
}

bool EventLoop::modifySocket(Socket socket, short events)
{
    auto found = registrations_.find(socket);
    if (!isValid() || found == registrations_.end()) {
        return false;
    }
    found->second.events = events;
    return true;
}

bool EventLoop::removeSocket(Socket socket)
{
    const bool removed = registrations_.erase(socket) != 0;
    if (removed) {
        diagnostics::GlobalDiagCounters().eventLoopSockets.fetch_sub(1, std::memory_order_relaxed);
    }
    return removed;
}

void EventLoop::post(Task task)
{
    if (!task) {
        return;
    }
    {
        std::lock_guard<std::mutex> lock(taskMutex_);
        pendingTasks_.push_back(std::move(task));
    }
    wakeup();
}

EventLoop::TimerId EventLoop::runAfter(std::uint64_t delayMs, Task task)
{
    if (!task) {
        return 0;
    }
    const TimerId id = nextTimerId_.fetch_add(1);
    {
        std::lock_guard<std::mutex> lock(timerMutex_);
        auto timer = timers_.emplace(
            Clock::now() + std::chrono::milliseconds(delayMs), Timer{id, 0, std::move(task)});
        timerIndex_[id] = timer;
    }
    wakeup();
    return id;
}

EventLoop::TimerId EventLoop::runEvery(std::uint64_t intervalMs, Task task)
{
    if (intervalMs == 0 || !task) {
        return 0;
    }
    const TimerId id = nextTimerId_.fetch_add(1);
    {
        std::lock_guard<std::mutex> lock(timerMutex_);
        auto timer = timers_.emplace(
            Clock::now() + std::chrono::milliseconds(intervalMs),
            Timer{id, intervalMs, std::move(task)});
        timerIndex_[id] = timer;
    }
    wakeup();
    return id;
}

void EventLoop::cancelTimer(TimerId id)
{
    std::lock_guard<std::mutex> lock(timerMutex_);
    auto found = timerIndex_.find(id);
    if (found == timerIndex_.end()) {
        return;
    }
    timers_.erase(found->second);
    timerIndex_.erase(found);
}

void EventLoop::wakeup()
{
    if (wakeSender_ == INVALID_SOCKET) {
        return;
    }
    const char signal = 1;
    const int result = ::send(wakeSender_, &signal, 1, 0);
    if (result == SOCKET_ERROR) {
        const int error = ::WSAGetLastError();
        if (error != WSAEWOULDBLOCK) {
            IO_LOG("wakeup send failed error=%d", error);
        }
    }
}

void EventLoop::run()
{
    if (!isValid()) {
        IO_LOG("WSAStartup failed error=%d", winsock_.error());
        return;
    }

    while (!stopRequested_.load()) {
        runDueTimers();

        std::vector<WSAPOLLFD> pollDescriptors;
        pollDescriptors.reserve(registrations_.size() + (usesSocketWakeup() ? 1U : 0U));
        if (usesSocketWakeup()) {
            pollDescriptors.push_back(WSAPOLLFD{wakeReceiver_, Readable, 0});
        }
        for (const auto& registration : registrations_) {
            pollDescriptors.push_back(WSAPOLLFD{
                registration.first, registration.second.events, 0});
        }

        int timeoutMs = computeTimeoutMs();
        if (!usesSocketWakeup()) {
            timeoutMs = timeoutMs < 0 ? FallbackPollMs : (std::min)(timeoutMs, FallbackPollMs);
        }

        int ready = 0;
        if (pollDescriptors.empty()) {
            const int sleepMs = timeoutMs < 0 ? FallbackPollMs : timeoutMs;
            std::this_thread::sleep_for(std::chrono::milliseconds(sleepMs));
        } else {
            ready = ::WSAPoll(pollDescriptors.data(),
                              static_cast<ULONG>(pollDescriptors.size()), timeoutMs);
            if (ready == SOCKET_ERROR) {
                const int error = ::WSAGetLastError();
                if (error == WSAEINTR) {
                    continue;
                }
                IO_LOG("WSAPoll failed error=%d", error);
                break;
            }
        }

        if (ready > 0) {
            for (const auto& descriptor : pollDescriptors) {
                if (descriptor.revents == 0) {
                    continue;
                }
                if (descriptor.fd == wakeReceiver_) {
                    drainWakeup();
                    continue;
                }

                SocketCallback callback;
                auto found = registrations_.find(descriptor.fd);
                if (found != registrations_.end()) {
                    callback = found->second.callback;
                }
                if (callback) {
                    callback(descriptor.fd, descriptor.revents);
                }
            }
        }

        runPostedTasks();
    }
}

void EventLoop::stop()
{
    stopRequested_.store(true);
    wakeup();
}

void EventLoop::clearPendingTasks()
{
    {
        std::lock_guard<std::mutex> lock(taskMutex_);
        pendingTasks_.clear();
    }
    {
        std::lock_guard<std::mutex> lock(timerMutex_);
        timers_.clear();
        timerIndex_.clear();
    }
}

bool EventLoop::createWakeupPair()
{
    Socket receiver = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    Socket sender = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (receiver == INVALID_SOCKET || sender == INVALID_SOCKET) {
        if (receiver != INVALID_SOCKET) { ::closesocket(receiver); }
        if (sender != INVALID_SOCKET) { ::closesocket(sender); }
        return false;
    }

    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = ::htonl(INADDR_LOOPBACK);
    address.sin_port = 0;
    if (::bind(receiver, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) == SOCKET_ERROR) {
        ::closesocket(receiver);
        ::closesocket(sender);
        return false;
    }

    int addressLength = sizeof(address);
    if (::getsockname(receiver, reinterpret_cast<sockaddr*>(&address), &addressLength) == SOCKET_ERROR ||
        ::connect(sender, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) == SOCKET_ERROR) {
        ::closesocket(receiver);
        ::closesocket(sender);
        return false;
    }

    u_long nonBlocking = 1;
    if (::ioctlsocket(receiver, FIONBIO, &nonBlocking) == SOCKET_ERROR ||
        ::ioctlsocket(sender, FIONBIO, &nonBlocking) == SOCKET_ERROR) {
        ::closesocket(receiver);
        ::closesocket(sender);
        return false;
    }

    // Probe the complete loopback path now. Some AppContainer policies allow
    // socket creation but reject the first datagram; those devices use the
    // bounded polling fallback instead.
    const char probe = 1;
    WSAPOLLFD probeDescriptor{receiver, Readable, 0};
    if (::send(sender, &probe, 1, 0) != 1 || ::WSAPoll(&probeDescriptor, 1, 100) != 1 ||
        (probeDescriptor.revents & Readable) == 0) {
        ::closesocket(receiver);
        ::closesocket(sender);
        return false;
    }
    char buffer[16];
    ::recv(receiver, buffer, sizeof(buffer), 0);

    wakeReceiver_ = receiver;
    wakeSender_ = sender;
    return true;
}

void EventLoop::closeWakeupPair()
{
    if (wakeReceiver_ != INVALID_SOCKET) {
        ::closesocket(wakeReceiver_);
        wakeReceiver_ = INVALID_SOCKET;
    }
    if (wakeSender_ != INVALID_SOCKET) {
        ::closesocket(wakeSender_);
        wakeSender_ = INVALID_SOCKET;
    }
}

void EventLoop::drainWakeup()
{
    char buffer[64];
    while (::recv(wakeReceiver_, buffer, sizeof(buffer), 0) > 0) {
    }
}

void EventLoop::runPostedTasks()
{
    std::vector<Task> tasks;
    {
        std::lock_guard<std::mutex> lock(taskMutex_);
        tasks.swap(pendingTasks_);
    }
    for (auto& task : tasks) {
        task();
    }
}

void EventLoop::runDueTimers()
{
    std::vector<Task> dueTasks;
    const Clock::time_point now = Clock::now();
    {
        std::lock_guard<std::mutex> lock(timerMutex_);
        auto timer = timers_.begin();
        while (timer != timers_.end() && timer->first <= now) {
            Timer value = std::move(timer->second);
            timerIndex_.erase(value.id);
            timer = timers_.erase(timer);
            if (value.intervalMs > 0) {
                auto next = timers_.emplace(
                    now + std::chrono::milliseconds(value.intervalMs), value);
                timerIndex_[value.id] = next;
            }
            dueTasks.push_back(std::move(value.task));
        }
    }
    for (auto& task : dueTasks) {
        task();
    }
}

int EventLoop::computeTimeoutMs()
{
    std::lock_guard<std::mutex> lock(timerMutex_);
    if (timers_.empty()) {
        return -1;
    }
    const auto remaining = timers_.begin()->first - Clock::now();
    if (remaining <= Clock::duration::zero()) {
        return 0;
    }
    const auto milliseconds =
        std::chrono::duration_cast<std::chrono::milliseconds>(remaining).count();
    return static_cast<int>((std::min<std::int64_t>)(milliseconds, INT_MAX));
}

} // namespace io
} // namespace sshclient
