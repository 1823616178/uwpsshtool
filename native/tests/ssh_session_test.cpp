#include "io/SessionThread.h" // winsock2.h must precede windows.h/gtest.
#include "io/WinsockInit.h"
#include "ssh/session.h"

#include <gtest/gtest.h>

#include <ws2tcpip.h>

#include <atomic>
#include <chrono>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include "sshd_testkit.h"

using namespace std::chrono_literals;
using sshclient::io::SessionThread;
using sshclient::io::WinsockInit;
using sshclient::ssh::SshSession;
using sshclient::ssh::SshSessionError;
using sshclient::ssh::SshSessionOptions;
using sshclient::ssh::SshSessionState;

namespace {

std::uint16_t PickClosedPort()
{
    WinsockInit winsock;
    if (!winsock.isValid()) {
        return 0;
    }
    const SOCKET socket = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (socket == INVALID_SOCKET) {
        return 0;
    }
    sockaddr_in address{};
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = ::htonl(INADDR_LOOPBACK);
    address.sin_port = 0;
    int length = sizeof(address);
    std::uint16_t port = 0;
    if (::bind(socket, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) == 0 &&
        ::getsockname(socket, reinterpret_cast<sockaddr*>(&address), &length) == 0) {
        port = ::ntohs(address.sin_port);
    }
    ::closesocket(socket);
    return port;
}

class DeadEndServer final {
public:
    explicit DeadEndServer(std::string payload) : payload_(std::move(payload)) {}
    ~DeadEndServer() { stop(); }

    bool start()
    {
        if (!winsock_.isValid()) {
            return false;
        }
        listener_ = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (listener_ == INVALID_SOCKET) {
            return false;
        }
        const BOOL reuse = TRUE;
        ::setsockopt(listener_, SOL_SOCKET, SO_REUSEADDR,
                     reinterpret_cast<const char*>(&reuse), sizeof(reuse));
        u_long nonBlocking = 1;
        ::ioctlsocket(listener_, FIONBIO, &nonBlocking);

        sockaddr_in address{};
        address.sin_family = AF_INET;
        address.sin_addr.s_addr = ::htonl(INADDR_LOOPBACK);
        address.sin_port = 0;
        int length = sizeof(address);
        if (::bind(listener_, reinterpret_cast<const sockaddr*>(&address), sizeof(address)) != 0 ||
            ::listen(listener_, 4) != 0 ||
            ::getsockname(listener_, reinterpret_cast<sockaddr*>(&address), &length) != 0) {
            ::closesocket(listener_);
            listener_ = INVALID_SOCKET;
            return false;
        }
        port_ = ::ntohs(address.sin_port);
        thread_ = std::thread([this] { serve(); });
        return true;
    }

    std::uint16_t port() const { return port_; }

    void stop()
    {
        stop_.store(true);
        if (thread_.joinable()) {
            thread_.join();
        }
        if (listener_ != INVALID_SOCKET) {
            ::closesocket(listener_);
            listener_ = INVALID_SOCKET;
        }
    }

private:
    void serve()
    {
        SOCKET client = INVALID_SOCKET;
        while (!stop_.load()) {
            WSAPOLLFD descriptor{listener_, POLLRDNORM, 0};
            if (::WSAPoll(&descriptor, 1, 50) <= 0) {
                continue;
            }
            client = ::accept(listener_, nullptr, nullptr);
            if (client != INVALID_SOCKET) {
                break;
            }
        }
        if (client == INVALID_SOCKET) {
            return;
        }

        u_long nonBlocking = 1;
        ::ioctlsocket(client, FIONBIO, &nonBlocking);
        if (!payload_.empty()) {
            ::send(client, payload_.data(), static_cast<int>(payload_.size()), 0);
        }
        char buffer[512];
        while (!stop_.load()) {
            WSAPOLLFD descriptor{client, POLLRDNORM, 0};
            const int ready = ::WSAPoll(&descriptor, 1, 50);
            if (ready > 0 && ::recv(client, buffer, sizeof(buffer), 0) <= 0) {
                break;
            }
        }
        ::closesocket(client);
    }

