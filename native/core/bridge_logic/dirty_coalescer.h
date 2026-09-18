#pragma once

// N09a: ContentDirty coalescing flag (01-DESIGN section 4.2: "terminal bytes
// do not flow through events; the I/O thread posts ContentDirty coalesced —
// multiple dirties within one frame post once").
//
// Semantics: the producer (loop thread, per channel data burst) calls
// markDirty(); only a false->true transition returns true = "notify now".
// While the flag stays set, further dirties merge silently. The consumer
// (C# pulling the terminal content) calls markConsumed() to reset, arming
// the next notification.
//
// Pure logic: standard library only; asserted by tests/bridge_logic_test.cpp.

#include <atomic>

namespace sshclient {
namespace bridge {

class DirtyCoalescer {
public:
    // true = the flag transitioned clear->dirty; the caller should post one
    // notification. false = a notification is already pending/unconsumed.
    bool markDirty()
    {
        return !dirty_.exchange(true, std::memory_order_acq_rel);
    }

    // Consumer pulled the content: reset so the next dirty notifies again.
    void markConsumed() { dirty_.store(false, std::memory_order_release); }

    bool pending() const { return dirty_.load(std::memory_order_acquire); }

private:
    std::atomic<bool> dirty_{false};
};

} // namespace bridge
} // namespace sshclient
