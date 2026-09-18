#pragma once

// N06: SSH channel and PTY (01-DESIGN.md section 7.2; depends on the N03
// session state machine and the N05 Established state).
//
// SshChannel binds one SshSession and covers three open forms (each a
// three-step EAGAIN-continuing pipeline):
//   - openShell:    channel_open_session -> request_pty -> process_startup("shell")
//   - exec:         channel_open_session -> process_startup("exec", command)
//   - execWithPty:  open_session -> request_pty -> process_startup("exec", command)
//     (one-shot commands that need a PTY, e.g. `stty size`, use this)
// Internal step machine: idle -> opening -> pty -> starting -> open. Every
// step can EAGAIN and is re-driven from socket events per
// libssh2_session_block_directions, same pattern as handshake/auth.
//
// Data plane:
//   - read: socket events dispatched by SshSession (driveChannels) pump
//     libssh2_channel_read_ex on both streams -- 0 = stdout,
//     SSH_EXTENDED_DATA_STDERR = stderr (libssh2 1.11 has no public API to
//     peel extended data off separately; with the default
//     EXTENDED_DATA_NORMAL mode stderr never mixes into stdout);
//   - write: write() is callable from any thread; admitted chunks queue onto
//     the loop thread; EAGAIN retains the remainder; a 4 MiB queue cap rejects
//     whole calls (backpressure, caller keeps the data);
//   - resize: libssh2_channel_request_pty_size_ex (pixel dims 0); in-flight
//     calls coalesce, the last one wins;
//   - EOF/exit: peer EOF -> drain leftovers -> close handshake (EAGAIN
//     continues) -> collect exit-status / exit-signal -> free -> onClose with
//     the reason (PeerEof / ExitStatus / ExitSignal / Error);
//   - sendEof(): tell the peer our input ended (exec("cat") style programs).
//
// Window adjust: no manual libssh2_channel_receive_window_adjust -- the read
// path replenishes the window internally as data is consumed (libssh2 1.11
// channel.c); the default 2 MiB window is plenty for terminals.
//
// Callback contract:
//   - onOpen fires exactly once: success -> kOpen; failure -> terminal, no onClose;
//   - onData fires 0..n times, only while open;
//   - onClose fires exactly once, only after a successful open;
//   - session loss notifies every registered channel via onSessionLost
//     (channels still opening get an onOpen failure instead).
//
// Threading (aligned with SshSession): openShell/exec/execWithPty/write/
// resize/sendEof/close/state are any-thread; admission posts to the session's
// loop thread (FIFO). All libssh2 calls and all callbacks run on the loop
// thread; receivers must not destroy the channel inside a callback. Destroy
// only after onOpen failure or onClose.
//
// Pure logic: standard library, WinSock and libssh2 public headers only.

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <functional>
#include <optional>
#include <string>
#include <utility>

// Forward declaration, keeps <libssh2.h> out of the public header (session.h
// convention).
struct _LIBSSH2_CHANNEL;

