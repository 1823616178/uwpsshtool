#pragma once

// F04: bidirectional byte pump between a TCP socket and an SSH channel
// (01-DESIGN.md section 11.2).
//
// Layering: BidirectionalPump is the testable core — it shuttles bytes between
// two abstract endpoints (SideA = the TCP socket, SideB = the SSH channel by
// convention) with bounded buffers in each direction, EOF half-close
// propagation, and backpressure interest hints. The Winsock/libssh2 binding
// lives in direct_tcpip.{h,cpp} (TcpEndpoint/ChannelEndpoint); unit tests
// inject scripted fakes (partial writes, EAGAIN stalls, EOF half-close).
//
// Drive model (loop thread only, no locking): the owner calls drive() whenever
// either side may have progressed (socket Readable/Writable events, session
// socket events for the channel side). drive() pulls from both endpoints,
// flushes both pending buffers, propagates observed EOFs, and reports what
// stalled so the owner can re-arm socket interests (EventLoop) and the session
// socket interest (SshSession::updateSocketInterest).
//
// Backpressure: a side is not read once its outbound buffer reaches
// maxBufferedBytes (wantRead returns false — the owner must drop the socket's
// Readable interest, i.e. "pause reading the socket while the channel write
// is blocked"). Completion: both sides EOF, both buffers empty, both half-
// close hooks done -> finished() (the owner then runs the graceful teardown:
// channel close handshake + socket close). A hard error on either side ->
// finished with FinishReason::Error (the owner tears down best-effort).
//
// Statistics: delivered bytes per direction are added into the shared
// TunnelStats (atomic, cross-thread readable for the F05 per-second rates).
//
// Pure logic: C++ standard library only.

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <string>

namespace sshclient {
namespace fwd {

// Per-tunnel counters (01-DESIGN.md section 11.2: active/cumulative count and
// up/down bytes). Shared between one listener and its connections; the pump
// only bumps the byte counters, connection open/close bumps the connection
// counters (see direct_tcpip.h). Non-copyable (atomics); share by shared_ptr.
struct TunnelStats {
    TunnelStats() = default;
    TunnelStats(const TunnelStats&) = delete;
    TunnelStats& operator=(const TunnelStats&) = delete;

    std::atomic<std::uint64_t> activeConnections{0};
    std::atomic<std::uint64_t> totalConnections{0};
    std::atomic<std::uint64_t> bytesUp{0};   // socket -> channel
    std::atomic<std::uint64_t> bytesDown{0}; // channel -> socket
};

// Read-only snapshot for UI polling (F05).
struct TunnelStatsSnapshot {
    std::uint64_t activeConnections = 0;
    std::uint64_t totalConnections = 0;
    std::uint64_t bytesUp = 0;
    std::uint64_t bytesDown = 0;
};

inline TunnelStatsSnapshot snapshotTunnelStats(const TunnelStats& stats)
{
    TunnelStatsSnapshot out;
    out.activeConnections = stats.activeConnections.load(std::memory_order_acquire);
    out.totalConnections = stats.totalConnections.load(std::memory_order_acquire);
    out.bytesUp = stats.bytesUp.load(std::memory_order_acquire);
    out.bytesDown = stats.bytesDown.load(std::memory_order_acquire);
    return out;
}

// Endpoint read outcome for one pull: Data carries bytes (possibly short);
// Eof = peer half-closed (socket FIN / channel EOF); Stalled = EAGAIN-style
// "try again on the next event"; Error = hard failure (RST / CHANNEL_CLOSED).
struct PumpReadResult {
    enum class Kind { Data, Eof, Stalled, Error };
    Kind kind = Kind::Stalled;
    std::string bytes;   // Data only (1..maxLen bytes)
    std::string message; // Error only (diagnostics)

    static PumpReadResult data(std::string data) { return {Kind::Data, std::move(data), {}}; }
    static PumpReadResult eof() { return {Kind::Eof, {}, {}}; }
    static PumpReadResult stalled() { return {Kind::Stalled, {}, {}}; }
    static PumpReadResult error(std::string message)
    {
        return {Kind::Error, {}, std::move(message)};
    }
};

// Endpoint write outcome for one push: Data carries the bytes consumed
// (possibly a partial prefix of the offered buffer); Stalled = nothing
// consumed, retry on the next writable event; Error = hard failure.
struct PumpWriteResult {
    enum class Kind { Data, Stalled, Error };
    Kind kind = Kind::Stalled;
    size_t bytes = 0;    // Data only (1..offered bytes)
    std::string message; // Error only (diagnostics)

