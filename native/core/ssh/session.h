#pragma once

#include <winsock2.h>

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

#include "hostkey.h"
#include "io/EventLoop.h"
#include "keepalive.h"

struct _LIBSSH2_SESSION;
// Forward declarations matching libssh2 1.11.x typedef tags (re-check on pin bump).
struct _LIBSSH2_USERAUTH_KBDINT_PROMPT;
struct _LIBSSH2_USERAUTH_KBDINT_RESPONSE;

namespace sshclient {
namespace io {
class SessionThread;
}
namespace ssh {

class SshChannel; // N06: channel.h

enum class SshSessionState {
    Idle,
    Connecting,
    Handshaking,
    Authenticating,
    Established,
    Closing,
    Closed,
    Disconnected,
    Error,
};

// Values match the shared error contract (01-DESIGN.md section 6.3 /
// SshErrorCode.cs). N08 supplies the complete table and libssh2 mapping.
enum class SshSessionError : int {
    None = 0,
    DnsResolutionFailed = 101,
    ConnectTimeout = 102,
    ConnectionRefused = 103,
    NetworkUnreachable = 104,
    // N05 auth failures: reported via AuthCallback and mirrored to lastError();
    // the session stays in Authenticating for retries until authMaxAttempts
    // is exhausted (then Error terminal) or a single attempt times out (205).
    AuthFailedPassword = 201,    // password rejected (wrong password/user/expired)
    AuthFailedKey = 202,         // public key rejected by the server
    AuthFailedInteractive = 203, // keyboard-interactive answers rejected
    AuthFailedPassphrase = 204,  // private key local load/decrypt failed
    AuthTimeout = 205,           // single auth attempt exceeded authTimeoutMs
    NoLocalCredential = 206,     // no local credential available (reserved for Core)
    AlgorithmNegotiationFailed = 301,
    HostKeyMismatch = 303,
    HandshakeFailed = 304,
    HandshakeTimeout = 305,
    RemoteClosed = 401,
    SocketError = 403,
    // N07: keepalive silent blackhole — keepaliveMaxMisses consecutive periods
    // with no inbound activity, or the keepalive send itself failed (terminal
    // Disconnected).
    KeepaliveTimeout = 404,
    InternalError = 500,
};

// N07 keepalive defaults (01-DESIGN section 7.1: 30 s default); the
// SshSessionOptions defaults below use these.
inline constexpr std::uint32_t kDefaultKeepaliveIntervalSec = 30;
inline constexpr unsigned kDefaultKeepaliveMaxMisses = 3;
// N07 active-probe verdict window default (network-change 5 s reconnect goal).
inline constexpr std::uint32_t kDefaultKeepaliveProbeTimeoutSec = 5;
// libssh2's minimum keepalive interval (1.11.1 keepalive.c promotes any
// interval < 2 to 2); doProbeNow borrows it to force a send past the
// time gate — semantics in that function's comment.
inline constexpr std::uint32_t kLibssh2MinKeepaliveIntervalSec = 2;

// ---------------------------------------------------------------- N05 auth types

enum class AuthMethod {
    Password,
    PublicKey,
    KeyboardInteractive,
};

// One server prompt of a keyboard-interactive round (RFC 4256): text is the
// prompt as sent (e.g. "Password: "), echo tells whether input may be shown.
struct KbdIntPrompt {
    std::string text;
    bool echo = false;
};

// Implemented by the upper layer (UI). Called on the event loop thread when a
// keyboard-interactive round arrives; it must only *dispatch* the prompts
// (e.g. queue a dialog) and return immediately — the session then blocks on
// its own gate until submitAuthAnswers() lands or authPromptTimeoutMs elapses.
class IAuthPromptSink {
public:
    virtual ~IAuthPromptSink() = default;
    virtual void onAuthPrompts(std::vector<KbdIntPrompt> prompts) = 0;
};

// Result of a single auth attempt; AuthCallback fires on the loop thread,
// exactly once per admitted attempt.
struct AuthResult {
    AuthMethod method;
    bool success = false;
    SshSessionError error = SshSessionError::None; // fine-grained code; None on success
    std::string message;       // libssh2's raw error text (diagnostics)
    unsigned attemptsLeft = 0; // retries left after a failure; 0 = session goes to Error
};
using AuthCallback = std::function<void(const AuthResult& result)>;

// Auth methods the server claims to support (queryAuthMethods report).
struct AuthMethodSet {
    bool password = false;
    bool publicKey = false;
    bool keyboardInteractive = false;
    std::vector<std::string> unsupported; // declared but unrecognized (hostbased etc.)
    std::string raw;                      // raw comma-separated list (diagnostics)
};
// nullopt = the probe itself failed (see lastErrorMessage / logs).
using AuthMethodsCallback = std::function<void(std::optional<AuthMethodSet> methods)>;

// keyboard-interactive answer gate: the loop thread blocks in wait() inside
// the libssh2 callback while the UI thread delivers via submit(). Timeout or
// cancel returns false and the round is answered empty (server rejects).
class AuthPromptGate {
public:
    void reset(); // loop thread, before notifying the sink about a new round
    bool wait(std::vector<std::string>& answersOut, std::uint32_t timeoutMs);
    void submit(std::vector<std::string> answers); // any thread

private:
    std::mutex mutex_;
    std::condition_variable cv_;
    bool answered_ = false;
    std::vector<std::string> answers_;
};

struct SshSessionOptions {
    std::uint32_t connectTimeoutMs = 15000;
    std::uint32_t handshakeTimeoutMs = 15000;
    std::uint32_t closeFlushTimeoutMs = 2000;
    std::uint32_t authTimeoutMs = 15000;    // single auth attempt timeout (terminal Error)
    std::uint32_t authMaxAttempts = 3;      // auth failures before Error terminal
    std::uint32_t authPromptTimeoutMs = 120000; // KI prompt wait; timeout = cancel
    // ---- N07 keepalive (takes effect while Established; DESIGN default 30 s) ----
    std::uint32_t keepaliveIntervalSec = kDefaultKeepaliveIntervalSec; // send period; 0 = off
    std::uint32_t keepaliveMaxMisses = kDefaultKeepaliveMaxMisses; // consecutive silent
                                                                   // periods -> blackhole (404)
    // Invoked on the loop thread after the handshake, before Authenticating.
    // Empty = accept (TOFU first-connect semantics; the Core layer supplies
    // the real known_hosts comparison). Must not block.
    HostKeyCallback hostKeyCallback;
    // KI prompt sink (owned by the caller, must outlive the session). Null =
    // KI rounds are answered empty (server rejects).
    IAuthPromptSink* authPromptSink = nullptr;
};

class SshSession final {
public:
    using StateCallback = std::function<void(SshSessionState from, SshSessionState to)>;

