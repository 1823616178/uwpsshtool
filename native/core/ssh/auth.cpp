#include "auth.h"
#include "debug_log.h"
#include "ssh/agent.h"

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <utility>

#include <libssh2.h>
#include <openssl/crypto.h>

#include "io/SessionThread.h"

#define SSH_LOG(...) sshclient::diagnostics::debugLog("ssh", __VA_ARGS__)

namespace sshclient {
namespace ssh {

// ---------------------------------------------------------------- pure logic

void secureZero(void* data, size_t len)
{
    if (data == nullptr || len == 0) {
        return;
    }
    OPENSSL_cleanse(data, len);
}

void secureZero(std::string& s)
{
    if (!s.empty()) {
        OPENSSL_cleanse(s.data(), s.size()); // data() is writable since C++17
    }
    s.clear();
}

AuthMethodSet parseAuthMethodList(const std::string& csv)
{
    AuthMethodSet set;
    set.raw = csv;
    size_t pos = 0;
    while (pos <= csv.size()) {
        const size_t comma = csv.find(',', pos);
        std::string item =
            csv.substr(pos, comma == std::string::npos ? std::string::npos : comma - pos);
        const size_t begin = item.find_first_not_of(" \t\r\n");
        const size_t end = item.find_last_not_of(" \t\r\n");
        item = begin == std::string::npos ? "" : item.substr(begin, end - begin + 1);
        if (item.empty()) {
            // skip empty items (including an all-empty input)
        } else if (item == "password") {
            set.password = true;
        } else if (item == "publickey") {
            set.publicKey = true;
        } else if (item == "keyboard-interactive") {
            set.keyboardInteractive = true;
        } else {
            set.unsupported.push_back(item);
        }
        if (comma == std::string::npos) {
            break;
        }
        pos = comma + 1;
    }
    return set;
}

SshSessionError mapAuthError(int libssh2Error, AuthMethod method, const std::string& message)
{
    switch (method) {
    case AuthMethod::Password:
        return SshSessionError::AuthFailedPassword;
    case AuthMethod::PublicKey:
        if (libssh2Error == LIBSSH2_ERROR_KEYFILE_AUTH_FAILED) {
            return SshSessionError::AuthFailedPassphrase; // PEM key: wrong passphrase
        }
        if (libssh2Error == LIBSSH2_ERROR_FILE) {
            // OpenSSH-format encrypted key with a wrong passphrase degrades to
            // "Unsupported private key file format" here (auth.h has details).
            return SshSessionError::AuthFailedPassphrase;
        }
        if (libssh2Error == LIBSSH2_ERROR_PUBLICKEY_UNVERIFIED &&
            message.find("Callback returned error") != std::string::npos) {
            // Local decrypt failed at the signing stage with public key data.
            // The same code with "Invalid signature for supplied public key"
            // is a server-side rejection and stays AuthFailedKey.
            return SshSessionError::AuthFailedPassphrase;
        }
        return SshSessionError::AuthFailedKey;
    case AuthMethod::KeyboardInteractive:
        return SshSessionError::AuthFailedInteractive;
    }
    return SshSessionError::InternalError;
}

const char* toString(AuthMethod method)
{
    switch (method) {
    case AuthMethod::Password:            return "password";
    case AuthMethod::PublicKey:           return "publickey";
    case AuthMethod::KeyboardInteractive: return "keyboard-interactive";
    }
    return "unknown";
}

// ---------------------------------------------------------------- AuthPromptGate

void AuthPromptGate::reset()
{
    std::lock_guard<std::mutex> lock(mutex_);
    answered_ = false;
    answers_.clear();
}

bool AuthPromptGate::wait(std::vector<std::string>& answersOut, std::uint32_t timeoutMs)
{
    std::unique_lock<std::mutex> lock(mutex_);
    const bool answered = cv_.wait_for(lock, std::chrono::milliseconds(timeoutMs),
                                       [&] { return answered_; });
    if (answered) {
        answersOut = std::move(answers_);
        answers_.clear();
    }
    return answered;
}

void AuthPromptGate::submit(std::vector<std::string> answers)
{
    {
        std::lock_guard<std::mutex> lock(mutex_);
        answered_ = true;
        answers_ = std::move(answers);
    }
    cv_.notify_all();
}

// ---------------------------------------------------------------- AuthOp

SshSession::AuthOp::~AuthOp()
{
    // Wipe on every conclusion path: credentials must not linger in session
    // memory (publicKey is not sensitive but shares the "nothing remains" rule).
    secureZero(password);
    secureZero(privateKey);
    secureZero(publicKey);
    secureZero(passphrase);
}

// ---------------------------------------------------------------- admission (any thread)

bool SshSession::authenticatePassword(std::string& password, AuthCallback callback)
{
    if (state() != SshSessionState::Authenticating || !callback) {
        SSH_LOG("password auth not admitted: state %s (only authenticating accepts)",
                toString(state()));
        return false;
    }
    bool expected = false;
    if (!authBusy_.compare_exchange_strong(expected, true)) {
        SSH_LOG("password auth not admitted: another auth operation is in flight");
        return false;
    }
    auto op = std::make_unique<AuthOp>();
    op->method = AuthMethod::Password;
    op->password = password;
    op->callback = std::move(callback);
    // Wipe the caller's buffer on admission: the credential now lives inside
    // the session; the internal copy is wiped by ~AuthOp (clearAuthState /
    // releaseResources).
    secureZero(password);
    authOpStaging_ = std::move(op);
    thread_.post([this] { beginAuthOp(); });
    return true;
}

bool SshSession::authenticatePublicKey(std::string& privateKeyData, std::string& publicKeyData,
                                       std::string& passphrase, AuthCallback callback)
{
    if (state() != SshSessionState::Authenticating || !callback || privateKeyData.empty()) {
        SSH_LOG("publickey auth not admitted: state %s", toString(state()));
        return false;
    }
    bool expected = false;
    if (!authBusy_.compare_exchange_strong(expected, true)) {
        SSH_LOG("publickey auth not admitted: another auth operation is in flight");
        return false;
    }
    auto op = std::make_unique<AuthOp>();
    op->method = AuthMethod::PublicKey;
    op->privateKey = privateKeyData;
    op->publicKey = publicKeyData;
    op->passphrase = passphrase;
    op->callback = std::move(callback);
    secureZero(privateKeyData);
    secureZero(passphrase); // publicKeyData is not sensitive: kept as-is
    authOpStaging_ = std::move(op);
    thread_.post([this] { beginAuthOp(); });
    return true;
}

bool SshSession::authenticateAgent(const std::string& keyId, SshAgent& agent,
                                    AuthCallback callback)
{
    // 先从 agent 取材料快照再受理：agent 未解锁/无此 keyId/已超时清除时不动用
    // authBusy_ 受理位、不消耗认证重试计数、不回调，直接返回 false
    // （未取到材料即无敏感副本产生，无残留）。
    std::optional<SshAgent::AgentKeyMaterial> material = agent.getKeyMaterial(keyId);
    if (!material.has_value()) {
        SSH_LOG("agent auth not admitted: keyId is locked or expired");
        return false;
    }
    std::string publicKey; // 留空：由 libssh2 从私钥提取公钥（OpenSSH 格式内嵌）
    const bool admitted = authenticatePublicKey(material->privateKey, publicKey,
                                                material->passphrase, std::move(callback));
    // 受理成功：私钥/短语快照已被 authenticatePublicKey 清零（受理即清零）；
    // 未受理（状态竞态/已有操作进行中）：material 析构兜底清零。
    return admitted;
}

bool SshSession::authenticateKeyboardInteractive(AuthCallback callback)
{
    if (state() != SshSessionState::Authenticating || !callback) {
        SSH_LOG("keyboard-interactive auth not admitted: state %s", toString(state()));
        return false;
    }
    bool expected = false;
    if (!authBusy_.compare_exchange_strong(expected, true)) {
        SSH_LOG("keyboard-interactive auth not admitted: another auth operation is in flight");
        return false;
    }
    auto op = std::make_unique<AuthOp>();
    op->method = AuthMethod::KeyboardInteractive;
    op->callback = std::move(callback);
    // Answer material does not exist yet (prompts arrive inside the libssh2
    // callback); answer strings are wiped in onKbdIntPrompts after use.
    authOpStaging_ = std::move(op);
    thread_.post([this] { beginAuthOp(); });
    return true;
}

void SshSession::submitAuthAnswers(std::vector<std::string> answers)
{
    promptGate_.submit(std::move(answers));
}

bool SshSession::queryAuthMethods(AuthMethodsCallback callback)
{
    if (state() != SshSessionState::Authenticating || !callback) {
        SSH_LOG("auth methods query not admitted: state %s", toString(state()));
        return false;
    }
    bool expected = false;
    if (!authBusy_.compare_exchange_strong(expected, true)) {
        SSH_LOG("auth methods query not admitted: another auth operation is in flight");
        return false;
    }
    thread_.post([this, callback = std::move(callback)]() mutable {
        beginAuthMethodsQuery(std::move(callback));
    });
    return true;
}

// ---------------------------------------------------------------- assembly & driving (loop thread)

void SshSession::beginAuthOp()
{
    std::unique_ptr<AuthOp> op = std::move(authOpStaging_);
    authOpStaging_ = nullptr;
    if (op == nullptr) {
        authBusy_.store(false, std::memory_order_release); // defensive: should not happen
        return;
    }
    if (state() != SshSessionState::Authenticating || authOp_ != nullptr) {
        // State raced away after admission (peer lost / previous op not yet
        // cleared): report failure and release the slot.
        authBusy_.store(false, std::memory_order_release);
        if (op->callback) {
            op->callback(AuthResult{op->method, false, SshSessionError::InternalError,
                                    "session left the authenticating state", 0});
        }
        return; // op's destructor wipes the credentials
    }
    authOp_ = std::move(op);
    authOp_->timer = thread_.loop().runAfter(options_.authTimeoutMs, [this] {
        // A server that does not answer within authTimeoutMs usually means a
        // broken link; retrying the same credentials is pointless.
        if (authOp_ != nullptr && state() == SshSessionState::Authenticating) {
            fail(SshSessionError::AuthTimeout, "auth attempt timed out (authTimeoutMs)");
        }
    });
    driveAuth();
}

void SshSession::driveAuth()
{
    AuthOp* op = authOp_.get();
    if (op == nullptr) {
        return;
    }

    int rc = LIBSSH2_ERROR_INVAL;
    switch (op->method) {
    case AuthMethod::Password:
        rc = ::libssh2_userauth_password_ex(
            session_, username_.c_str(), static_cast<unsigned int>(username_.size()),
            op->password.data(), static_cast<unsigned int>(op->password.size()), nullptr);
        break;
    case AuthMethod::PublicKey:
        // libssh2 re-reads the buffers on every call (no cross-call pointers),
        // so the credentials stay in AuthOp across EAGAIN continuations and are
        // wiped by clearAuthState once a conclusion is reached.
        rc = ::libssh2_userauth_publickey_frommemory(
            session_, username_.c_str(), username_.size(),
            op->publicKey.empty() ? nullptr : op->publicKey.data(), op->publicKey.size(),
            op->privateKey.data(), op->privateKey.size(),
            op->passphrase.empty() ? nullptr : op->passphrase.c_str());
        break;
    case AuthMethod::KeyboardInteractive:
        rc = ::libssh2_userauth_keyboard_interactive_ex(
            session_, username_.c_str(), static_cast<unsigned int>(username_.size()),
            &SshSession::kbdIntResponseCb);
        break;
    }
    if (rc == LIBSSH2_ERROR_EAGAIN) {
        updateSocketInterest(); // re-arm per libssh2's declared block direction
        return;
    }

    // Conclusion reached: lift method and callback out first (clearAuthState
    // releases them together with the credentials).
    const AuthMethod method = op->method;
    AuthCallback callback = std::move(op->callback);

    if (rc == 0) {
        clearAuthState();
        authFailedAttempts_ = 0;
        {
            std::lock_guard<std::mutex> lock(errorMutex_);
            error_ = SshSessionError::None;
            errorMessage_.clear();
        }
        transitionTo(SshSessionState::Established);
        if (callback) {
            callback(AuthResult{method, true, SshSessionError::None, "",
                                options_.authMaxAttempts});
        }
        return;
    }

    char* errmsg = nullptr;
    int errmsgLen = 0;
    ::libssh2_session_last_error(session_, &errmsg, &errmsgLen, 0);
    const std::string message =
        errmsg != nullptr ? std::string(errmsg, errmsgLen) : std::string("unknown error");
    const SshSessionError code = mapAuthError(rc, method, message);

    clearAuthState();
    ++authFailedAttempts_;
    const unsigned left = options_.authMaxAttempts > authFailedAttempts_
                              ? options_.authMaxAttempts - authFailedAttempts_
                              : 0;
    SSH_LOG("auth failed (method %s, code %s): %s (retries left %u)", toString(method),
            toString(code), message.c_str(), left);
    {
        // Non-terminal failures also mirror into lastError() (same convention
        // as HostKeyMismatch) so the upper layer can poll the latest cause.
        std::lock_guard<std::mutex> lock(errorMutex_);
        error_ = code;
        errorMessage_ = message;
    }
    if (left == 0) {
        fail(code, std::string("auth attempts exhausted (") + toString(method) +
                       "): " + message); // -> Error terminal
    }
    if (callback) {
        callback(AuthResult{method, false, code, message, left});
    }
}

// ---------------------------------------------------------------- methods query (loop thread)

void SshSession::beginAuthMethodsQuery(AuthMethodsCallback callback)
{
    if (state() != SshSessionState::Authenticating || authOp_ != nullptr ||
        authMethodsCallback_) {
        // State raced away after admission: report failure, release the slot.
        authBusy_.store(false, std::memory_order_release);
        if (callback) {
            callback(std::nullopt);
        }
        return;
    }
    authMethodsCallback_ = std::move(callback);
    authMethodsTimer_ = thread_.loop().runAfter(options_.authTimeoutMs, [this] {
        if (authMethodsCallback_ && state() == SshSessionState::Authenticating) {
            fail(SshSessionError::AuthTimeout, "auth methods query timed out (authTimeoutMs)");
        }
    });
    driveAuthMethodsQuery();
}

void SshSession::driveAuthMethodsQuery()
{
    if (!authMethodsCallback_) {
        return;
    }
    // userauth_list sends a "none" auth request and parses the method list out
    // of the server's failure reply.
    char* list = ::libssh2_userauth_list(session_, username_.c_str(),
                                         static_cast<unsigned int>(username_.size()));
    if (list == nullptr) {
        if (::libssh2_session_last_errno(session_) == LIBSSH2_ERROR_EAGAIN) {
            updateSocketInterest();
            return;
        }
        if (::libssh2_userauth_authenticated(session_) != 0) {
            // fix/functional-pass: "none" auth succeeded (libssh2 returns NULL
            // and marks the session authenticated). Mirror the success path of
            // driveAuth so callers do not attempt a second authentication.
            authFailedAttempts_ = 0;
            {
                std::lock_guard<std::mutex> lock(errorMutex_);
                error_ = SshSessionError::None;
                errorMessage_.clear();
            }
            authBusy_.store(false, std::memory_order_release);
            transitionTo(SshSessionState::Established);
            AuthMethodSet none;
            none.authenticated = true;
            none.raw = "none";
            finishAuthMethodsQuery(std::move(none));
            return;
        }
        char* errmsg = nullptr;
        int errmsgLen = 0;
        ::libssh2_session_last_error(session_, &errmsg, &errmsgLen, 0);
        SSH_LOG("auth methods query failed: %s",
                errmsg != nullptr ? std::string(errmsg, errmsgLen).c_str() : "unknown error");
        finishAuthMethodsQuery(std::nullopt);
        return;
    }
    // The returned buffer belongs to libssh2 (invalid after the next userauth
    // call): copy and parse immediately.
    finishAuthMethodsQuery(parseAuthMethodList(list));
}

void SshSession::finishAuthMethodsQuery(std::optional<AuthMethodSet> result)
{
    AuthMethodsCallback callback = std::move(authMethodsCallback_);
    authMethodsCallback_ = nullptr;
    if (authMethodsTimer_ != 0) {
        thread_.loop().cancelTimer(authMethodsTimer_);
        authMethodsTimer_ = 0;
    }
    authBusy_.store(false, std::memory_order_release);
    if (callback) {
        callback(std::move(result));
    }
}

// ---------------------------------------------------------------- KI answer bridge

void SshSession::kbdIntResponseCb(const char* name, int nameLen,
                                  const char* instruction, int instructionLen, int numPrompts,
                                  const _LIBSSH2_USERAUTH_KBDINT_PROMPT* prompts,
                                  _LIBSSH2_USERAUTH_KBDINT_RESPONSE* responses, void** abstract)
{
    (void)name;
    (void)nameLen;
    (void)instruction;
    (void)instructionLen;
    // abstract is the `this` passed to libssh2_session_init_ex in beginHandshake.
    auto* self = static_cast<SshSession*>(abstract != nullptr ? *abstract : nullptr);
    if (self == nullptr) {
        return; // defensive: responses are zero-initialized by libssh2
    }
    self->onKbdIntPrompts(numPrompts, prompts, responses);
}

void SshSession::onKbdIntPrompts(int numPrompts,
                                 const _LIBSSH2_USERAUTH_KBDINT_PROMPT* prompts,
                                 _LIBSSH2_USERAUTH_KBDINT_RESPONSE* responses)
{
    // libssh2 frees each responses[i].text with its own allocator after the
    // callback returns (libssh2.h: "Responses data will be freed by libssh2
    // after callback return"). This project never installs a custom allocator,
    // so the default malloc/free pairing holds and malloc is correct here.
    auto fillResponse = [&](int i, const char* data, size_t len) {
        char* buf = static_cast<char*>(std::malloc(len + 1));
        if (buf != nullptr) {
            if (len > 0) {
                std::memcpy(buf, data, len);
            }
            buf[len] = '\0';
        }
        responses[i].text = buf;
        responses[i].length = static_cast<unsigned int>(len);
    };

    AuthOp* op = authOp_.get();
    if (op == nullptr || op->method != AuthMethod::KeyboardInteractive) {
        // Defensive (unreachable on the normal path): empty answers let the
        // server reject cleanly instead of hanging on input.
        for (int i = 0; i < numPrompts; ++i) {
            fillResponse(i, "", 0);
        }
        return;
    }

    std::vector<KbdIntPrompt> promptList;
    promptList.reserve(static_cast<size_t>(numPrompts));
    for (int i = 0; i < numPrompts; ++i) {
        KbdIntPrompt p;
        if (prompts[i].text != nullptr && prompts[i].length > 0) {
            p.text.assign(reinterpret_cast<const char*>(prompts[i].text), prompts[i].length);
        }
        p.echo = prompts[i].echo != 0;
        promptList.push_back(std::move(p));
    }

    // N05 contract: deliver the prompts to the upper layer, then BLOCK the
    // loop thread on the gate until submitAuthAnswers() lands or
    // authPromptTimeoutMs elapses (timeout = cancel -> empty answers -> the
    // server rejects the round).
    std::vector<std::string> answers;
    if (options_.authPromptSink != nullptr) {
        promptGate_.reset();
        try {
            options_.authPromptSink->onAuthPrompts(promptList);
        } catch (...) {
            SSH_LOG("auth prompt sink threw; answering empty");
        }
        promptGate_.wait(answers, options_.authPromptTimeoutMs);
    }
    for (int i = 0; i < numPrompts; ++i) {
        if (static_cast<size_t>(i) < answers.size()) {
            fillResponse(i, answers[i].data(), answers[i].size());
        } else {
            fillResponse(i, "", 0); // short answer list: empty fallback
        }
    }
    for (auto& answer : answers) {
        secureZero(answer); // answers (often password/OTP) wiped after the copy
    }
}

// ---------------------------------------------------------------- cleanup (loop thread)

void SshSession::clearAuthState()
{
    if (authOp_ != nullptr) {
        if (authOp_->timer != 0) {
            thread_.loop().cancelTimer(authOp_->timer);
            authOp_->timer = 0;
        }
        authOp_.reset(); // ~AuthOp wipes password/private key/passphrase copies
    }
    if (authMethodsTimer_ != 0) {
        thread_.loop().cancelTimer(authMethodsTimer_);
        authMethodsTimer_ = 0;
    }
    authMethodsCallback_ = nullptr;
    authBusy_.store(false, std::memory_order_release);
}

} // namespace ssh
} // namespace sshclient
