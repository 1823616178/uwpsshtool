// N07 pure-logic tests — the host-testable part of "keepalive and automatic
// reconnect policy".
//
// Covers:
//   BackoffSchedule (ssh/reconnect_policy.h):
//     - default sequence 1/2/5/10/20/30 entry by entry; clamp to the last
//       entry out of range; attempt=0 defensively treated as 1
//     - default cap 6: no give-up for 1..6, give up from 7
//     - infinite mode (maxAttempts=0): never gives up, delay still clamps to 30 s
//     - custom sequence and cap; empty sequence falls back to default; delay
//       clamping still applies when the cap exceeds the sequence length
//   KeepaliveMissTracker / keepaliveInboundObserved (ssh/keepalive.h):
//     - consecutive silence reaching the threshold judges a blackhole; inbound
//       resets the counter; maxMisses=0 is send-only; 1 judges immediately
//     - inbound observation merge: Readable event flag OR pending-byte growth
//   KeepaliveProbe (ssh/keepalive.h, active-probe verdict window):
//     - arm/disarm and baseline recording; deadline verdict uses the same
//       signals as the periodic judgement; re-arm swaps the baseline
//     - the stale-baseline case keeps an in-flight reply as alive evidence
//   isAutoReconnectable (ssh/session.h):
//     - Disconnected always reconnectable; Error classified link vs credential;
//       Closed and non-terminal states not reconnectable
//   SshSessionOptions defaults (30 s / 3 misses, 01-DESIGN section 7.1)
//
// A positive blackhole trigger (true silent packet loss) cannot be simulated
// on a loopback host without root, so the judgement logic lives entirely in
// the pure-function tests above; the integration side only covers "keepalive
// running normally does not misjudge" (tests/keepalive_test.cpp).
#include "ssh/session.h" // winsock2.h must precede windows.h/gtest.

#include <gtest/gtest.h>

#include <cstdint>
#include <vector>

#include "ssh/keepalive.h"
#include "ssh/reconnect_policy.h"

using sshclient::ssh::BackoffSchedule;
using sshclient::ssh::KeepaliveMissTracker;
using sshclient::ssh::KeepaliveProbe;
using sshclient::ssh::SshSessionError;
using sshclient::ssh::SshSessionOptions;
using sshclient::ssh::SshSessionState;
using sshclient::ssh::isAutoReconnectable;
using sshclient::ssh::keepaliveInboundObserved;

