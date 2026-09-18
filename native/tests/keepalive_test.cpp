// N07 keepalive session-side tests — admission semantics offline, behaviour
// against a live sshd behind the usual SSH_TEST_* gates.
//
// Covers:
//   - setKeepaliveConfig admission: only Idle admits (before connect); once
//     past it (Error terminal here) it is rejected — needs no sshd;
//   - keepalive running without false positives: Established with a 1 s
//     interval (libssh2 internally promotes it to its 2 s minimum, see
//     session.cpp armKeepalive), ~2 ticks: the send count grows, the miss
//     count stays 0, the session stays Established — a real sshd answers the
//     global request with REQUEST_SUCCESS/FAILURE and the inbound observation
//     (no channels here -> the FIONREAD growth signal) must keep judging alive;
//   - keepaliveIntervalSec=0 disables it completely: nothing is sent while
//     Established;
//   - probeNow admission: only Established admits; a probe on a live session
//     resolves alive (no disconnect, miss counter stays 0).
//
// The positive blackhole trigger (silent loss) cannot be simulated on a
// loopback host; the verdict logic is covered by pure-logic unit tests
// (tests/reconnect_policy_test.cpp). Fast RST/FIN teardown paths are covered
// by the N03 cases.
#include "io/SessionThread.h" // winsock2.h must precede windows.h/gtest.
#include "ssh/session.h"

#include <gtest/gtest.h>

#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstdlib>
#include <mutex>
#include <optional>
#include <string>
#include <thread>

#include "sshd_testkit.h"

using namespace std::chrono_literals;
using sshclient::io::SessionThread;
using sshclient::io::WinsockInit;
using sshclient::ssh::AuthResult;
using sshclient::ssh::SshSession;
using sshclient::ssh::SshSessionOptions;
using sshclient::ssh::SshSessionState;

namespace {

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

// A port that was free a moment ago (nothing listens after the close).
std::uint16_t PickFreePort()
{
    WinsockInit winsock;
    SOCKET s = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET) {
        return 0;
    }
    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = ::htonl(INADDR_LOOPBACK);
    addr.sin_port = 0;
    std::uint16_t port = 0;
    if (::bind(s, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == 0) {
        int len = sizeof(addr);
        if (::getsockname(s, reinterpret_cast<sockaddr*>(&addr), &len) == 0) {
            port = ::ntohs(addr.sin_port);
        }
    }
    ::closesocket(s);
    return port;
}

// Live-session helper (fixture-key auth, like channel_test): configure()
// runs between construction and connect so tests can pre-set keepalive
// parameters (they are only admitted in Idle).
class LiveSession {
public:
    ~LiveSession() { shutdown(); }

    bool establish(const std::function<void(SshSession&)>& configure)
    {
        if (!LoadSshIntegrationEnvironment(env_)) {
            return false;
        }
        const char* fixtureGate = std::getenv("SSH_TEST_FIXTURE_KEYS");
        if (fixtureGate == nullptr || fixtureGate[0] == '\0') {
            return false;
        }
        std::string privateKey;
        std::string publicKey;
        std::string passphrase; // unencrypted fixture
        if (!ReadTextFile(FixturePath("ed25519_openssh"), privateKey) ||
            !ReadTextFile(FixturePath("ed25519_openssh.pub"), publicKey)) {
            return false;
        }
        if (!thread_.start()) {
            return false;
        }
        session_ = std::make_unique<SshSession>(thread_, options_, std::ref(recorder_));
        if (configure) {
            configure(*session_);
        }
        if (!session_->connect(env_.host, env_.port, env_.user) ||
            !recorder_.waitFor(SshSessionState::Authenticating, 20s)) {
            return false;
        }
        std::mutex mutex;
        std::condition_variable cv;
        std::optional<bool> authOk;
        if (!session_->authenticatePublicKey(privateKey, publicKey, passphrase,
                                             [&](const AuthResult& result) {
                                                 {
                                                     std::lock_guard<std::mutex> lock(mutex);
                                                     authOk = result.success;
                                                 }
                                                 cv.notify_all();
                                             })) {
            return false;
        }
        {
            std::unique_lock<std::mutex> lock(mutex);
            if (!cv.wait_for(lock, 15s, [&] { return authOk.has_value(); })) {
                return false;
            }
        }
        return authOk.value() && recorder_.waitFor(SshSessionState::Established, 5s);
    }

    void shutdown()
    {
        if (session_) {
            session_->close();
            recorder_.waitFor(SshSessionState::Closed, 5s);
            session_.reset();
        }
        thread_.stop();
    }

