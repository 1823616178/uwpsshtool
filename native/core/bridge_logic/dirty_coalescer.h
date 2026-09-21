#pragma once

// N09a/O01: ContentDirty coalescing gate (01-DESIGN section 4.2: "terminal
// bytes do not flow through events; the I/O thread posts ContentDirty
// coalesced — multiple dirties within one frame post once", and the §4.2
// sequence "若本帧未通知：ContentDirty 事件 → FrameScheduler.Wake()").
//
// O01 replaced the original consume-to-rearm flag with a time window. The old
// design needed the consumer to call markConsumed(); the only caller was the
// transitional FetchPendingOutput(), which T03's TerminalScreen replaced and
// O01 deleted — leaving the flag stuck at "posted" forever, so ContentDirty
// fired exactly once per session. A time window rearms itself, which is what
// "once per frame" meant in the first place and needs no consumer callback.
//
// Semantics: the producer (loop thread, per channel data burst) calls
// markDirty(nowMs); it returns true at most once per windowMs, and the caller
// posts one notification when it does. Dirties inside the window merge
// silently — the consumer repaints from TerminalScreen::Revision anyway, so a
// merged notification loses nothing. forcePost(nowMs) is for the one place
// that must notify regardless (channel close: the last output has to land).
//
// Thread safety: markDirty/forcePost are safe from any thread (lock-free CAS);
// in practice only the session's I/O loop thread calls them.
//
// Pure logic: standard library only; asserted by tests/bridge_logic_test.cpp.

#include <atomic>
#include <cstdint>
#include <limits>

namespace sshclient {
namespace bridge {

class DirtyCoalescer {
public:
    // One frame at 60 Hz. SP04 measured the per-canvas ceiling at ~30 draw/s,
    // so a 16 ms gate is already finer than anything the renderer can consume.
    static constexpr std::int64_t kDefaultWindowMs = 16;

    explicit DirtyCoalescer(std::int64_t windowMs = kDefaultWindowMs)
        : windowMs_(windowMs < 0 ? 0 : windowMs), lastPostMs_(kNever)
    {
    }

    // true = this dirty opened a new window; the caller should post one
    // notification. false = a notification already went out this window.
    bool markDirty(std::int64_t nowMs)
    {
        std::int64_t last = lastPostMs_.load(std::memory_order_acquire);
        for (;;) {
            // kNever must short-circuit: nowMs - kNever would overflow.
            if (last != kNever && nowMs - last < windowMs_) {
                return false;
            }
            if (lastPostMs_.compare_exchange_weak(last, nowMs,
                                                  std::memory_order_acq_rel,
                                                  std::memory_order_acquire)) {
                return true;
            }
            // CAS failed: `last` now holds the winner's value; re-evaluate.
        }
    }

    // The caller posts unconditionally and tells the gate, so the next
    // markDirty() still honours the window from this moment.
    void forcePost(std::int64_t nowMs)
    {
        lastPostMs_.store(nowMs, std::memory_order_release);
    }

    std::int64_t windowMs() const { return windowMs_; }

private:
    static constexpr std::int64_t kNever = (std::numeric_limits<std::int64_t>::min)();

    std::int64_t windowMs_;
    std::atomic<std::int64_t> lastPostMs_;
};

} // namespace bridge
} // namespace sshclient
