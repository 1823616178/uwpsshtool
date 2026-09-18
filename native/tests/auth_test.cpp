// N05: authentication — password / public key / keyboard-interactive.
//
// Offline unit tests cover the error mapping (201/202/203/204), the auth
// method list parser, secureZero, the KI prompt gate (timeout = cancel, no
// deadlock) and admission rejection outside Authenticating.
// Integration tests follow the N03/N04 pattern: SSH_TEST_HOST/PORT/USER/
// PASSWORD gate the live runs (GTEST_SKIP otherwise). Public key tests
// additionally need SSH_TEST_FIXTURE_KEYS=1, meaning the target server
// authorizes the committed test-only keys in native/tests/fixtures/keys/
// (passphrase for the *_enc fixtures: "n05-test-passphrase").
#include "io/SessionThread.h" // winsock2.h must precede windows.h/gtest.
#include "ssh/auth.h"
#include "ssh/session.h"

#include <gtest/gtest.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstdlib>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <vector>

#include <libssh2.h>

#include "sshd_testkit.h"

using namespace std::chrono_literals;
using sshclient::io::SessionThread;
using sshclient::ssh::AuthCallback;
using sshclient::ssh::AuthMethod;
using sshclient::ssh::AuthMethodSet;
using sshclient::ssh::AuthPromptGate;
using sshclient::ssh::AuthResult;
using sshclient::ssh::IAuthPromptSink;
using sshclient::ssh::KbdIntPrompt;
using sshclient::ssh::SshSession;
using sshclient::ssh::SshSessionError;
using sshclient::ssh::SshSessionOptions;
using sshclient::ssh::SshSessionState;

namespace {

constexpr const char* kFixturePassphrase = "n05-test-passphrase";

std::string FixturePath(const char* name)
{
    return std::string(SSH_TEST_SOURCE_DIR) + "/fixtures/keys/" + name;
}

bool ReadTextFile(const std::string& path, std::string& out)
{
    FILE* f = std::fopen(path.c_str(), "rb");
    if (f == nullptr) {
        return false;
    }
    char buf[8192];
    out.clear();
    size_t n = 0;
    while ((n = std::fread(buf, 1, sizeof(buf), f)) > 0) {
        out.append(buf, n);
    }
    std::fclose(f);
    return !out.empty();
}

class AuthRecorder {
public:
    void operator()(const AuthResult& result)
    {
        {
            std::lock_guard<std::mutex> lock(mutex_);
            results_.push_back(result);
        }
        changed_.notify_all();
    }

    bool waitForCount(size_t count, std::chrono::milliseconds timeout)
    {
        std::unique_lock<std::mutex> lock(mutex_);
        return changed_.wait_for(lock, timeout, [&] { return results_.size() >= count; });
    }

    std::vector<AuthResult> results() const
    {
        std::lock_guard<std::mutex> lock(mutex_);
        return results_;
    }

private:
    mutable std::mutex mutex_;
    std::condition_variable changed_;
    std::vector<AuthResult> results_;
};

// KI sink that simulates an instantly-answering UI: submitAuthAnswers is
// invoked inline (reset() ran before the sink call, so the gate takes it).
class ImmediateSink final : public IAuthPromptSink {
public:
    std::vector<std::string> answers;
    SshSession* session = nullptr;
    std::atomic<int> rounds{0};
    std::vector<KbdIntPrompt> lastPrompts;

    void onAuthPrompts(std::vector<KbdIntPrompt> prompts) override
    {
        ++rounds;
        lastPrompts = prompts;
        if (session != nullptr) {
            session->submitAuthAnswers(answers);
        }
    }
};

} // namespace

// ================================================================== pure logic