namespace sshclient {
namespace ssh {

class SshSession;

// Channel step machine: idle -> opening -> pty -> starting -> open -> closing
// -> closed (the pty step only for PTY forms; any non-terminal state can reach
// closing/closed on failure, local close or session loss).
enum class ChannelState {
    Idle,
    Opening,       // libssh2_channel_open_session driving
    RequestingPty, // libssh2_channel_request_pty_ex driving
    Starting,      // libssh2_channel_process_startup driving
    Open,          // ready: reads/writes/resize/sendEof in flight
    Closing,       // close handshake driving
    Closed,        // terminal: handle freed, unregistered, callbacks done
};

enum class ChannelStream {
    Stdout, // SSH_MSG_CHANNEL_DATA (stream 0)
    Stderr, // SSH_MSG_CHANNEL_EXTENDED_DATA (SSH_EXTENDED_DATA_STDERR)
};

enum class ChannelCloseReason {
    PeerEof,    // channel ended on EOF/close semantics without process exit
                // info (a clean local close also lands here)
    ExitStatus, // peer process exited normally; exitStatus valid
    ExitSignal, // peer process killed by a signal; exitSignal valid ("KILL" etc.)
    Error,      // transport/session loss or libssh2 error; message has details
};

enum class SshChannelError {
    None,
    NotEstablished, // session was not Established at admission (or raced away)
    OpenFailed,     // channel_open_session rejected (peer channel limit etc.)
    PtyFailed,      // request_pty rejected
    StartupFailed,  // process_startup (shell/exec) failed
    SessionLost,    // session dropped/closed while the channel was opening
};

struct PtySpec {
    std::string termType = "xterm-256color"; // 01-DESIGN section 7.2: truecolor
    std::uint32_t cols = 80;
    std::uint32_t rows = 24;
};

struct ChannelOpenResult {
    bool success = false;
    SshChannelError error = SshChannelError::None; // None on success
    std::string message;                           // failure diagnostics
};

struct ChannelCloseInfo {
    ChannelCloseReason reason = ChannelCloseReason::PeerEof;
    int exitStatus = 0;     // ExitStatus only
    std::string exitSignal; // ExitSignal only (no "SIG" prefix)
    std::string message;    // Error diagnostics / supplementary note
};

struct SshChannelCallbacks {
    std::function<void(const ChannelOpenResult& result)> onOpen; // exactly once
    // data is valid only for the duration of the call; copy if needed.
    std::function<void(const std::string& data, ChannelStream stream)> onData;
    std::function<void(const ChannelCloseInfo& info)> onClose; // exactly once, after open
};

// Pure-logic pending-write queue (loop-thread only, no locking): chunked FIFO
// with a head offset. The EAGAIN/partial-write semantics are unit-tested
// offline through flush() with a scripted writer.
class PendingWriteQueue {
public:
    void push(std::string chunk);

    // writer: >0 bytes sent, 0 = no progress, <0 = stalled (EAGAIN).
    // Partial sends advance the head offset; a stall keeps the remainder.
    // Returns true on any progress; *stalledOut (optional) marks the stall.
    bool flush(const std::function<ptrdiff_t(const char* data, size_t len)>& writer,
               bool* stalledOut = nullptr, size_t* flushedBytesOut = nullptr);

    void clear();
    bool empty() const { return chunks_.empty(); }
    size_t chunkCount() const { return chunks_.size(); }   // test observation
    size_t headOffset() const { return headOffset_; }      // test observation
    size_t remainingBytes() const; // queued bytes not yet sent (head offset aware)

private:
    std::deque<std::string> chunks_;
    size_t headOffset_ = 0;
};

// Pure-logic read classification for the read pump: libssh2_channel_read_ex
// returns >0 data, 0 EOF, LIBSSH2_ERROR_EAGAIN stalled, other <0 error.
enum class ChannelReadKind { Data, Eof, Stalled, Error };
ChannelReadKind classifyChannelRead(ptrdiff_t result);

class SshChannel {
public:
    // Pending-write cap (backpressure threshold): once admitted bytes exceed
    // this, write() rejects the whole call.
    static constexpr size_t kMaxPendingWriteBytes = 4 * 1024 * 1024;

    // Any-thread construction; callbacks immutable afterwards. session must
    // outlive the channel.
    SshChannel(SshSession& session, SshChannelCallbacks callbacks);
    ~SshChannel();

    SshChannel(const SshChannel&) = delete;
    SshChannel& operator=(const SshChannel&) = delete;

    // ---- open (any thread; only Idle admits, true = admitted) ----
    // Admission requires the session in Established and copies the arguments
    // before posting assembly to the loop thread. write/resize/sendEof are
    // accepted right after admission (they queue until the channel is ready).
    bool openShell(PtySpec pty);
    bool exec(std::string command);                     // no PTY
    bool execWithPty(PtySpec pty, std::string command); // one-shot command with PTY

    // ---- writes (any thread) ----
    // true = fully admitted (queued or sent); false = closed / never opened /
    // backpressure rejection (whole call rejected, caller retries later).
    bool write(const char* data, size_t len);
    bool write(const std::string& data) { return write(data.data(), data.size()); }

    // ---- PTY resize (any thread; only PTY channels with an admitted open) ----
    // In-flight calls coalesce, the last one wins. false = no PTY / not
    // admitted / closed.
    bool resize(std::uint32_t cols, std::uint32_t rows);

    // ---- env var (any thread) ----
    // Sends SSH_MSG_CHANNEL_REQUEST "env". Servers commonly restrict AcceptEnv
    // and reject these; per the task spec a rejection is logged and ignored,
    // never fatal. Vars queued before process_startup go out first.
    bool setenv(std::string name, std::string value);

