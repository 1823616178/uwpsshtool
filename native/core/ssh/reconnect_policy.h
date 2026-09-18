#pragma once

// N07: automatic-reconnect backoff policy — pure logic.
//
// Architecture split (task convention): reconnect orchestration lives in the
// upper layer (Core) — a reconnect needs a fresh socket and session, and the
// layer holding the host profile decides when to rebuild; native does not run
// a reconnect loop itself. This header only provides the pure computation of
// "how long to wait before attempt n, and when to give up", queried by the
// upper layer after stateChange(Disconnected) with a reconnect hint.
//
// Semantics (01-DESIGN section 7.1):
//   - default backoff sequence 1/2/5/10/20/30 seconds; attempt counts from 1
//     (wait 1 s before the first reconnect);
//   - attempts beyond the sequence length clamp to the last entry (30 s cap);
//   - default max attempts 6 (aligned with the 6-entry default sequence: six
//     attempts walk the whole sequence); 0 = retry forever (never give up);
//   - shouldGiveUp(attempt) answers "should the upcoming attempt-th reconnect
//     still happen": attempt > maxAttempts (non-infinite) -> give up.
//
// Pure logic: C++ standard library only; asserted directly by host GoogleTest
// (tests/reconnect_policy_test.cpp).

#include <cstdint>
#include <vector>

namespace sshclient {
namespace ssh {

class BackoffSchedule {
public:
    // Default backoff sequence (01-DESIGN section 7.1): 1->2->5->10->20->30 s.
    static const std::vector<std::uint32_t>& defaultDelaysSec()
    {
        static const std::vector<std::uint32_t> kDefault{1, 2, 5, 10, 20, 30};
        return kDefault;
    }
    // Default max attempts: 6 (matches the default sequence length); 0 = infinite.
    static constexpr std::uint32_t kDefaultMaxAttempts = 6;

    BackoffSchedule() : delays_(defaultDelaysSec()), maxAttempts_(kDefaultMaxAttempts) {}

    // An empty delaysSec falls back to the default sequence (defense: an empty
    // sequence has no delay entry to give). Entries are seconds; 0 is allowed
    // (immediate-retry entry). maxAttempts 0 = retry forever.
    explicit BackoffSchedule(std::vector<std::uint32_t> delaysSec,
                             std::uint32_t maxAttempts = kDefaultMaxAttempts)
        : delays_(delaysSec.empty() ? defaultDelaysSec() : std::move(delaysSec)),
          maxAttempts_(maxAttempts)
    {
    }

    // Suggested wait in seconds before the attempt-th reconnect. attempt
    // counts from 1; 0 is defensively clamped to the first attempt; beyond the
    // sequence length clamps to the last entry. This does NOT decide giving
    // up — callers ask shouldGiveUp first.
    std::uint32_t delayForAttempt(std::uint32_t attempt) const
    {
        if (attempt < 1) {
            attempt = 1;
        }
        const size_t idx = attempt - 1;
        return idx < delays_.size() ? delays_[idx] : delays_.back();
    }

    // Whether the upcoming attempt-th reconnect should be abandoned (cap
    // reached). Infinite mode (maxAttempts == 0) never gives up.
    bool shouldGiveUp(std::uint32_t attempt) const
    {
        return maxAttempts_ != 0 && attempt > maxAttempts_;
    }

    std::uint32_t maxAttempts() const { return maxAttempts_; }
    const std::vector<std::uint32_t>& delaysSec() const { return delays_; }

private:
    std::vector<std::uint32_t> delays_;
    std::uint32_t maxAttempts_;
};

} // namespace ssh
} // namespace sshclient