    WinsockInit winsock_;
    std::string payload_;
    SOCKET listener_ = INVALID_SOCKET;
    std::uint16_t port_ = 0;
    std::atomic<bool> stop_{false};
    std::thread thread_;
};

} // namespace

TEST(SshSessionStateMachineTest, TransitionTableRejectsSkipsAndTerminalRestarts)
{
    using State = SshSessionState;
    EXPECT_TRUE(SshSession::isLegalTransition(State::Idle, State::Connecting));
    EXPECT_TRUE(SshSession::isLegalTransition(State::Connecting, State::Handshaking));
    EXPECT_TRUE(SshSession::isLegalTransition(State::Handshaking, State::Authenticating));
    EXPECT_TRUE(SshSession::isLegalTransition(State::Authenticating, State::Established));
    EXPECT_TRUE(SshSession::isLegalTransition(State::Established, State::Closing));
    EXPECT_TRUE(SshSession::isLegalTransition(State::Closing, State::Closed));
    EXPECT_TRUE(SshSession::isLegalTransition(State::Connecting, State::Error));
    EXPECT_TRUE(SshSession::isLegalTransition(State::Established, State::Disconnected));

    EXPECT_FALSE(SshSession::isLegalTransition(State::Idle, State::Established));
    EXPECT_FALSE(SshSession::isLegalTransition(State::Connecting, State::Established));
    EXPECT_FALSE(SshSession::isLegalTransition(State::Authenticating, State::Connecting));
    EXPECT_FALSE(SshSession::isLegalTransition(State::Closed, State::Connecting));
    EXPECT_FALSE(SshSession::isLegalTransition(State::Disconnected, State::Idle));
    EXPECT_FALSE(SshSession::isLegalTransition(State::Error, State::Connecting));
}

TEST(SshSessionStateMachineTest, CloseFromIdleIsNoop)
{
    SessionThread thread;
    ASSERT_TRUE(thread.start());
    StateRecorder recorder;
    {
        SshSession session(thread, {}, std::ref(recorder));
        session.close();
        std::this_thread::sleep_for(50ms);
        EXPECT_EQ(SshSessionState::Idle, session.state());
        EXPECT_TRUE(recorder.sequence().empty());
        thread.stop();
    }
}

TEST(SshSessionStateMachineTest, DuplicateConnectIsRejected)
{
    SessionThread thread;
    ASSERT_TRUE(thread.start());
    StateRecorder recorder;
    {
        SshSession session(thread, {}, std::ref(recorder));
        const std::uint16_t port = PickClosedPort();
        ASSERT_NE(0, port);
        ASSERT_TRUE(session.connect("127.0.0.1", port, "tester"));
        EXPECT_FALSE(session.connect("127.0.0.1", port, "tester"));
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Error, 5s));
        EXPECT_FALSE(session.connect("127.0.0.1", port, "tester"));
        thread.stop();
    }
    EXPECT_EQ((std::vector<SshSessionState>{SshSessionState::Connecting,
                                             SshSessionState::Error}),
              recorder.sequence());
}

TEST(SshSessionStateMachineTest, CloseImmediatelyAfterConnectIsNotLost)
{
    DeadEndServer server("");
    ASSERT_TRUE(server.start());
    SessionThread thread;
    StateRecorder recorder;
    {
        SshSession session(thread, {}, std::ref(recorder));
        ASSERT_TRUE(session.connect("127.0.0.1", server.port(), "tester"));
        session.close();
        ASSERT_TRUE(thread.start());
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Closed, 5s));
        thread.stop();
    }
    server.stop();
    EXPECT_EQ((std::vector<SshSessionState>{SshSessionState::Connecting,
                                             SshSessionState::Closing,
                                             SshSessionState::Closed}),
              recorder.sequence());
}

