// N09a bridge pure-logic tests — native/core/bridge_logic/.
//
// DirtyCoalescer: the ContentDirty merge flag (01-DESIGN section 4.2:
// dirties within one frame post once; the consumer's pull rearms).
// DecisionGate: the deferral-aware wait behind HostKeyCheck/AuthPrompt
// (submit/cancel/timeout, deferral pauses the timeout, completing the last
// deferral restarts a fresh window, first conclusion wins).
#include <gtest/gtest.h>

#include <atomic>
#include <chrono>
#include <thread>

#include "bridge_logic/decision_gate.h"
#include "bridge_logic/dirty_coalescer.h"

using namespace std::chrono_literals;
using sshclient::bridge::DecisionGate;
using sshclient::bridge::DirtyCoalescer;
using sshclient::bridge::GateOutcome;

namespace {

// ================================================================== DirtyCoalescer

TEST(DirtyCoalescerTest, FirstDirtyNotifiesRestMerge)
{
    DirtyCoalescer c;
    EXPECT_FALSE(c.pending());
    EXPECT_TRUE(c.markDirty());  // clear -> dirty: notify once
    EXPECT_FALSE(c.markDirty()); // already pending: merged
    EXPECT_FALSE(c.markDirty());
    EXPECT_TRUE(c.pending());
}

TEST(DirtyCoalescerTest, ConsumeRearmsNotification)
{
    DirtyCoalescer c;
    EXPECT_TRUE(c.markDirty());
    c.markConsumed(); // consumer pulled the content
    EXPECT_FALSE(c.pending());
    EXPECT_TRUE(c.markDirty()); // next dirty notifies again
}

// Producer bursts from another thread while the consumer lags: exactly one
// notification until the consume.
TEST(DirtyCoalescerTest, ConcurrentBurstCoalesces)
{
    DirtyCoalescer c;
    std::atomic<int> notifications{0};
    std::thread producer([&] {
        for (int i = 0; i < 1000; ++i) {
            if (c.markDirty()) {
                notifications.fetch_add(1);
            }
        }
    });
    producer.join();
    EXPECT_EQ(notifications.load(), 1);
    EXPECT_TRUE(c.pending());
}

// ================================================================== DecisionGate

TEST(DecisionGateTest, SubmitBeforeWaitAnswersImmediately)
{
    DecisionGate<int> gate;
    gate.reset();
    ASSERT_TRUE(gate.submit(42));
    int out = 0;
    EXPECT_EQ(gate.wait(out, 50), GateOutcome::Answered);
    EXPECT_EQ(out, 42);
}

TEST(DecisionGateTest, CancelConcludesWithoutValue)
{
    DecisionGate<int> gate;
    gate.reset();
    ASSERT_TRUE(gate.cancel());
    int out = 0;
    EXPECT_EQ(gate.wait(out, 50), GateOutcome::Cancelled);
}

TEST(DecisionGateTest, TimeoutWithoutAnswer)
{
    DecisionGate<int> gate;
    gate.reset();
    const auto started = std::chrono::steady_clock::now();
    int out = 0;
    EXPECT_EQ(gate.wait(out, 150), GateOutcome::TimedOut);
    EXPECT_GE(std::chrono::steady_clock::now() - started, 140ms);
}

TEST(DecisionGateTest, SubmitFromAnotherThreadWakesWaiter)
{
    DecisionGate<int> gate;
    gate.reset();
    std::thread producer([&] {
        std::this_thread::sleep_for(50ms);
        gate.submit(7);
    });
    int out = 0;
    EXPECT_EQ(gate.wait(out, 5000), GateOutcome::Answered);
    EXPECT_EQ(out, 7);
    producer.join();
}

TEST(DecisionGateTest, FirstConclusionWins)
{
    DecisionGate<int> gate;
    gate.reset();
    EXPECT_TRUE(gate.submit(1));
    EXPECT_FALSE(gate.submit(2));   // too late
    EXPECT_FALSE(gate.cancel());    // too late
    int out = 0;
    EXPECT_EQ(gate.wait(out, 50), GateOutcome::Answered);
    EXPECT_EQ(out, 1);
}

// GetDeferral semantics: while a deferral is outstanding the timeout does
// not fire; the answer can land far past the original window.
TEST(DecisionGateTest, DeferralPausesTimeout)
{
    DecisionGate<int> gate;
    gate.reset();
    gate.defer(); // handler took a Deferral
    std::thread ui([&] {
        std::this_thread::sleep_for(300ms); // user think time > the 100 ms window
        gate.submit(9);
    });
    const auto started = std::chrono::steady_clock::now();
    int out = 0;
    EXPECT_EQ(gate.wait(out, 100), GateOutcome::Answered);
    EXPECT_GE(std::chrono::steady_clock::now() - started, 290ms);
    EXPECT_EQ(out, 9);
    ui.join();
}

// Completing the last deferral without a decision restarts a fresh window:
// the timeout applies from the completion, not from the original wait start.
TEST(DecisionGateTest, CompletingDeferralRestartsWindow)
{
    DecisionGate<int> gate;
    gate.reset();
    gate.defer();
    std::thread ui([&] {
        std::this_thread::sleep_for(150ms); // past the 100 ms window, still deferred
        gate.completeDeferral();
        // no decision: the fresh 100 ms window runs out from here
    });
    const auto started = std::chrono::steady_clock::now();
    int out = 0;
    EXPECT_EQ(gate.wait(out, 100), GateOutcome::TimedOut);
    const auto elapsed = std::chrono::steady_clock::now() - started;
    EXPECT_GE(elapsed, 240ms); // 150 ms deferred + ~100 ms fresh window
    ui.join();
}

TEST(DecisionGateTest, MultipleDeferralsAllMustComplete)
{
    DecisionGate<int> gate;
    gate.reset();
    gate.defer();
    gate.defer();
    std::thread ui([&] {
        std::this_thread::sleep_for(120ms); // beyond the 60 ms window
        gate.completeDeferral();            // one left: still paused
        std::this_thread::sleep_for(120ms);
        gate.completeDeferral();            // window restarts now
        gate.submit(5);
    });
    int out = 0;
    EXPECT_EQ(gate.wait(out, 60), GateOutcome::Answered);
    EXPECT_EQ(out, 5);
    ui.join();
}

TEST(DecisionGateTest, ResetDiscardsStaleConclusion)
{
    DecisionGate<int> gate;
    gate.reset();
    ASSERT_TRUE(gate.submit(1));
    gate.reset(); // a new round must not consume the stale answer
    int out = 0;
    EXPECT_EQ(gate.wait(out, 100), GateOutcome::TimedOut);
}

} // namespace
