#include "fwd/pump.h"

namespace sshclient {
namespace fwd {

BidirectionalPump::BidirectionalPump(IPumpEndpoint& sideA, IPumpEndpoint& sideB,
                                     TunnelStats* stats)
    : sideA_(sideA), sideB_(sideB), stats_(stats)
{
}

bool BidirectionalPump::wantReadA() const
{
    return !finished_ && !eofSeenA_ && bufferAtoB_.size() < maxBufferedBytes_;
}

bool BidirectionalPump::wantWriteA() const
{
    return !finished_ && !bufferBtoA_.empty();
}

bool BidirectionalPump::wantReadB() const
{
    return !finished_ && !eofSeenB_ && bufferBtoA_.size() < maxBufferedBytes_;
}

bool BidirectionalPump::wantWriteB() const
{
    return !finished_ && !bufferAtoB_.empty();
}

PumpReadResult BidirectionalPump::pullOne(IPumpEndpoint& endpoint, bool& eofFlag,
                                          StreamBuffer& buffer, bool& progress)
{
    if (eofFlag || buffer.size() >= maxBufferedBytes_) {
        return PumpReadResult::stalled(); // backpressure: caller checks the hint
    }
    const size_t room = maxBufferedBytes_ - buffer.size();
    const size_t want = room < kReadChunkBytes ? room : kReadChunkBytes;
    PumpReadResult result = endpoint.read(want);
    if (result.kind == PumpReadResult::Kind::Data) {
        // A misbehaving endpoint must not smuggle more than asked (or EOF
        // framing breaks); truncate defensively rather than over-buffer.
        if (result.bytes.size() > want) {
            result.bytes.resize(want);
        }
        if (!result.bytes.empty()) {
            buffer.append(result.bytes);
            progress = true;
        }
    } else if (result.kind == PumpReadResult::Kind::Eof) {
        eofFlag = true;
        progress = true;
    } else if (result.kind == PumpReadResult::Kind::Error) {
        finishWithError(std::move(result.message));
    }
    return result;
}

bool BidirectionalPump::flushOne(IPumpEndpoint& endpoint, StreamBuffer& buffer, bool& progress,
                                 bool& stalledFlag)
{
    stalledFlag = false;
    // Drain the buffer in one pass (partial writes keep the remainder and
    // loop again): a single drive() carries a full round trip, mirroring
    // PendingWriteQueue::flush. The loop always terminates —every iteration
    // either consumes bytes or exits via stall/error/empty.
    while (!buffer.empty()) {
        PumpWriteResult result = endpoint.write(buffer.data(), buffer.size());
        if (result.kind == PumpWriteResult::Kind::Data) {
            size_t sent = result.bytes;
            if (sent > buffer.size()) {
                sent = buffer.size(); // defensive, see pullOne
            }
            if (sent > 0) {
                buffer.consume(sent); // O10：推进读偏移，不 memmove
                progress = true;
                continue;
            }
            stalledFlag = true; // zero progress: retry on the next event, no spin
            return true;
        }
        if (result.kind == PumpWriteResult::Kind::Error) {
            finishWithError(std::move(result.message));
            return false;
        }
        stalledFlag = true;
        return true;
    }
    return true;
}

bool BidirectionalPump::propagateEof(bool eofSeen, const StreamBuffer& buffer, bool& hookDone,
                                     IPumpEndpoint& peer, bool& progress)
{
    if (!eofSeen || !buffer.empty() || hookDone) {
        return true;
    }
    const EofSendResult result = peer.sendEof();
    if (result == EofSendResult::Done || result == EofSendResult::GiveUp) {
        hookDone = true;
        progress = true;
        return true;
    }
    return false; // RetryLater: keep the hook pending, completion waits
}

void BidirectionalPump::finishWithError(std::string message)
{
    if (finished_) {
        return;
    }
    finished_ = true;
    finishReason_ = PumpFinishReason::Error;
    errorMessage_ = std::move(message);
}

PumpDriveReport BidirectionalPump::drive()
{
    PumpDriveReport report;
    if (finished_) {
        return report;
    }
    bool progress = false;

    // Pull both sides first so a single pass can carry a full round trip.
    // (Byte counters accrue on delivery in the flush step below, not here.)
    const PumpReadResult readA = pullOne(sideA_, eofSeenA_, bufferAtoB_, progress);
    if (finished_) {
        report.progress = progress;
        return report;
    }
    const PumpReadResult readB = pullOne(sideB_, eofSeenB_, bufferBtoA_, progress);
    if (finished_) {
        report.progress = progress;
        return report;
    }
    report.readStalledA = readA.kind == PumpReadResult::Kind::Stalled;
    report.readStalledB = readB.kind == PumpReadResult::Kind::Stalled;

    // Flush both directions (partial writes keep the remainder buffered).
    // Counters accrue delivered bytes only (buffer shrink per direction).
    const size_t pendingAtoB = bufferAtoB_.size();
    if (!flushOne(sideB_, bufferAtoB_, progress, report.writeStalledB)) {
        report.progress = progress;
        return report; // error path already finished
    }
    if (stats_ != nullptr && bufferAtoB_.size() < pendingAtoB) {
        stats_->bytesUp.fetch_add(static_cast<std::uint64_t>(pendingAtoB - bufferAtoB_.size()),
                                  std::memory_order_relaxed);
    }
    const size_t pendingBtoA = bufferBtoA_.size();
    if (!flushOne(sideA_, bufferBtoA_, progress, report.writeStalledA)) {
        report.progress = progress;
        return report;
    }
    if (stats_ != nullptr && bufferBtoA_.size() < pendingBtoA) {
        stats_->bytesDown.fetch_add(static_cast<std::uint64_t>(pendingBtoA - bufferBtoA_.size()),
                                    std::memory_order_relaxed);
    }

    // Half-close propagation only after the direction's leftovers drained.
    propagateEof(eofSeenA_, bufferAtoB_, eofSentToB_, sideB_, progress);
    propagateEof(eofSeenB_, bufferBtoA_, eofSentToA_, sideA_, progress);

    if (eofSeenA_ && eofSeenB_ && bufferAtoB_.empty() && bufferBtoA_.empty() &&
        eofSentToB_ && eofSentToA_) {
        finished_ = true;
        finishReason_ = PumpFinishReason::GracefulEof;
        progress = true;
    }

    report.progress = progress;
    return report;
}

} // namespace fwd
} // namespace sshclient
