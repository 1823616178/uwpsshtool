#include "io/SessionThread.h" // winsock2.h must precede windows.h.
#include "io/WinsockInit.h"

#include <gtest/gtest.h>

#include <windows.h>

#include <atomic>
#include <chrono>
#include <future>
#include <thread>

using namespace std::chrono_literals;
using sshclient::io::SessionThread;
using sshclient::io::WinsockInit;

namespace {

DWORD ProcessHandleCount()
{
    DWORD count = 0;
    return ::GetProcessHandleCount(::GetCurrentProcess(), &count) ? count : 0;
}

} // namespace

TEST(SessionThreadTest, StartPostStopLifecycle)
{
    SessionThread session;
    EXPECT_FALSE(session.isRunning());
    ASSERT_TRUE(session.start());
    EXPECT_TRUE(session.isRunning());
    EXPECT_TRUE(session.start());

    std::promise<std::thread::id> ranOn;
    auto ranOnFuture = ranOn.get_future();
    session.post([&] { ranOn.set_value(std::this_thread::get_id()); });
    ASSERT_EQ(std::future_status::ready, ranOnFuture.wait_for(5s));
    EXPECT_NE(std::this_thread::get_id(), ranOnFuture.get());

    session.stop();
    EXPECT_FALSE(session.isRunning());
    session.stop();
}

TEST(SessionThreadTest, PostBeforeStartRunsAfterStart)
{
    SessionThread session;
    std::promise<void> ran;
    auto ranFuture = ran.get_future();
    session.post([&] { ran.set_value(); });

    ASSERT_TRUE(session.start());
    EXPECT_EQ(std::future_status::ready, ranFuture.wait_for(5s));
    session.stop();
}

TEST(SessionThreadTest, StopClearsPendingTasksBeforeRestart)
{
    SessionThread session;
    ASSERT_TRUE(session.start());
    session.stop();

    std::atomic<bool> ran{false};
    session.post([&] { ran.store(true); });
    session.stop();

    ASSERT_TRUE(session.start());
    std::this_thread::sleep_for(100ms);
    EXPECT_FALSE(ran.load());
    session.stop();
}

TEST(SessionThreadTest, WinsockUsesProcessWideReferenceCounting)
{
    const unsigned int baseline = WinsockInit::activeReferencesForTesting();
    {
        WinsockInit first;
        WinsockInit second;
        ASSERT_TRUE(first.isValid());
        ASSERT_TRUE(second.isValid());
        EXPECT_EQ(baseline + 2, WinsockInit::activeReferencesForTesting());
    }
    EXPECT_EQ(baseline, WinsockInit::activeReferencesForTesting());
}

TEST(SessionThreadTest, ThousandCreateStartStopCyclesDoNotLeakHandles)
{
    // Warm up CRT/thread naming/Winsock lazy state before taking the baseline;
    // those process-wide handles are initialized once and are not session leaks.
    {
        SessionThread warmup;
        ASSERT_TRUE(warmup.start());
        warmup.stop();
    }
    const DWORD baseline = ProcessHandleCount();
    ASSERT_GT(baseline, 0U);

    constexpr int Iterations = 1000;
    for (int index = 0; index < Iterations; ++index) {
        SessionThread session;
        ASSERT_TRUE(session.start()) << "iteration " << index;
        session.stop();
        EXPECT_FALSE(session.isRunning());
    }

    EXPECT_EQ(baseline, ProcessHandleCount());
}