    SshSession(io::SessionThread& thread, SshSessionOptions options, StateCallback callback);
    ~SshSession();

    SshSession(const SshSession&) = delete;
    SshSession& operator=(const SshSession&) = delete;

    // Only Idle accepts connect. Parameters are copied before work is posted
    // to the session thread.
    bool connect(std::string host, std::uint16_t port, std::string username);
    void close();

    SshSessionState state() const { return state_.load(); }
    SshSessionError lastError() const;
    std::string lastErrorMessage() const;

    // Host key presented by the peer; set once the handshake completes and
    // kept after a rejection so the UI can show actual vs expected.
    std::optional<HostKeyInfo> hostKeyInfo() const;

    // ---- N05 auth (any thread; only Authenticating accepts, else false) ----
    // Admission semantics: credentials are copied into session state and the
    // caller's buffers are secureZero'd immediately (except publicKeyData —
    // public keys are not sensitive). On false (not admitted) the buffers stay
    // untouched. The internal copies are wiped when the attempt concludes.
    // The callback fires exactly once on the loop thread: success moves the
    // session to Established; failure keeps Authenticating for a retry until
    // authMaxAttempts is exhausted or authTimeoutMs hits (then Error).
    // At most one auth-class operation (attempts and queryAuthMethods) runs at
    // a time; concurrent admission returns false.
    bool authenticatePassword(std::string& password, AuthCallback callback);
    // privateKeyData/passphrase are wiped on admission. Empty passphrase = the
    // key is not encrypted. Empty publicKeyData = libssh2 derives the public
    // key from the private key; passing the .pub text is recommended (fewer
    // decrypt passes, and passphrase errors still normalize to 204 either way).
    bool authenticatePublicKey(std::string& privateKeyData, std::string& publicKeyData,
                               std::string& passphrase, AuthCallback callback);
    // Prompts go to options.authPromptSink; answers come back through
    // submitAuthAnswers(); the prompt wait is bounded by authPromptTimeoutMs.
    bool authenticateKeyboardInteractive(AuthCallback callback);
    // Any thread: deliver the UI's answers to a pending KI round.
    void submitAuthAnswers(std::vector<std::string> answers);
    // Probe the server's supported auth methods (non-blocking drive; mutually
    // exclusive with an auth attempt).
    bool queryAuthMethods(AuthMethodsCallback callback);