    // ---- local EOF (any thread; for peer programs waiting on stdin EOF) ----
    bool sendEof();

    // Close (any thread, idempotent): an in-flight open finishes first; an
    // open channel best-effort flushes the write queue before the close
    // handshake. The result arrives via onClose.
    void close();

    ChannelState state() const { return state_.load(std::memory_order_acquire); }
    // Backpressure observation: currently queued bytes (any thread).
    size_t pendingWriteBytes() const { return pendingBytes_.load(std::memory_order_acquire); }

    // Step-machine legality table (static pure function, for unit tests).
    static bool isLegalTransition(ChannelState from, ChannelState to);

private:
    friend class SshSession; // event dispatch (pump) and session-loss cleanup

    enum class RequestKind { Shell, Exec };

    // Common admission path of open* (any thread): CAS slot -> stage args -> post.
    bool admitOpen(RequestKind kind, bool withPty, PtySpec pty, std::string command);

    // ---- loop thread only below ----
    void begin();
    bool pump();          // master pump: advance the machine/flush/drain/close
    bool driveOpen();     // opening -> pty -> starting continuation
    bool driveEnv();      // queued setenv pairs (pre-startup first, then best-effort)
    bool driveResize();
    bool driveSendEof();
    bool flushWrites();
    bool drainReads();    // dual-stream drain + EOF observation
    bool driveClose();    // close handshake -> exit info -> free -> finish
    void failOpen(SshChannelError error, const std::string& message);
    void finishClose(ChannelCloseReason reason, const std::string& message);
    void collectExitInfo();
    void onSessionLost(); // forced cleanup when the session drops/closes
    bool wantsOutboundBlocked() const { return blockedOutbound_; }
    void noteBlocked();   // record the EAGAIN direction per libssh2's declaration
    void transitionTo(ChannelState to);
    void dropWriteQueue();
    std::string lastLibssh2Error();

    SshSession& session_;
    SshChannelCallbacks callbacks_;

    // ---- cross-thread visible state ----
    std::atomic<ChannelState> state_{ChannelState::Idle};
    std::atomic<bool> openAdmitted_{false};
    std::atomic<bool> acceptWrites_{false};
    std::atomic<size_t> pendingBytes_{0};
    std::atomic<bool> closeRequested_{false};
    std::atomic<bool> hasPty_{false};

    // ---- open* staging (admitting thread writes before post; the post lock
    // forms the happens-before edge, same convention as SshSession::connect) ----
    RequestKind kind_ = RequestKind::Shell;
    PtySpec pty_;
    std::string command_;

    // ---- loop thread only ----
    struct _LIBSSH2_CHANNEL* channel_ = nullptr;
    bool blockedOutbound_ = false; // last EAGAIN stalled on the outbound direction
    PendingWriteQueue writeQueue_;
    std::deque<std::pair<std::string, std::string>> pendingEnv_; // setenv backlog
    std::optional<std::pair<std::uint32_t, std::uint32_t>> pendingResize_; // coalesced
    bool eofRequested_ = false;
    bool eofSent_ = false;
    bool eofSeen_ = false;          // peer EOF/close observed (incl. read == 0)
    bool localCloseRequested_ = false;
    bool openFailed_ = false;       // cleanup after a failed open: suppress onClose
    bool closeCompleted_ = false;   // libssh2_channel_close returned 0
    int exitStatus_ = 0;
    std::string exitSignal_;
};

// Blocking one-shot exec for tests/diagnostics: opens an exec channel,
// collects both streams, waits for close. Must NOT be called on the session's
// loop thread. ok = channel opened and closed without a transport error;
// exitStatus is valid when ok (missing exit-status reports as 0).
struct ExecResult {
    bool ok = false;
    int exitStatus = 0;
    std::string stdoutData;
    std::string stderrData;
    std::string message; // diagnostics when !ok
};
ExecResult execCommand(SshSession& session, const std::string& command,
                       std::uint32_t timeoutMs = 15000);

const char* toString(ChannelState state);
const char* toString(ChannelCloseReason reason);
const char* toString(SshChannelError error);

} // namespace ssh
} // namespace sshclient