namespace {

// ================================================================== BackoffSchedule

TEST(BackoffScheduleTest, DefaultSequenceStepByStep)
{
    const BackoffSchedule s;
    // 01-DESIGN section 7.1 default sequence, entry by entry (attempt from 1)
    EXPECT_EQ(s.delayForAttempt(1), 1u);
    EXPECT_EQ(s.delayForAttempt(2), 2u);
    EXPECT_EQ(s.delayForAttempt(3), 5u);
    EXPECT_EQ(s.delayForAttempt(4), 10u);
    EXPECT_EQ(s.delayForAttempt(5), 20u);
    EXPECT_EQ(s.delayForAttempt(6), 30u);
    // Out of range clamps to the last entry (30 s cap)
    EXPECT_EQ(s.delayForAttempt(7), 30u);
    EXPECT_EQ(s.delayForAttempt(100), 30u);
    // Defense: attempt=0 is treated as the first attempt
    EXPECT_EQ(s.delayForAttempt(0), 1u);
    // Default cap and sequence contents
    EXPECT_EQ(s.maxAttempts(), 6u);
    EXPECT_EQ(s.delaysSec(), (std::vector<std::uint32_t>{1, 2, 5, 10, 20, 30}));
}

TEST(BackoffScheduleTest, GiveUpBoundaryAtDefaultMaxAttempts)
{
    const BackoffSchedule s; // maxAttempts = 6
    for (std::uint32_t attempt = 1; attempt <= 6; ++attempt) {
        EXPECT_FALSE(s.shouldGiveUp(attempt)) << "attempt=" << attempt;
    }
    EXPECT_TRUE(s.shouldGiveUp(7));
    EXPECT_TRUE(s.shouldGiveUp(100));
}

TEST(BackoffScheduleTest, InfiniteModeNeverGivesUp)
{
    const BackoffSchedule s({}, 0); // empty sequence + 0 = infinite
    EXPECT_FALSE(s.shouldGiveUp(1));
    EXPECT_FALSE(s.shouldGiveUp(6));
    EXPECT_FALSE(s.shouldGiveUp(1000));
    // Delays still clamp per the default sequence
    EXPECT_EQ(s.delayForAttempt(1), 1u);
    EXPECT_EQ(s.delayForAttempt(6), 30u);
    EXPECT_EQ(s.delayForAttempt(1000), 30u);
}

TEST(BackoffScheduleTest, CustomSequenceAndClamp)
{
    const BackoffSchedule s({3, 9}, 2);
    EXPECT_EQ(s.delayForAttempt(1), 3u);
    EXPECT_EQ(s.delayForAttempt(2), 9u);
    // Beyond the sequence length: clamps to the last entry
    EXPECT_EQ(s.delayForAttempt(3), 9u);
    // Cap 2: give up on the third attempt
    EXPECT_FALSE(s.shouldGiveUp(2));
    EXPECT_TRUE(s.shouldGiveUp(3));
}

TEST(BackoffScheduleTest, MaxAttemptsBeyondSequenceLength)
{
    const BackoffSchedule s({1, 2}, 5);
    EXPECT_EQ(s.delayForAttempt(1), 1u);
    EXPECT_EQ(s.delayForAttempt(2), 2u);
    EXPECT_EQ(s.delayForAttempt(3), 2u);
    EXPECT_EQ(s.delayForAttempt(5), 2u);
    EXPECT_FALSE(s.shouldGiveUp(5));
    EXPECT_TRUE(s.shouldGiveUp(6));
}

TEST(BackoffScheduleTest, EmptySequenceFallsBackToDefault)
{
    const BackoffSchedule s({}, 4);
    EXPECT_EQ(s.delaysSec(), BackoffSchedule::defaultDelaysSec());
    EXPECT_EQ(s.delayForAttempt(1), 1u);
    EXPECT_EQ(s.maxAttempts(), 4u);
}

TEST(BackoffScheduleTest, ZeroDelayEntryAllowed)
{
    // A 0-second entry is legal (immediate-retry first entry usage)
    const BackoffSchedule s({0, 5}, 2);
    EXPECT_EQ(s.delayForAttempt(1), 0u);
    EXPECT_EQ(s.delayForAttempt(2), 5u);
}

// ================================================================== KeepaliveMissTracker

TEST(KeepaliveMissTrackerTest, ConsecutiveSilenceReachesThreshold)
{
    KeepaliveMissTracker t(3);
    EXPECT_FALSE(t.tick(false)); // miss 1
    EXPECT_EQ(t.misses(), 1u);
    EXPECT_FALSE(t.tick(false)); // miss 2
    EXPECT_TRUE(t.tick(false));  // miss 3 -> blackhole verdict
    EXPECT_EQ(t.misses(), 3u);
}

TEST(KeepaliveMissTrackerTest, InboundResetsCounter)
{
    KeepaliveMissTracker t(3);
    EXPECT_FALSE(t.tick(false));
    EXPECT_FALSE(t.tick(false));
    EXPECT_FALSE(t.tick(true)); // inbound -> reset
    EXPECT_EQ(t.misses(), 0u);
    // Counts a fresh 3 before judging again
    EXPECT_FALSE(t.tick(false));
    EXPECT_FALSE(t.tick(false));
    EXPECT_TRUE(t.tick(false));
}

TEST(KeepaliveMissTrackerTest, ZeroMaxMissesDisablesJudgement)
{
    KeepaliveMissTracker t(0); // send-only, no verdict
    for (int i = 0; i < 10; ++i) {
        EXPECT_FALSE(t.tick(false));
    }
}

TEST(KeepaliveMissTrackerTest, OneMaxMissJudgesImmediately)
{
    KeepaliveMissTracker t(1);
    EXPECT_TRUE(t.tick(false));
}

TEST(KeepaliveMissTrackerTest, ResetClearsCounter)
{
    KeepaliveMissTracker t(3);
    t.tick(false);
    t.tick(false);
    t.reset();
    EXPECT_EQ(t.misses(), 0u);
    EXPECT_FALSE(t.tick(false));
}

// ================================================================== inbound observation merge

TEST(KeepaliveInboundObservedTest, EventFlagOrPendingGrowth)
{
    // A Readable event counts as inbound regardless of byte counts
    EXPECT_TRUE(keepaliveInboundObserved(true, 0, 0));
    EXPECT_TRUE(keepaliveInboundObserved(true, 48, 48));
    // Without an event, pending-byte growth counts (a keepalive reply sitting
    // unread in the kernel buffer in the channel-less scenario)
    EXPECT_TRUE(keepaliveInboundObserved(false, 96, 48));
    // No event and no growth (including flat/shrinking) -> no inbound
    EXPECT_FALSE(keepaliveInboundObserved(false, 48, 48));
    EXPECT_FALSE(keepaliveInboundObserved(false, 0, 48));
    EXPECT_FALSE(keepaliveInboundObserved(false, 0, 0));
}

// ================================================================== KeepaliveProbe (active-probe window)

TEST(KeepaliveProbeTest, InactiveUntilArmed)
{
    KeepaliveProbe probe;
    EXPECT_FALSE(probe.active());
    probe.arm(48);
    EXPECT_TRUE(probe.active());
    EXPECT_EQ(probe.baseline(), 48);
    probe.disarm();
    EXPECT_FALSE(probe.active());
    EXPECT_EQ(probe.baseline(), 48); // disarm only closes the window; the
                                     // baseline stays for the deadline verdict
}

TEST(KeepaliveProbeTest, VerdictFollowsInboundObservation)
{
    KeepaliveProbe probe;
    probe.arm(48);
    // Readable inside the window (with channels the probe reply gets drained
    // by the channel pump) -> alive
    EXPECT_TRUE(probe.verdictAlive(true, 48));
    // No event but pending-byte growth (reply left in the kernel buffer
    // without channels) -> alive
    EXPECT_TRUE(probe.verdictAlive(false, 96));
    // No event and no growth (including shrink) -> blackhole: the classic
    // shape of an old socket after a network change
    EXPECT_FALSE(probe.verdictAlive(false, 48));
    EXPECT_FALSE(probe.verdictAlive(false, 0));
}

TEST(KeepaliveProbeTest, RearmResetsBaseline)
{
    KeepaliveProbe probe;
    probe.arm(48);
    EXPECT_FALSE(probe.verdictAlive(false, 48));
    // A second probe arms with a new baseline: the same 96 bytes no longer
    // count as growth against it
    probe.arm(96);
    EXPECT_EQ(probe.baseline(), 96);
    EXPECT_FALSE(probe.verdictAlive(false, 96));
    EXPECT_TRUE(probe.verdictAlive(false, 97));
}

TEST(KeepaliveProbeTest, StaleBaselineKeepsInFlightReplyAsEvidence)
{
    // The session.cpp doProbeNow willSend == false branch: when libssh2 skips
    // the send, the old baseline stays and the previous tick's reply landing
    // inside the window counts as alive evidence (re-basing would erase it)
    KeepaliveProbe probe;
    const long staleBaseline = 48;
    probe.arm(staleBaseline);
    EXPECT_TRUE(probe.verdictAlive(false, 64)); // the previous tick's reply landed
}

TEST(KeepaliveProbeTest, DefaultTimeoutMatchesAcceptance)
{
    // Network-change reconnect goal: verdict within 5 s
    EXPECT_EQ(sshclient::ssh::kDefaultKeepaliveProbeTimeoutSec, 5u);
    // libssh2 1.11.1 keepalive.c minimum interval (doProbeNow borrows it to
    // force the send past the time gate)
    EXPECT_EQ(sshclient::ssh::kLibssh2MinKeepaliveIntervalSec, 2u);
}

// ================================================================== isAutoReconnectable

TEST(AutoReconnectableTest, DisconnectedAlwaysReconnectable)
{
    using E = SshSessionError;
    EXPECT_TRUE(isAutoReconnectable(SshSessionState::Disconnected, E::RemoteClosed));
    EXPECT_TRUE(isAutoReconnectable(SshSessionState::Disconnected, E::SocketError));
    EXPECT_TRUE(isAutoReconnectable(SshSessionState::Disconnected, E::KeepaliveTimeout));
    EXPECT_TRUE(isAutoReconnectable(SshSessionState::Disconnected, E::None));
}

TEST(AutoReconnectableTest, ErrorStateClassifiedByCode)
{
    using E = SshSessionError;
    const SshSessionState err = SshSessionState::Error;
    // Link class (transient network): reconnectable
    EXPECT_TRUE(isAutoReconnectable(err, E::DnsResolutionFailed));
    EXPECT_TRUE(isAutoReconnectable(err, E::ConnectionRefused));
    EXPECT_TRUE(isAutoReconnectable(err, E::NetworkUnreachable));
    EXPECT_TRUE(isAutoReconnectable(err, E::ConnectTimeout));
    EXPECT_TRUE(isAutoReconnectable(err, E::HandshakeFailed));
    EXPECT_TRUE(isAutoReconnectable(err, E::HandshakeTimeout));
    EXPECT_TRUE(isAutoReconnectable(err, E::SocketError));
    EXPECT_TRUE(isAutoReconnectable(err, E::AuthTimeout));
    // Credential, negotiation and local-resource classes: reconnecting helps not
    EXPECT_FALSE(isAutoReconnectable(err, E::AuthFailedPassword));
    EXPECT_FALSE(isAutoReconnectable(err, E::AuthFailedKey));
    EXPECT_FALSE(isAutoReconnectable(err, E::AuthFailedPassphrase));
    EXPECT_FALSE(isAutoReconnectable(err, E::AuthFailedInteractive));
    EXPECT_FALSE(isAutoReconnectable(err, E::NoLocalCredential));
    EXPECT_FALSE(isAutoReconnectable(err, E::AlgorithmNegotiationFailed));
    EXPECT_FALSE(isAutoReconnectable(err, E::InternalError));
    EXPECT_FALSE(isAutoReconnectable(err, E::HostKeyMismatch));
}

TEST(AutoReconnectableTest, ClosedAndNonTerminalNotReconnectable)
{
    using E = SshSessionError;
    EXPECT_FALSE(isAutoReconnectable(SshSessionState::Closed, E::None));
    EXPECT_FALSE(isAutoReconnectable(SshSessionState::Closed, E::HostKeyMismatch));
    EXPECT_FALSE(isAutoReconnectable(SshSessionState::Established, E::None));
    EXPECT_FALSE(isAutoReconnectable(SshSessionState::Connecting, E::ConnectTimeout));
}

// ================================================================== option defaults (DESIGN 7.1)

TEST(KeepaliveOptionsTest, DefaultsMatchDesign)
{
    const SshSessionOptions opts;
    EXPECT_EQ(opts.keepaliveIntervalSec, 30u);
    EXPECT_EQ(opts.keepaliveMaxMisses, 3u);
}

} // namespace