TEST(AuthMethodListTest, ParseVariants)
{
    using sshclient::ssh::parseAuthMethodList;

    const AuthMethodSet all = parseAuthMethodList("publickey,password,keyboard-interactive");
    EXPECT_TRUE(all.publicKey);
    EXPECT_TRUE(all.password);
    EXPECT_TRUE(all.keyboardInteractive);
    EXPECT_TRUE(all.unsupported.empty());
    EXPECT_EQ(all.raw, "publickey,password,keyboard-interactive");

    // Whitespace is trimmed, empty items ignored, unknown methods collected.
    const AuthMethodSet messy = parseAuthMethodList(" publickey , ,hostbased,, password ");
    EXPECT_TRUE(messy.publicKey);
    EXPECT_TRUE(messy.password);
    EXPECT_FALSE(messy.keyboardInteractive);
    ASSERT_EQ(messy.unsupported.size(), 1u);
    EXPECT_EQ(messy.unsupported[0], "hostbased");

    const AuthMethodSet empty = parseAuthMethodList("");
    EXPECT_FALSE(empty.password);
    EXPECT_FALSE(empty.publicKey);
    EXPECT_FALSE(empty.keyboardInteractive);
    EXPECT_TRUE(empty.unsupported.empty());

    const AuthMethodSet single = parseAuthMethodList("password");
    EXPECT_TRUE(single.password);
    EXPECT_FALSE(single.publicKey);
}

// N05 acceptance: auth failure -> 201/202/203, wrong passphrase -> 204.
TEST(AuthErrorMapTest, PasswordAlways201)
{
    using sshclient::ssh::mapAuthError;
    EXPECT_EQ(mapAuthError(LIBSSH2_ERROR_AUTHENTICATION_FAILED, AuthMethod::Password, ""),
              SshSessionError::AuthFailedPassword);
    EXPECT_EQ(mapAuthError(LIBSSH2_ERROR_PASSWORD_EXPIRED, AuthMethod::Password, "expired"),
              SshSessionError::AuthFailedPassword);
}

TEST(AuthErrorMapTest, PublicKeySplitsLocalKeyFailureFromServerRejection)
{
    using sshclient::ssh::mapAuthError;
    // Local private key load/decrypt failures -> 204:
    EXPECT_EQ(mapAuthError(LIBSSH2_ERROR_KEYFILE_AUTH_FAILED, AuthMethod::PublicKey,
                           "Wrong passphrase for private key"),
              SshSessionError::AuthFailedPassphrase);
    EXPECT_EQ(mapAuthError(LIBSSH2_ERROR_FILE, AuthMethod::PublicKey,
                           "Unsupported private key file format"),
              SshSessionError::AuthFailedPassphrase);
    EXPECT_EQ(mapAuthError(LIBSSH2_ERROR_PUBLICKEY_UNVERIFIED, AuthMethod::PublicKey,
                           "Callback returned error"),
              SshSessionError::AuthFailedPassphrase);
    // Server-side rejections -> 202:
    EXPECT_EQ(mapAuthError(LIBSSH2_ERROR_AUTHENTICATION_FAILED, AuthMethod::PublicKey, ""),
              SshSessionError::AuthFailedKey);
    EXPECT_EQ(mapAuthError(LIBSSH2_ERROR_PUBLICKEY_UNVERIFIED, AuthMethod::PublicKey,
                           "Invalid signature for supplied public key"),
              SshSessionError::AuthFailedKey);
}

TEST(AuthErrorMapTest, KeyboardInteractiveAlways203)
{
    using sshclient::ssh::mapAuthError;
    EXPECT_EQ(mapAuthError(LIBSSH2_ERROR_AUTHENTICATION_FAILED,
                           AuthMethod::KeyboardInteractive, ""),
              SshSessionError::AuthFailedInteractive);
}

TEST(SecureZeroTest, WipesStringContent)
{
    std::string secret = "correct horse battery staple";
    const char* storage = secret.data(); // SSO or heap: capacity survives clear()
    const size_t len = secret.size();
    sshclient::ssh::secureZero(secret);
    EXPECT_TRUE(secret.empty());
    for (size_t i = 0; i < len; ++i) {
        EXPECT_EQ(storage[i], '\0') << "byte " << i << " not wiped";
    }
}

// N05 acceptance: an unanswered KI prompt returns cancel after the timeout,
// without deadlocking.
TEST(AuthPromptGateTest, TimeoutWithoutAnswerReturnsFalse)
{
    AuthPromptGate gate;
    gate.reset();
    std::vector<std::string> answers;
    const auto started = std::chrono::steady_clock::now();
    EXPECT_FALSE(gate.wait(answers, 150));
    EXPECT_TRUE(answers.empty());
    EXPECT_GE(std::chrono::steady_clock::now() - started, 140ms);
}

