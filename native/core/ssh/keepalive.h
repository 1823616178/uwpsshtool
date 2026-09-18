#pragma once

// N07: keepalive silent-blackhole judgement — the testable pure-logic unit
// (judgement semantics below).
//
// Background: socket ERR / peer FIN / RST are caught within a beat by WSAPoll
// events; a "cable pulled, no RST" silent blackhole produces no socket event
// at all and can only be detected by periodic keepalive probes. libssh2 only
// provides libssh2_keepalive_config + libssh2_keepalive_send (want_reply=1
// sends an SSH_MSG_GLOBAL_REQUEST demanding an answer); it has NO built-in
// unanswered-count or disconnect verdict (verified against 1.11.1
// keepalive.c). Counting and verdicts live here.
//
// Judgement semantics (used by session.cpp onKeepaliveTick; approximations
// stated honestly):
//   Each keepalive period observes once "was there ANY inbound activity this
//   period":
//     - signal 1: a Readable event was seen on the socket while Established
//       (with channels, the channel pump drains the socket right after, so
//       this signal covers normal traffic);
//     - signal 2: the socket's pending-read byte count (FIONREAD) grew since
//       the last period (without channels the keepalive reply sits unread in
//       the kernel buffer; growth proves the peer answered).
//   The two OR together — the approximation: this proves "the peer sent bytes
//   during the last period", it does not match each byte to a keepalive
//   reply; sufficient for liveness, and one-way outbound traffic is never
//   mistaken for aliveness (outbound is not inbound).
//   keepaliveMaxMisses consecutive periods with no inbound activity at all ->
//   blackhole verdict (tick returns true); the session goes to Disconnected
//   with KeepaliveTimeout (404). maxMisses == 0 = send-only, no verdict
//   (keeps the NAT/firewall session-refresh traffic value, gives up local
//   blackhole detection).
//
// Pure logic: C++ standard library only; asserted directly by host GoogleTest
// (tests/reconnect_policy_test.cpp).

namespace sshclient {
namespace ssh {

// Inbound-observation merge: Readable event flag OR pending-byte growth
// (pure function, for branch-by-branch assertions).
inline bool keepaliveInboundObserved(bool readableSeen, long pendingBytesNow,
                                     long pendingBytesBaseline)
{
    return readableSeen || pendingBytesNow > pendingBytesBaseline;
}

// Consecutive silence counter: tick(hadInbound) is called once per keepalive
// period; true = threshold reached, judge a blackhole.
class KeepaliveMissTracker {
public:
    // maxMisses == 0: judgement disabled (tick always returns false).
    explicit KeepaliveMissTracker(unsigned maxMisses) : maxMisses_(maxMisses) {}

    bool tick(bool hadInbound)
    {
        if (hadInbound) {
            misses_ = 0;
            return false;
        }
        if (maxMisses_ == 0) {
            return false; // send-only, no verdict
        }
        ++misses_;
        return misses_ >= maxMisses_;
    }

    unsigned misses() const { return misses_; }
    unsigned maxMisses() const { return maxMisses_; }
    void reset() { misses_ = 0; }

private:
    unsigned maxMisses_;
    unsigned misses_ = 0;
};

// Active-probe window: after a network change (Wi-Fi flip, VPN up/down) the
// old socket is most likely already a blackhole (new interface address, no
// RST will ever arrive) and the default 30 s x 3 keepalive judgement is too
// slow. probeNow() fires one keepalive immediately and opens a timeoutSec
// verdict window — no inbound activity inside the window = blackhole, feeding
// the existing disconnect/reconnect chain.
//
// The verdict signal is the same source as the periodic judgement
// (keepaliveInboundObserved: Readable event or FIONREAD growth), so this
// class only carries the pure "arm -> record baseline -> verdict at deadline"
// segment.
class KeepaliveProbe {
public:
    bool active() const { return active_; }
    long baseline() const { return baseline_; }

    // Arm the window: record the pending-byte baseline at the window start.
    // (When libssh2 skips the actual send — see session.cpp doProbeNow — the
    // caller passes the OLD baseline instead: resetting it would erase the
    // already-in-flight reply as evidence; keeping it is more conservative.)
    void arm(long pendingBaseline)
    {
        active_ = true;
        baseline_ = pendingBaseline;
    }

    void disarm() { active_ = false; }

    // Deadline verdict: any inbound activity inside the window = peer alive
    // (true); otherwise blackhole (false).
    bool verdictAlive(bool readableSeen, long pendingNow) const
    {
        return keepaliveInboundObserved(readableSeen, pendingNow, baseline_);
    }

private:
    bool active_ = false;
    long baseline_ = 0;
};

} // namespace ssh
} // namespace sshclient