    // ---- N07 keepalive configuration and observation ----
    // Set keepalive parameters (any thread; only Idle admits — call before
    // connect; false = not admitted). Takes effect when entering Established
    // (libssh2_keepalive_config + periodic timer); semantics in the options
    // comments and the keepalive.h header note.
    bool setKeepaliveConfig(std::uint32_t intervalSec, std::uint32_t maxMisses);

    // Called by the upper layer after a network change: fire one keepalive
    // immediately and open a timeoutSec verdict window — no inbound activity
    // inside the window goes to Disconnected with KeepaliveTimeout (404), into
    // the existing reconnect chain. timeoutSec == 0 uses
    // kDefaultKeepaliveProbeTimeoutSec. Only Established admits (else false).
    bool probeNow(std::uint32_t timeoutSec);

    // Observation hooks (any thread; for integration tests and diagnostics):
    // keepalive counters since entering Established.
    std::uint32_t keepaliveSendCount() const
    {
        return keepaliveSendCount_.load(std::memory_order_acquire);
    }
    std::uint32_t keepaliveMissCount() const
    {
        return keepaliveMissCount_.load(std::memory_order_acquire);
    }
    std::uint32_t keepaliveProbeCount() const
    {
        return keepaliveProbeCount_.load(std::memory_order_acquire);
    }

    static bool isLegalTransition(SshSessionState from, SshSessionState to);

private:
    friend class SshChannel; // N06: registration, pump dispatch, handle access

    void doConnect();
    void onSocketEvent(SOCKET socket, short events);
    void beginHandshake();
    void driveHandshake();
    void verifyHostKey();
    void rejectHostKey(std::string message);
    void updateSocketInterest();
    void doClose();
    void driveClose();
    void completeClose();

    bool transitionTo(SshSessionState to);
    void fail(SshSessionError error, std::string message);
    void peerDisconnected(SshSessionError error, std::string message);
    void releaseResources();
    void cancelTimers();

    // ---- N06 channel registry (loop thread only; channel.cpp) ----
    void registerChannel(SshChannel* channel);
    void unregisterChannel(SshChannel* channel);
    void driveChannels();             // socket event -> pump every channel
    void notifyChannelsSessionLost(); // releaseResources: force-clean all

    // ---- N07 keepalive (loop thread only) ----
    void armKeepalive();    // on entering Established: libssh2_keepalive_config + first tick
    void onKeepaliveTick(); // each tick: observe inbound -> send -> re-arm per seconds_to_next
    void doProbeNow(std::uint32_t timeoutSec);   // active probe: force one send + short window
    void onProbeDeadline(std::uint32_t timeoutSec); // window verdict, resume the periodic chain

    // ---- N05 auth driver (loop thread; implemented in auth.cpp) ----
    // In-flight auth attempt: method, credential copies (admission copies and
    // wipes the caller's buffers), timeout timer. The destructor (auth.cpp)
    // secureZero's the in-memory credentials on every conclusion path.
    struct AuthOp {
        AuthMethod method = AuthMethod::Password;
        std::string password;
        std::string privateKey;
        std::string publicKey;
        std::string passphrase;
        AuthCallback callback;
        io::EventLoop::TimerId timer = 0;
        ~AuthOp();
    };
    void beginAuthOp();             // admission assembly: take the staging slot, arm timeout, first drive
    void driveAuth();               // drive the current attempt (EAGAIN continues, one step per call)
    void beginAuthMethodsQuery(AuthMethodsCallback callback);
    void driveAuthMethodsQuery();
    void finishAuthMethodsQuery(std::optional<AuthMethodSet> result);
    void clearAuthState();          // disarm timers, wipe credentials, release the admission slot
    bool hasAuthPending() const { return authOp_ != nullptr || authMethodsCallback_ != nullptr; }
    // libssh2 C callback bridge: static to satisfy the C signature, recovers the
    // instance through the session abstract pointer (set at beginHandshake).
    static void kbdIntResponseCb(const char* name, int nameLen,
                                 const char* instruction, int instructionLen, int numPrompts,
                                 const _LIBSSH2_USERAUTH_KBDINT_PROMPT* prompts,
                                 _LIBSSH2_USERAUTH_KBDINT_RESPONSE* responses, void** abstract);
    void onKbdIntPrompts(int numPrompts, const _LIBSSH2_USERAUTH_KBDINT_PROMPT* prompts,
                         _LIBSSH2_USERAUTH_KBDINT_RESPONSE* responses);