    static PumpWriteResult data(size_t sent) { return {Kind::Data, sent, {}}; }
    static PumpWriteResult stalled() { return {Kind::Stalled, 0, {}}; }
    static PumpWriteResult error(std::string message)
    {
        return {Kind::Error, 0, std::move(message)};
    }
};

// Half-close propagation outcome: Done = the peer was told (or told
// best-effort); RetryLater = EAGAIN-style, call again on the next pass;
// GiveUp = the peer is gone, stop trying (completion proceeds regardless).
enum class EofSendResult { Done, RetryLater, GiveUp };

// One pump side (socket or channel). All calls are loop-thread only.
class IPumpEndpoint {
public:
    virtual ~IPumpEndpoint() = default;
    virtual PumpReadResult read(size_t maxLen) = 0;
    virtual PumpWriteResult write(const char* data, size_t len) = 0;
    virtual EofSendResult sendEof() = 0; // our side EOFed: tell the peer
};

enum class PumpFinishReason { NotFinished, GracefulEof, Error };

// Per-pass report: progress = any byte moved, buffer drained, EOF observed or
// hook completed; the stall flags let the owner re-arm interests and record
// the libssh2 outbound-blocked direction (SshSession::updateSocketInterest).
struct PumpDriveReport {
    bool progress = false;
    bool readStalledA = false;
    bool writeStalledA = false;
    bool readStalledB = false;
    bool writeStalledB = false;
};

class BidirectionalPump {
public:
    // Default per-direction buffer cap: backpressure engages early enough to
    // bound memory (~512 KiB per connection worst case) while staying far
    // above typical socket buffers so throughput does not suffer.
    static constexpr size_t kDefaultMaxBufferedBytes = 256 * 1024;
    static constexpr size_t kReadChunkBytes = 32 * 1024;

    // endpoints must outlive the pump; stats may be null (no accounting).
    // SideA = socket, SideB = channel (only matters for stats direction).
    BidirectionalPump(IPumpEndpoint& sideA, IPumpEndpoint& sideB, TunnelStats* stats = nullptr);
    ~BidirectionalPump() = default;

    BidirectionalPump(const BidirectionalPump&) = delete;
    BidirectionalPump& operator=(const BidirectionalPump&) = delete;

    void setMaxBufferedBytes(size_t bytes) { maxBufferedBytes_ = bytes; }

    // F05：注入本应从 SideA 读到但已被前置阶段（SOCKS5 握手）消费的字节
    //（客户端把应用数据与请求一并送达的流水线场景）。仅在泵尚未结束时调用；
    // 字节按顺序进入 A→B 缓冲，随下一次 drive() 送往 SideB。
    void injectAtoB(std::string bytes)
    {
        if (!bytes.empty()) {
            bufferAtoB_ += std::move(bytes);
        }
    }

    PumpDriveReport drive(); // loop thread only

    bool finished() const { return finished_; }
    PumpFinishReason finishReason() const { return finishReason_; }
    const std::string& errorMessage() const { return errorMessage_; }

    // Interest hints for the owner (all false once finished):
    // - wantReadA: pull from A (else drop the socket Readable interest);
    // - wantWriteA: B->A bytes pending (arm the socket Writable interest);
    // - wantReadB/wantWriteB: channel-side equivalents (no socket interest;
    //   the channel is re-driven from session socket events anyway).
    bool wantReadA() const;
    bool wantWriteA() const;
    bool wantReadB() const;
    bool wantWriteB() const;

    // Observation for unit tests.
    size_t bufferedAtoB() const { return bufferAtoB_.size(); }
    size_t bufferedBtoA() const { return bufferBtoA_.size(); }
    bool eofSeenA() const { return eofSeenA_; }
    bool eofSeenB() const { return eofSeenB_; }

private:
    PumpReadResult pullOne(IPumpEndpoint& endpoint, bool& eofFlag, std::string& buffer,
                           bool& progress);
    bool flushOne(IPumpEndpoint& endpoint, std::string& buffer, bool& progress,
                  bool& stalledFlag);
    bool propagateEof(bool eofSeen, const std::string& buffer, bool& hookDone,
                      IPumpEndpoint& peer, bool& progress);
    void finishWithError(std::string message);

    IPumpEndpoint& sideA_;
    IPumpEndpoint& sideB_;
    TunnelStats* stats_; // nullable, non-owning
    size_t maxBufferedBytes_ = kDefaultMaxBufferedBytes;

    std::string bufferAtoB_;
    std::string bufferBtoA_;
    bool eofSeenA_ = false;
    bool eofSeenB_ = false;
    bool eofSentToB_ = false; // A EOFed and B was told (or given up)
    bool eofSentToA_ = false; // B EOFed and A was told (or given up)
    bool finished_ = false;
    PumpFinishReason finishReason_ = PumpFinishReason::NotFinished;
    std::string errorMessage_;
};

} // namespace fwd
} // namespace sshclient
