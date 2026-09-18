#pragma once

// N09a: deferral-aware decision gate for the WinRT event bridges
// (HostKeyCheck 60 s, AuthPrompt 120 s per the task spec).
//
// Problem: the I/O loop thread raises a WinRT event and needs the user's
// decision synchronously (the host-key callback must return a verdict). The
// handler may answer inline, or take a Deferral and answer later from the UI
// thread. The gate models exactly that:
//   - reset() before raising the event;
//   - the loop thread calls wait(): returns as soon as submit()/cancel()
//     lands, or after timeoutMs (fail-closed decision by the caller);
//   - defer() (GetDeferral) pauses the timeout while at least one deferral
//     is outstanding; completeDeferral() on the last one restarts a fresh
//     full window from that moment (user think time is unbounded only while
//     the handler explicitly holds the wait open);
//   - submit()/cancel() are idempotent-safe: the first conclusion wins,
//     later calls return false.
//
// Pure logic: standard library only; asserted by tests/bridge_logic_test.cpp.

#include <condition_variable>
#include <cstdint>
#include <mutex>
#include <optional>
#include <utility>

namespace sshclient {
namespace bridge {

enum class GateOutcome {
    Answered,  // submit() landed; out has the value
    Cancelled, // cancel() landed; caller applies its fail-default
    TimedOut,  // window elapsed without a conclusion; caller applies fail-default
};

template <typename T>
class DecisionGate {
public:
    // Loop thread, before raising the event. Clears any stale conclusion.
    void reset()
    {
        std::lock_guard<std::mutex> lock(mutex_);
        answer_.reset();
        concluded_ = false;
        deferrals_ = 0;
    }

    // Any thread (event handler): hold the window open.
    void defer()
    {
        std::lock_guard<std::mutex> lock(mutex_);
        ++deferrals_;
    }

    // Any thread: Deferral.Complete. The last completion restarts the window
    // (wait() measures a fresh timeoutMs from here).
    void completeDeferral()
    {
        bool kick = false;
        {
            std::lock_guard<std::mutex> lock(mutex_);
            if (deferrals_ == 0) {
                return;
            }
            --deferrals_;
            kick = deferrals_ == 0;
        }
        if (kick) {
            cv_.notify_all();
        }
    }

    // Any thread: conclude with a value (Accept/Respond). false = already
    // concluded; the first conclusion wins.
    bool submit(T value)
    {
        {
            std::lock_guard<std::mutex> lock(mutex_);
            if (concluded_) {
                return false;
            }
            concluded_ = true;
            answer_ = std::move(value);
        }
        cv_.notify_all();
        return true;
    }

    // Any thread: conclude without a value (Reject/Cancel).
    bool cancel()
    {
        {
            std::lock_guard<std::mutex> lock(mutex_);
            if (concluded_) {
                return false;
            }
            concluded_ = true;
        }
        cv_.notify_all();
        return true;
    }

    // Loop thread: block until a conclusion or the window elapses. While
    // deferrals are outstanding the deadline is suspended; when the last
    // deferral completes, a fresh timeoutMs window starts.
    GateOutcome wait(T& out, std::uint32_t timeoutMs)
    {
        std::unique_lock<std::mutex> lock(mutex_);
        for (;;) {
            if (concluded_) {
                if (answer_.has_value()) {
                    out = std::move(*answer_);
                    return GateOutcome::Answered;
                }
                return GateOutcome::Cancelled;
            }
            if (deferrals_ > 0) {
                cv_.wait(lock); // deferred: no deadline
                continue;
            }
            if (cv_.wait_for(lock, std::chrono::milliseconds(timeoutMs),
                             [&] { return concluded_ || deferrals_ > 0; })) {
                continue; // concluded or freshly deferred: re-evaluate
            }
            return GateOutcome::TimedOut;
        }
    }

private:
    std::mutex mutex_;
    std::condition_variable cv_;
    std::optional<T> answer_;
    bool concluded_ = false;
    unsigned deferrals_ = 0;
};

} // namespace bridge
} // namespace sshclient
