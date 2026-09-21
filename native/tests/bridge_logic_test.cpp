// N09a bridge pure-logic tests — native/core/bridge_logic/.
//
// DirtyCoalescer: the ContentDirty merge gate (01-DESIGN section 4.2:
// dirties within one frame post once). O01 replaced the consume-to-rearm
// flag with a self-rearming time window; the clock is supplied by the caller
// so these tests are deterministic.
// DecisionGate: the deferral-aware wait behind HostKeyCheck/AuthPrompt
// (submit/cancel/timeout, deferral pauses the timeout, completing the last
// deferral restarts a fresh window, first conclusion wins).
#include <gtest/gtest.h>

#include <atomic>
#include <chrono>
#include <cstdint>
#include <thread>

#include "bridge_logic/decision_gate.h"
#include "bridge_logic/dirty_coalescer.h"

using namespace std::chrono_literals;
using sshclient::bridge::DecisionGate;
using sshclient::bridge::DirtyCoalescer;
using sshclient::bridge::GateOutcome;

namespace {

// ================================================================== DirtyCoalescer

TEST(DirtyCoalescerTest, FirstDirtyNotifiesRestOfWindowMerges)
{
    DirtyCoalescer c(16);
    EXPECT_TRUE(c.markDirty(1000));  // first ever dirty always posts
    EXPECT_FALSE(c.markDirty(1000)); // same instant: merged
    EXPECT_FALSE(c.markDirty(1015)); // still inside the 16 ms window
}

TEST(DirtyCoalescerTest, WindowRearmsItself)
{
    DirtyCoalescer c(16);
    EXPECT_TRUE(c.markDirty(1000));
    EXPECT_FALSE(c.markDirty(1015));
    EXPECT_TRUE(c.markDirty(1016)); // window elapsed: post again
    EXPECT_FALSE(c.markDirty(1031));
    EXPECT_TRUE(c.markDirty(1032));
}

// The regression O01 was written for: with no consumer calling anything, the
// gate must keep posting. The old flag stayed set forever once nobody pulled.
TEST(DirtyCoalescerTest, KeepsPostingWithoutAnyConsumer)
{
    DirtyCoalescer c(16);
    int posts = 0;
    for (std::int64_t now = 0; now < 1000; now += 4) { // 250 dirties over 1 s
        if (c.markDirty(now)) {
            ++posts;
        }
    }
    // posts land at t = 0, 16, 32 ... 992 -> 63, i.e. one per window rather
    // than the single post the old consume-to-rearm flag would have produced.
    EXPECT_EQ(posts, 63);
}

// Channel close must notify even mid-window, and must not leave the window
// open behind it.
TEST(DirtyCoalescerTest, ForcePostOverridesWindowAndResetsIt)
{
    DirtyCoalescer c(16);
    EXPECT_TRUE(c.markDirty(1000));
    c.forcePost(1005); // caller posted unconditionally
    EXPECT_FALSE(c.markDirty(1015)); // window now measured from 1005
    EXPECT_TRUE(c.markDirty(1021));
}

TEST(DirtyCoalescerTest, ZeroWindowPostsEveryDirty)
{
    DirtyCoalescer c(0);
    EXPECT_TRUE(c.markDirty(1000));
    EXPECT_TRUE(c.markDirty(1000));
    EXPECT_TRUE(c.markDirty(1000));
}

// A burst from the producer thread must never post more than one notification
// per window, and never lose the window's first one.
TEST(DirtyCoalescerTest, ConcurrentBurstCoalescesWithinWindow)
{
    DirtyCoalescer c(16);
    std::atomic<int> notifications{0};
    std::thread a([&] {
        for (int i = 0; i < 1000; ++i) {
            if (c.markDirty(5000)) {
                notifications.fetch_add(1);
            }
        }
    });
    std::thread b([&] {
        for (int i = 0; i < 1000; ++i) {
            if (c.markDirty(5000)) {
                notifications.fetch_add(1);
            }
        }
    });
    a.join();
    b.join();
    EXPECT_EQ(notifications.load(), 1);
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