    SshSessionOptions options_;
    SshSession& session() { return *session_; }
    StateRecorder& recorder() { return recorder_; }

private:
    SshIntegrationEnvironment env_;
    SessionThread thread_;
    StateRecorder recorder_;
    std::unique_ptr<SshSession> session_;
};

} // namespace

// ================================================================== admission (no sshd needed)

TEST(KeepaliveConfigTest, SetKeepaliveConfigOnlyInIdle)
{
    SessionThread thread;
    ASSERT_TRUE(thread.start());
    StateRecorder recorder;
    {
        SshSession session(thread, {}, std::ref(recorder));
        // Idle admits.
        EXPECT_TRUE(session.setKeepaliveConfig(5, 2));

        // Connect to a just-vacated port: Connecting -> Error (refused).
        const std::uint16_t port = PickFreePort();
        ASSERT_NE(port, 0);
        ASSERT_TRUE(session.connect("127.0.0.1", port, "tester"));
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Error, 10s));

        // A terminal state no longer admits.
        EXPECT_FALSE(session.setKeepaliveConfig(10, 5));
        thread.stop();
    }
}

TEST(KeepaliveConfigTest, ProbeNowOnlyInEstablished)
{
    SessionThread thread;
    ASSERT_TRUE(thread.start());
    {
        SshSession session(thread, {}, nullptr); // Idle: nothing to probe
        EXPECT_FALSE(session.probeNow(0));
        EXPECT_FALSE(session.probeNow(5));
        thread.stop();
    }
}

// ==================================================== integration (live server)

TEST(KeepaliveIntegrationTest, KeepaliveRunsWithoutFalsePositive)
{
    LiveSession live;
    bool configured = false;
    if (!live.establish([&](SshSession& session) {
            // Pre-connect: 1 s period (libssh2 promotes it to its 2 s
            // minimum), 3 misses to judge a blackhole.
            configured = session.setKeepaliveConfig(1, 3);
        })) {
        GTEST_SKIP() << "set SSH_TEST_* plus SSH_TEST_FIXTURE_KEYS=1 to enable live keepalive";
    }
    ASSERT_TRUE(configured);
    // Past connect, the config no longer admits.
    EXPECT_FALSE(live.session().setKeepaliveConfig(5, 2));

    // Wait ~2 full keepalive ticks (first tick at t~1 s, then per
    // seconds_to_next=2 s).
    std::this_thread::sleep_for(4600ms);

    // Sends are flowing (>=2 ticks), misses stay 0 (the peer answers), the
    // session stays Established.
    EXPECT_GE(live.session().keepaliveSendCount(), 2u);
    EXPECT_EQ(live.session().keepaliveMissCount(), 0u);
    EXPECT_EQ(live.session().state(), SshSessionState::Established);
    EXPECT_FALSE(Visited(live.recorder(), SshSessionState::Disconnected))
        << "keepalive false-positive disconnect, lastError="
        << sshclient::ssh::toString(live.session().lastError());
}

TEST(KeepaliveIntegrationTest, DisabledKeepaliveSendsNothing)
{
    LiveSession live;
    live.options_.keepaliveIntervalSec = 0; // off
    if (!live.establish(nullptr)) {
        GTEST_SKIP() << "set SSH_TEST_* plus SSH_TEST_FIXTURE_KEYS=1 to enable live keepalive";
    }

    std::this_thread::sleep_for(1200ms);
    EXPECT_EQ(live.session().keepaliveSendCount(), 0u);
    EXPECT_EQ(live.session().keepaliveMissCount(), 0u);
    EXPECT_EQ(live.session().state(), SshSessionState::Established);
}

TEST(KeepaliveIntegrationTest, ProbeNowOnLiveSessionStaysAlive)
{
    LiveSession live;
    if (!live.establish(nullptr)) { // default 30 s period
        GTEST_SKIP() << "set SSH_TEST_* plus SSH_TEST_FIXTURE_KEYS=1 to enable live keepalive";
    }

    ASSERT_TRUE(live.session().probeNow(2)); // 2 s verdict window
    // A live peer answers the probe inside the window: no disconnect, and the
    // probe counter recorded the attempt.
    std::this_thread::sleep_for(3s);
    EXPECT_EQ(live.session().keepaliveProbeCount(), 1u);
    EXPECT_EQ(live.session().keepaliveMissCount(), 0u);
    EXPECT_EQ(live.session().state(), SshSessionState::Established);
    EXPECT_FALSE(Visited(live.recorder(), SshSessionState::Disconnected))
        << "probe misjudged a live peer, lastError="
        << sshclient::ssh::toString(live.session().lastError());
}