TEST(SshSessionStateMachineTest, ClosedPortFailsWithoutWaitingForTimeout)
{
    SessionThread thread;
    ASSERT_TRUE(thread.start());
    StateRecorder recorder;
    {
        SshSessionOptions options;
        options.connectTimeoutMs = 3000;
        SshSession session(thread, options, std::ref(recorder));
        const std::uint16_t port = PickClosedPort();
        ASSERT_NE(0, port);
        const auto started = std::chrono::steady_clock::now();
        ASSERT_TRUE(session.connect("127.0.0.1", port, "tester"));
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Error, 5s));
        EXPECT_EQ(SshSessionError::ConnectionRefused, session.lastError());
        // Windows WSAPoll may defer a refused-connect notification for about
        // two seconds, but it must still beat the configured timeout.
        EXPECT_LT(std::chrono::steady_clock::now() - started, 3s);
        thread.stop();
    }
}

TEST(SshSessionStateMachineTest, UnreachableAddressConvergesToTimeoutOrUnreachable)
{
    SessionThread thread;
    ASSERT_TRUE(thread.start());
    StateRecorder recorder;
    {
        SshSessionOptions options;
        options.connectTimeoutMs = 400;
        options.handshakeTimeoutMs = 800;
        SshSession session(thread, options, std::ref(recorder));
        const auto started = std::chrono::steady_clock::now();
        // RFC 3849 documentation prefix: normally no IPv6 route exists, so
        // Windows reports WSAENETUNREACH; if a route silently drops it, our
        // application timer still produces ConnectTimeout.
        ASSERT_TRUE(session.connect("2001:db8::1", 22, "tester"));
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Error, 4s));
        EXPECT_TRUE(session.lastError() == SshSessionError::ConnectTimeout ||
                    session.lastError() == SshSessionError::NetworkUnreachable)
            << "error=" << static_cast<int>(session.lastError());
        EXPECT_LT(std::chrono::steady_clock::now() - started, 4s);
        thread.stop();
    }
}

TEST(SshSessionStateMachineTest, SilentServerHitsHandshakeTimeout)
{
    DeadEndServer server("");
    ASSERT_TRUE(server.start());
    SessionThread thread;
    ASSERT_TRUE(thread.start());
    StateRecorder recorder;
    {
        SshSessionOptions options;
        options.handshakeTimeoutMs = 300;
        SshSession session(thread, options, std::ref(recorder));
        ASSERT_TRUE(session.connect("127.0.0.1", server.port(), "tester"));
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Error, 5s));
        EXPECT_EQ(SshSessionError::HandshakeTimeout, session.lastError());
        thread.stop();
    }
    server.stop();
    EXPECT_EQ((std::vector<SshSessionState>{SshSessionState::Connecting,
                                             SshSessionState::Handshaking,
                                             SshSessionState::Error}),
              recorder.sequence());
}

TEST(SshSessionIntegrationTest, EnvironmentServerReachesAuthenticatingAndCloses)
{
    SshIntegrationEnvironment environment;
    if (!LoadSshIntegrationEnvironment(environment)) {
        GTEST_SKIP() << "set SSH_TEST_HOST/PORT/USER/PASSWORD to enable live SSH handshake";
    }

    SessionThread thread;
    ASSERT_TRUE(thread.start());
    StateRecorder recorder;
    {
        SshSession session(thread, {}, std::ref(recorder));
        ASSERT_TRUE(session.connect(environment.host, environment.port, environment.user));
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Authenticating, 20s));
        EXPECT_EQ(SshSessionError::None, session.lastError());

        session.close();
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Closed, 5s));
        thread.stop();
    }

    EXPECT_EQ((std::vector<SshSessionState>{SshSessionState::Connecting,
                                             SshSessionState::Handshaking,
                                             SshSessionState::Authenticating,
                                             SshSessionState::Closing,
                                             SshSessionState::Closed}),
              recorder.sequence());
    EXPECT_FALSE(environment.password.empty()); // Presence gates the integration test; never logged.
}