TEST(AuthPromptGateTest, SubmitFromAnotherThreadWakesWaiter)
{
    AuthPromptGate gate;
    gate.reset();
    std::thread producer([&] {
        std::this_thread::sleep_for(50ms);
        gate.submit({"s3cret", "123456"});
    });
    std::vector<std::string> answers;
    EXPECT_TRUE(gate.wait(answers, 5000));
    EXPECT_EQ(answers, (std::vector<std::string>{"s3cret", "123456"}));
    producer.join();
}

TEST(AuthPromptGateTest, ResetDiscardsStaleAnswers)
{
    AuthPromptGate gate;
    gate.submit({"late answer from a previous round"});
    gate.reset(); // a new round must not consume the stale answer
    std::vector<std::string> answers;
    EXPECT_FALSE(gate.wait(answers, 100));
}

TEST(AuthAdmissionTest, RejectedOutsideAuthenticatingKeepsCallerBuffers)
{
    SessionThread thread;
    ASSERT_TRUE(thread.start());
    {
        SshSession session(thread, {}, nullptr);
        std::string password = "keep-me";
        std::string privateKey = "keep-me-too";
        std::string publicKey = "pub";
        std::string passphrase = "phrase";
        // Idle: nothing is admitted and (per contract) buffers stay intact.
        EXPECT_FALSE(session.authenticatePassword(password, [](const AuthResult&) {}));
        EXPECT_EQ(password, "keep-me");
        EXPECT_FALSE(session.authenticatePublicKey(privateKey, publicKey, passphrase,
                                                   [](const AuthResult&) {}));
        EXPECT_EQ(privateKey, "keep-me-too");
        EXPECT_EQ(passphrase, "phrase");
        EXPECT_FALSE(session.authenticateKeyboardInteractive([](const AuthResult&) {}));
        EXPECT_FALSE(session.queryAuthMethods([](std::optional<AuthMethodSet>) {}));
        thread.stop();
    }
}

// ==================================================== integration (live server)

TEST(AuthIntegrationTest, QueryAuthMethodsReportsServerList)
{
    SshIntegrationEnvironment environment;
    if (!LoadSshIntegrationEnvironment(environment)) {
        GTEST_SKIP() << "set SSH_TEST_HOST/PORT/USER/PASSWORD to enable live SSH auth";
    }

    SessionThread thread;
    ASSERT_TRUE(thread.start());
    StateRecorder recorder;
    std::mutex mutex;
    std::condition_variable cv;
    std::optional<AuthMethodSet> methods;
    {
        SshSession session(thread, {}, std::ref(recorder));
        ASSERT_TRUE(session.connect(environment.host, environment.port, environment.user));
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Authenticating, 20s));
        ASSERT_TRUE(session.queryAuthMethods([&](std::optional<AuthMethodSet> result) {
            {
                std::lock_guard<std::mutex> lock(mutex);
                methods = std::move(result);
            }
            cv.notify_all();
        }));
        {
            std::unique_lock<std::mutex> lock(mutex);
            ASSERT_TRUE(cv.wait_for(lock, 10s, [&] { return methods.has_value(); }));
        }
        session.close();
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Closed, 5s));
        thread.stop();
    }

    ASSERT_TRUE(methods.has_value());
    EXPECT_TRUE(methods->password || methods->publicKey || methods->keyboardInteractive)
        << "server offered no known method; raw=" << methods->raw;
    EXPECT_FALSE(methods->raw.empty());
    std::fprintf(stderr, "[test] server auth methods: %s\n", methods->raw.c_str());
}

TEST(AuthIntegrationTest, PasswordSuccessReachesEstablished)
{
    SshIntegrationEnvironment environment;
    if (!LoadSshIntegrationEnvironment(environment)) {
        GTEST_SKIP() << "set SSH_TEST_HOST/PORT/USER/PASSWORD to enable live SSH auth";
    }

    SessionThread thread;
    ASSERT_TRUE(thread.start());
    StateRecorder recorder;
    AuthRecorder auth;
    {
        SshSession session(thread, {}, std::ref(recorder));
        ASSERT_TRUE(session.connect(environment.host, environment.port, environment.user));
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Authenticating, 20s));

        std::string password = environment.password;
        ASSERT_TRUE(session.authenticatePassword(password, std::ref(auth)));
        // Admission wiped the caller's buffer synchronously.
        EXPECT_TRUE(password.empty());
        ASSERT_TRUE(auth.waitForCount(1, 15s));
        session.close();
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Closed, 5s));
        thread.stop();
    }

    const std::vector<AuthResult> results = auth.results();
    ASSERT_EQ(results.size(), 1u);
    EXPECT_TRUE(results[0].success) << results[0].message;
    EXPECT_EQ(results[0].method, AuthMethod::Password);
    EXPECT_EQ(results[0].error, SshSessionError::None);
    EXPECT_TRUE(Visited(recorder, SshSessionState::Established));
}

