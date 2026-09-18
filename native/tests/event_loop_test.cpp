#include <gtest/gtest.h>

#include <winsock2.h>
#include <ws2tcpip.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <future>
#include <mutex>
#include <thread>
#include <vector>

#include "io/EventLoop.h"

using namespace std::chrono_literals;
using sshclient::io::EventLoop;

namespace {

class LoopRunner final {
public:
    explicit LoopRunner(bool forcePollingFallback = false)
        : loop_(forcePollingFallback), thread_([this] { loop_.run(); }) {}
    ~LoopRunner()
    {
        loop_.stop();
        thread_.join();
    }

    EventLoop& loop() { return loop_; }
    std::thread::id threadId() const { return thread_.get_id(); }

private:
    EventLoop loop_;
    std::thread thread_;
};

struct UdpPair {
    SOCKET receiver = INVALID_SOCKET;
    SOCKET sender = INVALID_SOCKET;

    ~UdpPair()
    {
        if (receiver != INVALID_SOCKET) { ::closesocket(receiver); }
        if (sender != INVALID_SOCKET) { ::closesocket(sender); }
    }
};

bool CreateUdpPair(UdpPair& pair)
{
    pair.receiver = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    pair.sender = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (pair.receiver == INVALID_SOCKET || pair.sender == INVALID_SOCKET) {
        return false;
    }

    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = ::htonl(INADDR_LOOPBACK);
    address.sin_port = 0;
    if (::bind(pair.receiver, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) == SOCKET_ERROR) {
        return false;
    }
    int length = sizeof(address);
    return ::getsockname(pair.receiver, reinterpret_cast<sockaddr*>(&address), &length) == 0 &&
           ::connect(pair.sender, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) == 0;
}

} // namespace

TEST(EventLoopTest, PostTasksRunInOrderOnLoopThread)
{
    LoopRunner runner;
    std::mutex mutex;
    std::condition_variable changed;
    std::vector<int> order;
    std::vector<std::thread::id> threads;

    constexpr int TaskCount = 10;
    for (int index = 0; index < TaskCount; ++index) {
        runner.loop().post([&, index] {
            {
                std::lock_guard<std::mutex> lock(mutex);
                order.push_back(index);
                threads.push_back(std::this_thread::get_id());
            }
            changed.notify_one();
        });
    }

    std::unique_lock<std::mutex> lock(mutex);
    ASSERT_TRUE(changed.wait_for(lock, 5s, [&] { return order.size() == TaskCount; }));
    for (int index = 0; index < TaskCount; ++index) {
        EXPECT_EQ(index, order[index]);
        EXPECT_EQ(runner.threadId(), threads[index]);
    }
}

TEST(EventLoopTest, RunAfterFiresAndCanBeCancelled)
{
    LoopRunner runner;
    const auto started = EventLoop::Clock::now();
    std::promise<std::chrono::milliseconds> fired;
    auto firedFuture = fired.get_future();

    runner.loop().runAfter(50, [&] {
        fired.set_value(std::chrono::duration_cast<std::chrono::milliseconds>(
            EventLoop::Clock::now() - started));
    });
    ASSERT_EQ(std::future_status::ready, firedFuture.wait_for(5s));
    EXPECT_GE(firedFuture.get(), 40ms);

    std::atomic<bool> cancelledFired{false};
    const auto id = runner.loop().runAfter(30, [&] { cancelledFired.store(true); });
    runner.loop().cancelTimer(id);
    runner.loop().cancelTimer(id);
    std::this_thread::sleep_for(100ms);
    EXPECT_FALSE(cancelledFired.load());
}

TEST(EventLoopTest, RunEveryRepeatsUntilCancelled)
{
    LoopRunner runner;
    std::atomic<int> count{0};
    const auto id = runner.loop().runEvery(10, [&] { count.fetch_add(1); });
    ASSERT_NE(0U, id);

    const auto deadline = EventLoop::Clock::now() + 5s;
    while (count.load() < 3 && EventLoop::Clock::now() < deadline) {
        std::this_thread::sleep_for(2ms);
    }
    ASSERT_GE(count.load(), 3);

    runner.loop().cancelTimer(id);
    const int snapshot = count.load();
    std::this_thread::sleep_for(60ms);
    EXPECT_EQ(snapshot, count.load());
}

TEST(EventLoopTest, WakeupInterruptsBlockedPoll)
{
    LoopRunner runner;
    std::this_thread::sleep_for(20ms);

    std::promise<void> done;
    auto doneFuture = done.get_future();
    runner.loop().post([&] { done.set_value(); });

    EXPECT_EQ(std::future_status::ready, doneFuture.wait_for(500ms));
}

TEST(EventLoopTest, FiftyMillisecondPollingFallbackRunsPostedTasks)
{
    LoopRunner runner(true);
    EXPECT_FALSE(runner.loop().usesSocketWakeup());

    std::promise<void> done;
    auto doneFuture = done.get_future();
    runner.loop().post([&] { done.set_value(); });

    EXPECT_EQ(std::future_status::ready, doneFuture.wait_for(250ms));
}

TEST(EventLoopTest, SocketCallbackReceivesReadEventAndDoesNotOwnSocket)
{
    LoopRunner runner;
    UdpPair pair;
    ASSERT_TRUE(CreateUdpPair(pair));

    std::promise<short> received;
    auto receivedFuture = received.get_future();
    runner.loop().post([&] {
        if (!runner.loop().addSocket(pair.receiver, EventLoop::Readable,
            [&](SOCKET socket, short events) {
                char byte = 0;
                const int count = ::recv(socket, &byte, 1, 0);
                received.set_value(count == 1 && byte == 'x' ? events : 0);
            })) {
            received.set_value(0);
        }
    });
    ASSERT_EQ(1, ::send(pair.sender, "x", 1, 0));
    ASSERT_EQ(std::future_status::ready, receivedFuture.wait_for(5s));
    EXPECT_NE(0, receivedFuture.get() & EventLoop::Readable);

    std::promise<bool> removed;
    auto removedFuture = removed.get_future();
    runner.loop().post([&] { removed.set_value(runner.loop().removeSocket(pair.receiver)); });
    ASSERT_EQ(std::future_status::ready, removedFuture.wait_for(5s));
    EXPECT_TRUE(removedFuture.get());

    // removeSocket only unregisters. The caller still owns and can use both sockets.
    ASSERT_EQ(1, ::send(pair.sender, "y", 1, 0));
    char byte = 0;
    EXPECT_EQ(1, ::recv(pair.receiver, &byte, 1, 0));
    EXPECT_EQ('y', byte);
}