    io::SessionThread& thread_;
    SshSessionOptions options_;
    StateCallback stateCallback_;

    std::atomic<SshSessionState> state_{SshSessionState::Idle};
    std::atomic<bool> connectAdmitted_{false};
    std::atomic<bool> closeAdmitted_{false};

    std::string host_;
    std::uint16_t port_ = 0;
    std::string username_;

    SOCKET socket_ = INVALID_SOCKET;
    bool socketRegistered_ = false;
    _LIBSSH2_SESSION* session_ = nullptr;

    io::EventLoop::TimerId connectTimer_ = 0;
    io::EventLoop::TimerId handshakeTimer_ = 0;
    io::EventLoop::TimerId closeTimer_ = 0;

    mutable std::mutex errorMutex_;
    SshSessionError error_ = SshSessionError::None;
    std::string errorMessage_;

    mutable std::mutex hostKeyMutex_;
    std::optional<HostKeyInfo> hostKeyInfo_;
    bool hostKeyRejected_ = false;

    // ---- N05 auth state ----
    // authOp_ / authMethodsCallback_ are loop-thread only; authBusy_ is the
    // admission slot (any-thread CAS, released on the loop thread), same
    // semantics as connectAdmitted_; authOpStaging_ is written by the
    // admitting thread before post() and taken over on the loop thread.
    std::atomic<bool> authBusy_{false};
    std::unique_ptr<AuthOp> authOpStaging_;
    std::unique_ptr<AuthOp> authOp_;
    AuthMethodsCallback authMethodsCallback_;
    io::EventLoop::TimerId authMethodsTimer_ = 0;
    unsigned authFailedAttempts_ = 0;
    AuthPromptGate promptGate_;

    // ---- N06 channel registry (loop thread only) ----
    std::vector<SshChannel*> channels_;

    // ---- N07 keepalive state (loop thread only, except the atomic counters) ----
    io::EventLoop::TimerId keepaliveTimer_ = 0; // next tick / probe deadline (runAfter)
    KeepaliveMissTracker keepaliveMissTracker_{kDefaultKeepaliveMaxMisses}; // rebuilt on config
    bool keepaliveInboundSeen_ = false; // a Readable event was seen this period
    long keepalivePendingBaseline_ = 0; // last tick's FIONREAD pending-byte baseline
    KeepaliveProbe keepaliveProbe_;     // active-probe window (loop thread only)
    // Wall time of the last keepalive actually sent (steady_clock ms; 0 = none
    // yet). doProbeNow uses it to decide whether libssh2's time gate would
    // skip the forced send (see its comment).
    std::uint64_t keepaliveLastSentMs_ = 0;
    // Observation hooks: written on the loop thread, read from any thread.
    std::atomic<std::uint32_t> keepaliveSendCount_{0};
    std::atomic<std::uint32_t> keepaliveMissCount_{0};
    std::atomic<std::uint32_t> keepaliveProbeCount_{0};
};

const char* toString(SshSessionState state);
const char* toString(SshSessionError error);

// N07: may the upper layer auto-reconnect after this terminal state?
//   - Disconnected: always (peer close / socket error / keepalive blackhole
//     may all be transient network trouble);
//   - Error: link-class codes yes (DNS/refused/unreachable/timeout/handshake/
//     socket/auth-timeout), credential-, negotiation- and local-resource-class
//     codes no (reconnecting does not help);
//   - Closed and non-terminal states: no.
bool isAutoReconnectable(SshSessionState terminalState, SshSessionError error);

} // namespace ssh
} // namespace sshclient