TEST(AuthIntegrationTest, WrongPasswordMaps201AndSessionStaysAuthenticating)
{
    SshIntegrationEnvironment environment;
    if (!LoadSshIntegrationEnvironment(environment)) {
        GTEST_SKIP() << "set SSH_TEST_HOST/PORT/USER/PASSWORD to enable live SSH auth";
    }

    SessionThread thread;
    ASSERT_TRUE(thread.start());
    StateRecorder recorder;
    AuthRecorder auth;
    {
        SshSession session(thread, {}, std::ref(recorder));
        ASSERT_TRUE(session.connect(environment.host, environment.port, environment.user));
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Authenticating, 20s));

        std::string wrong = "definitely-not-the-password";
        ASSERT_TRUE(session.authenticatePassword(wrong, std::ref(auth)));
        ASSERT_TRUE(auth.waitForCount(1, 15s));

        std::vector<AuthResult> results = auth.results();
        ASSERT_EQ(results.size(), 1u);
        EXPECT_FALSE(results[0].success);
        EXPECT_EQ(results[0].error, SshSessionError::AuthFailedPassword)
            << "code=" << static_cast<int>(results[0].error) << " msg=" << results[0].message;
        EXPECT_EQ(results[0].attemptsLeft, 2u); // authMaxAttempts defaults to 3
        EXPECT_EQ(session.state(), SshSessionState::Authenticating); // retryable
        EXPECT_EQ(session.lastError(), SshSessionError::AuthFailedPassword);

        // Retry with the real password succeeds on the same session.
        std::string password = environment.password;
        ASSERT_TRUE(session.authenticatePassword(password, std::ref(auth)));
        ASSERT_TRUE(auth.waitForCount(2, 15s));
        session.close();
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Closed, 5s));
        thread.stop();
    }

    const std::vector<AuthResult> results = auth.results();
    ASSERT_EQ(results.size(), 2u);
    EXPECT_TRUE(results[1].success) << results[1].message;
    EXPECT_TRUE(Visited(recorder, SshSessionState::Established));
}

TEST(AuthIntegrationTest, PublicKeyUnencryptedFixture)
{
    SshIntegrationEnvironment environment;
    const char* fixtureGate = std::getenv("SSH_TEST_FIXTURE_KEYS");
    if (!LoadSshIntegrationEnvironment(environment) || fixtureGate == nullptr ||
        fixtureGate[0] == '\0') {
        GTEST_SKIP() << "set SSH_TEST_* plus SSH_TEST_FIXTURE_KEYS=1 (server must "
                        "authorize native/tests/fixtures/keys/*.pub)";
    }

    std::string privateKey;
    std::string publicKey;
    ASSERT_TRUE(ReadTextFile(FixturePath("ed25519_openssh"), privateKey));
    ASSERT_TRUE(ReadTextFile(FixturePath("ed25519_openssh.pub"), publicKey));

    SessionThread thread;
    ASSERT_TRUE(thread.start());
    StateRecorder recorder;
    AuthRecorder auth;
    {
        SshSession session(thread, {}, std::ref(recorder));
        ASSERT_TRUE(session.connect(environment.host, environment.port, environment.user));
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Authenticating, 20s));

        std::string passphrase; // unencrypted key
        ASSERT_TRUE(session.authenticatePublicKey(privateKey, publicKey, passphrase,
                                                  std::ref(auth)));
        EXPECT_TRUE(privateKey.empty()); // caller buffers wiped on admission
        EXPECT_FALSE(publicKey.empty()); // public key is not sensitive: kept
        ASSERT_TRUE(auth.waitForCount(1, 15s));
        session.close();
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Closed, 5s));
        thread.stop();
    }

    const std::vector<AuthResult> results = auth.results();
    ASSERT_EQ(results.size(), 1u);
    EXPECT_TRUE(results[0].success) << results[0].message;
    EXPECT_EQ(results[0].method, AuthMethod::PublicKey);
    EXPECT_TRUE(Visited(recorder, SshSessionState::Established));
}

TEST(AuthIntegrationTest, PublicKeyEncryptedWrongThenRightPassphrase)
{
    SshIntegrationEnvironment environment;
    const char* fixtureGate = std::getenv("SSH_TEST_FIXTURE_KEYS");
    if (!LoadSshIntegrationEnvironment(environment) || fixtureGate == nullptr ||
        fixtureGate[0] == '\0') {
        GTEST_SKIP() << "set SSH_TEST_* plus SSH_TEST_FIXTURE_KEYS=1 (server must "
                        "authorize native/tests/fixtures/keys/*.pub)";
    }

    std::string privateKey;
    std::string publicKey;
    ASSERT_TRUE(ReadTextFile(FixturePath("ed25519_openssh_enc"), privateKey));
    ASSERT_TRUE(ReadTextFile(FixturePath("ed25519_openssh_enc.pub"), publicKey));

    SessionThread thread;
    ASSERT_TRUE(thread.start());
    StateRecorder recorder;
    AuthRecorder auth;
    {
        SshSession session(thread, {}, std::ref(recorder));
        ASSERT_TRUE(session.connect(environment.host, environment.port, environment.user));
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Authenticating, 20s));

        // Wrong passphrase -> 204 (local decrypt failure), retryable.
        std::string key1 = privateKey;
        std::string wrongPhrase = "wrong-passphrase";
        ASSERT_TRUE(session.authenticatePublicKey(key1, publicKey, wrongPhrase, std::ref(auth)));
        ASSERT_TRUE(auth.waitForCount(1, 15s));
        {
            const std::vector<AuthResult> results = auth.results();
            ASSERT_EQ(results.size(), 1u);
            EXPECT_FALSE(results[0].success);
            EXPECT_EQ(results[0].error, SshSessionError::AuthFailedPassphrase)
                << "code=" << static_cast<int>(results[0].error) << " msg=" << results[0].message;
            EXPECT_EQ(session.state(), SshSessionState::Authenticating);
        }

        // Right passphrase -> Established.
        std::string key2 = privateKey;
        std::string phrase = kFixturePassphrase;
        ASSERT_TRUE(session.authenticatePublicKey(key2, publicKey, phrase, std::ref(auth)));
        ASSERT_TRUE(auth.waitForCount(2, 15s));
        session.close();
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Closed, 5s));
        thread.stop();
    }

    const std::vector<AuthResult> results = auth.results();
    ASSERT_EQ(results.size(), 2u);
    EXPECT_TRUE(results[1].success) << results[1].message;
    EXPECT_TRUE(Visited(recorder, SshSessionState::Established));
}

TEST(AuthIntegrationTest, KeyboardInteractiveRoundTrip)
{
    SshIntegrationEnvironment environment;
    const char* kiGate = std::getenv("SSH_TEST_KI");
    if (!LoadSshIntegrationEnvironment(environment) || kiGate == nullptr ||
        kiGate[0] == '\0') {
        GTEST_SKIP() << "set SSH_TEST_* plus SSH_TEST_KI=1 on a server offering "
                        "keyboard-interactive";
    }

    ImmediateSink sink;
    sink.answers = {environment.password};
    SshSessionOptions options;
    options.authPromptSink = &sink;
    options.authPromptTimeoutMs = 10000;

    SessionThread thread;
    ASSERT_TRUE(thread.start());
    StateRecorder recorder;
    AuthRecorder auth;
    {
        SshSession session(thread, options, std::ref(recorder));
        sink.session = &session;
        ASSERT_TRUE(session.connect(environment.host, environment.port, environment.user));
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Authenticating, 20s));
        ASSERT_TRUE(session.authenticateKeyboardInteractive(std::ref(auth)));
        ASSERT_TRUE(auth.waitForCount(1, 20s));
        session.close();
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Closed, 5s));
        sink.session = nullptr;
        thread.stop();
    }

    EXPECT_GE(sink.rounds.load(), 1);
    const std::vector<AuthResult> results = auth.results();
    ASSERT_EQ(results.size(), 1u);
    EXPECT_TRUE(results[0].success) << results[0].message;
    EXPECT_TRUE(Visited(recorder, SshSessionState::Established));
}
