// F04: native forwarding — local_listener / direct_tcpip / remote_listen /
// pump (01-DESIGN.md section 11.2).
//
// Offline unit tests (no server):
//   - BidirectionalPump over scripted fake endpoints: bidirectional flow,
//     partial-write reassembly, EAGAIN stall + resume, EOF half-close waiting
//     for the drain, error teardown, backpressure interest hints, half-close
//     RetryLater, stats accounting;
//   - TunnelStats snapshot;
//   - ForwardState / RemoteListenState legality tables;
//   - admission guards on an Idle session (openLocal/openRemote/listener
//     start rejected; close/stop without open are safe no-ops);
//   - pump over real loopback TCP sockets (no SSH): 1 MB one way, small reply
//     the other, then half-close to a graceful finish.
// Integration tests (SSH_TEST_HOST/PORT/USER/PASSWORD, else GTEST_SKIP):
//   - local forward to a far-end HTTP service (SSH_TEST_FWD_HTTP=host:port,
//     as the SSH server sees it): GET / through 127.0.0.1:<bound> returns
//     "HTTP/..." plus stats assertions;
//   - remote forward echo round trip (SSH_TEST_FWD_REMOTE=1, only when the
//     SSH server is localhost-reachable): the server-side bound port relays
//     to a test-machine echo target.
#include "io/SessionThread.h" // winsock2.h must precede windows.h/gtest.
#include "fwd/direct_tcpip.h"
#include "fwd/local_listener.h"
#include "fwd/pump.h"
#include "fwd/remote_listen.h"
#include "ssh/session.h"

#include <gtest/gtest.h>

#include <libssh2.h>
#include <ws2tcpip.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <deque>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "sshd_testkit.h"

using namespace std::chrono_literals;
using sshclient::fwd::BidirectionalPump;
using sshclient::fwd::EofSendResult;
using sshclient::fwd::ForwardCloseInfo;
using sshclient::fwd::ForwardCloseReason;
using sshclient::fwd::ForwardedCallbacks;
using sshclient::fwd::ForwardedConnection;
using sshclient::fwd::ForwardOpenError;
using sshclient::fwd::ForwardOpenResult;
using sshclient::fwd::ForwardState;
using sshclient::fwd::IPumpEndpoint;
using sshclient::fwd::LocalListener;
using sshclient::fwd::LocalListenerCallbacks;
using sshclient::fwd::LocalListenResult;
using sshclient::fwd::LocalListenState;
using sshclient::fwd::PumpDriveReport;
using sshclient::fwd::PumpFinishReason;
using sshclient::fwd::PumpReadResult;
using sshclient::fwd::PumpWriteResult;
using sshclient::fwd::RemoteListener;
using sshclient::fwd::RemoteListenerCallbacks;
using sshclient::fwd::RemoteListenResult;
using sshclient::fwd::RemoteListenState;
using sshclient::fwd::TunnelStats;
using sshclient::fwd::snapshotTunnelStats;
using sshclient::io::SessionThread;
using sshclient::ssh::SshSession;
using sshclient::ssh::SshSessionState;

namespace {

// ================================================================== fakes

// Scripted pump endpoint: reads pop queued results (empty queue = stalled);
// writes sink bytes with an optional per-call cap (partial writes) and an
// optional stall countdown; sendEof pops scripted outcomes (empty = Done).
class ScriptEndpoint final : public IPumpEndpoint {
public:
    std::deque<PumpReadResult> readScript;
    size_t writeCap = SIZE_MAX;
    int stallWrites = 0;
    bool failWrites = false;
    std::string written;
    std::deque<EofSendResult> eofScript;
    int eofCalls = 0;

    PumpReadResult read(size_t maxLen) override
    {
        if (readScript.empty()) {
            return PumpReadResult::stalled();
        }
        PumpReadResult result = std::move(readScript.front());
        readScript.pop_front();
        if (result.kind == PumpReadResult::Kind::Data && result.bytes.size() > maxLen) {
            readScript.push_front(PumpReadResult::data(result.bytes.substr(maxLen)));
            result.bytes.resize(maxLen);
        }
        return result;
    }

    PumpWriteResult write(const char* data, size_t len) override
    {
        if (failWrites) {
            return PumpWriteResult::error("injected write error");
        }
        if (stallWrites > 0) {
            --stallWrites;
            return PumpWriteResult::stalled();
        }
        size_t n = len < writeCap ? len : writeCap;
        if (n == 0) {
            return PumpWriteResult::stalled();
        }
        written.append(data, n);
        return PumpWriteResult::data(n);
    }

    EofSendResult sendEof() override
    {
        ++eofCalls;
        if (eofScript.empty()) {
            return EofSendResult::Done;
        }
        const EofSendResult result = eofScript.front();
        eofScript.pop_front();
        return result;
    }
};

std::string PatternData(size_t bytes)
{
    std::string out;
    out.reserve(bytes);
    for (size_t i = 0; i < bytes; ++i) {
        out.push_back(static_cast<char>((i * 31 + 7) & 0xFF));
    }
    return out;
}

// Drive until no pass reports progress (bounded: stalls stop the loop).
PumpDriveReport driveAll(BidirectionalPump& pump, int maxPasses = 10000)
{
    PumpDriveReport last;
    for (int i = 0; i < maxPasses; ++i) {
        last = pump.drive();
        if (!last.progress) {
            break;
        }
    }
    return last;
}

} // namespace

// ================================================================== pump core

TEST(PumpCoreTest, BidirectionalFlowExchangesBothWays)
{
    ScriptEndpoint sideA;
    ScriptEndpoint sideB;
    sideA.readScript.push_back(PumpReadResult::data("ping"));
    sideB.readScript.push_back(PumpReadResult::data("pong"));
    TunnelStats stats;
    BidirectionalPump pump(sideA, sideB, &stats);
    driveAll(pump);
    EXPECT_EQ(sideB.written, "ping");
    EXPECT_EQ(sideA.written, "pong");
    EXPECT_FALSE(pump.finished());
    EXPECT_EQ(snapshotTunnelStats(stats).bytesUp, 4u);
    EXPECT_EQ(snapshotTunnelStats(stats).bytesDown, 4u);
}

TEST(PumpCoreTest, PartialWritesReassembleExactly)
{
    ScriptEndpoint sideA;
    ScriptEndpoint sideB;
    const std::string up = PatternData(100000);
    const std::string down = PatternData(70000);
    sideA.readScript.push_back(PumpReadResult::data(up));
    sideB.readScript.push_back(PumpReadResult::data(down));
    sideA.writeCap = 3; // at most 3 bytes per push: partial-write path
    sideB.writeCap = 5;
    BidirectionalPump pump(sideA, sideB, nullptr);
    driveAll(pump);
    EXPECT_EQ(sideB.written, up);
    EXPECT_EQ(sideA.written, down);
    EXPECT_EQ(pump.bufferedAtoB(), 0u);
    EXPECT_EQ(pump.bufferedBtoA(), 0u);
}

TEST(PumpCoreTest, EagainStallPausesAndResumesWithoutLoss)
{
    ScriptEndpoint sideA; // socket side: steady source
    ScriptEndpoint sideB; // channel side: write-blocked twice, then drains
    sideA.readScript.push_back(PumpReadResult::data("stalled-bytes"));
    sideB.stallWrites = 2;
    BidirectionalPump pump(sideA, sideB, nullptr);
    bool sawWriteStall = false;
    for (int i = 0; i < 10 && sideB.written.empty(); ++i) {
        const PumpDriveReport report = pump.drive();
        sawWriteStall = sawWriteStall || report.writeStalledB;
    }
    EXPECT_TRUE(sawWriteStall); // the EAGAIN was observed, not spun past
    driveAll(pump);
    EXPECT_EQ(sideB.written, "stalled-bytes");
    EXPECT_FALSE(pump.finished());
}

TEST(PumpCoreTest, EofHalfCloseWaitsForTheDrain)
{
    ScriptEndpoint sideA;
    ScriptEndpoint sideB;
    sideA.readScript.push_back(PumpReadResult::data("tail"));
    sideA.readScript.push_back(PumpReadResult::eof());
    sideB.stallWrites = 1000; // channel not draining: EOF must not propagate yet
    BidirectionalPump pump(sideA, sideB, nullptr);
    driveAll(pump);
    EXPECT_TRUE(pump.eofSeenA());
    EXPECT_EQ(pump.bufferedAtoB(), 4u);
    EXPECT_EQ(sideB.eofCalls, 0); // half-close held until the drain
    EXPECT_FALSE(pump.finished());

    sideB.stallWrites = 0;
    driveAll(pump);
    EXPECT_EQ(sideB.written, "tail");
    EXPECT_EQ(sideB.eofCalls, 1); // EOF propagated once drained
    EXPECT_FALSE(pump.finished()); // side B still open

    sideB.readScript.push_back(PumpReadResult::eof());
    driveAll(pump);
    EXPECT_TRUE(pump.finished());
    EXPECT_EQ(pump.finishReason(), PumpFinishReason::GracefulEof);
    EXPECT_EQ(sideA.eofCalls, 1);
    EXPECT_FALSE(pump.wantReadA());
    EXPECT_FALSE(pump.wantReadB());
    EXPECT_FALSE(pump.wantWriteA());
    EXPECT_FALSE(pump.wantWriteB());
}

TEST(PumpCoreTest, ErrorFinishesWithMessage)
{
    ScriptEndpoint sideA;
    ScriptEndpoint sideB;
    sideA.readScript.push_back(PumpReadResult::data("some"));
    sideB.readScript.push_back(PumpReadResult::error("channel read failed: boom"));
    BidirectionalPump pump(sideA, sideB, nullptr);
    driveAll(pump);
    EXPECT_TRUE(pump.finished());
    EXPECT_EQ(pump.finishReason(), PumpFinishReason::Error);
    EXPECT_EQ(pump.errorMessage(), "channel read failed: boom");
    EXPECT_FALSE(pump.wantReadA());
    EXPECT_FALSE(pump.wantWriteB());
}

TEST(PumpCoreTest, BackpressureHintsGateReads)
{
    ScriptEndpoint sideA;
    ScriptEndpoint sideB;
    sideA.readScript.push_back(PumpReadResult::data(std::string(100, 'x')));
    sideB.stallWrites = 1000; // never drains while stalled
    BidirectionalPump pump(sideA, sideB, nullptr);
    pump.setMaxBufferedBytes(10);
    driveAll(pump);
    // The channel write is blocked: the socket side must pause (dropping the
    // Readable interest), holding exactly the cap.
    EXPECT_EQ(pump.bufferedAtoB(), 10u);
    EXPECT_FALSE(pump.wantReadA());
    EXPECT_TRUE(pump.wantWriteB());

    sideB.stallWrites = 0;
    driveAll(pump);
    EXPECT_EQ(sideB.written, std::string(100, 'x'));
    EXPECT_TRUE(pump.wantReadA()); // drained: reading may resume
}

TEST(PumpCoreTest, SendEofRetryLaterDelaysCompletion)
{
    ScriptEndpoint sideA;
    ScriptEndpoint sideB;
    sideA.readScript.push_back(PumpReadResult::eof());
    sideB.readScript.push_back(PumpReadResult::eof());
    sideB.eofScript.push_back(EofSendResult::RetryLater);
    sideB.eofScript.push_back(EofSendResult::RetryLater);
    sideB.eofScript.push_back(EofSendResult::Done);
    BidirectionalPump pump(sideA, sideB, nullptr);
    pump.drive();
    EXPECT_FALSE(pump.finished());
    EXPECT_EQ(sideB.eofCalls, 1);
    pump.drive();
    EXPECT_FALSE(pump.finished());
    EXPECT_EQ(sideB.eofCalls, 2);
    driveAll(pump);
    EXPECT_TRUE(pump.finished());
    EXPECT_EQ(pump.finishReason(), PumpFinishReason::GracefulEof);
    EXPECT_EQ(sideB.eofCalls, 3);
}

TEST(TunnelStatsTest, SnapshotReadsCounters)
{
    TunnelStats stats;
    stats.activeConnections.store(3, std::memory_order_relaxed);
    stats.totalConnections.store(7, std::memory_order_relaxed);
    stats.bytesUp.store(100, std::memory_order_relaxed);
    stats.bytesDown.store(200, std::memory_order_relaxed);
    const auto snapshot = snapshotTunnelStats(stats);
    EXPECT_EQ(snapshot.activeConnections, 3u);
    EXPECT_EQ(snapshot.totalConnections, 7u);
    EXPECT_EQ(snapshot.bytesUp, 100u);
    EXPECT_EQ(snapshot.bytesDown, 200u);
}

// ================================================================== transitions

TEST(ForwardTransitionTest, LegalityTable)
{
    using S = ForwardState;
    const S states[] = {S::Idle, S::Opening, S::Pumping, S::Closing, S::Closed};
    auto expected = [](S from, S to) {
        switch (from) {
        case S::Idle:
            return to == S::Opening;
        case S::Opening:
            return to == S::Pumping || to == S::Closing || to == S::Closed;
        case S::Pumping:
            return to == S::Closing || to == S::Closed;
        case S::Closing:
            return to == S::Closed;
        case S::Closed:
            return false;
        }
        return false;
    };
    for (const S from : states) {
        for (const S to : states) {
            EXPECT_EQ(ForwardedConnection::isLegalTransition(from, to), expected(from, to))
                << "from=" << static_cast<int>(from) << " to=" << static_cast<int>(to);
        }
    }
}

TEST(RemoteListenTransitionTest, LegalityTable)
{
    using S = RemoteListenState;
    const S states[] = {S::Idle, S::Opening, S::Listening, S::Cancelling, S::Stopped};
    auto expected = [](S from, S to) {
        switch (from) {
        case S::Idle:
            return to == S::Opening;
        case S::Opening:
            return to == S::Listening || to == S::Cancelling || to == S::Stopped;
        case S::Listening:
            return to == S::Cancelling || to == S::Stopped;
        case S::Cancelling:
            return to == S::Stopped;
        case S::Stopped:
            return false;
        }
        return false;
    };
    for (const S from : states) {
        for (const S to : states) {
            EXPECT_EQ(RemoteListener::isLegalTransition(from, to), expected(from, to))
                << "from=" << static_cast<int>(from) << " to=" << static_cast<int>(to);
        }
    }
}

// ================================================================== admission (offline)

TEST(ForwardAdmissionTest, RejectedWhenNotEstablished)
{
    SessionThread thread;
    ASSERT_TRUE(thread.start());
    {
        SshSession session(thread, {}, nullptr); // Idle: never connected
        // A real (unconnected) socket: admission must fail at the session
        // gate before touching it; the Idle destructor reclaims it quietly.
        SOCKET raw = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        ASSERT_NE(raw, INVALID_SOCKET);
        ForwardedConnection local(session, raw, nullptr, {});
        EXPECT_EQ(local.state(), ForwardState::Idle);
        EXPECT_FALSE(local.openLocal("example.com", 80, "127.0.0.1", 0));
        EXPECT_FALSE(local.openLocal("", 80, "127.0.0.1", 0));
        EXPECT_FALSE(local.openLocal("example.com", 0, "127.0.0.1", 0));
        // Remote half: an accepted channel the session gate refuses before
        // any libssh2 call (the pointer is never dereferenced here).
        auto* fakeChannel = reinterpret_cast<struct _LIBSSH2_CHANNEL*>(0x1);
        ForwardedConnection remote(session, fakeChannel, nullptr, {});
        EXPECT_FALSE(remote.openRemote("127.0.0.1", 8080));
        EXPECT_FALSE(remote.openRemote("", 8080));
        // Data-plane calls are safe no-ops before an open.
        local.close(); // never opened: nothing posted
        remote.close();
        EXPECT_EQ(local.state(), ForwardState::Idle);
        EXPECT_EQ(remote.state(), ForwardState::Idle);
        thread.stop();
    }
}

TEST(ForwardAdmissionTest, ListenerStartRejectedWhenNotEstablished)
{
    SessionThread thread;
    ASSERT_TRUE(thread.start());
    {
        SshSession session(thread, {}, nullptr); // Idle
        LocalListener local(session, "example.com", 80, nullptr, {});
        EXPECT_FALSE(local.start("127.0.0.1", 0));
        local.stop(); // safe without a start
        EXPECT_EQ(local.state(), LocalListenState::Idle);

        RemoteListener remote(session, "127.0.0.1", 8080, nullptr, {});
        EXPECT_FALSE(remote.start("127.0.0.1", 0));
        remote.stop();
        EXPECT_EQ(remote.state(), RemoteListenState::Idle);

        // Missing destinations reject before the session gate.
        LocalListener noDest(session, "", 0, nullptr, {});
        EXPECT_FALSE(noDest.start("127.0.0.1", 0));
        RemoteListener noTarget(session, "", 0, nullptr, {});
        EXPECT_FALSE(noTarget.start("127.0.0.1", 0));
        thread.stop();
    }
}

// ================================================================== loopback pump (real sockets, no SSH)

namespace {

// Non-blocking loopback TCP pair (same process): server accepts one client.
struct LoopbackPair {
    SOCKET listener = INVALID_SOCKET;
    SOCKET server = INVALID_SOCKET;
    SOCKET client = INVALID_SOCKET;
    std::uint16_t port = 0;

    bool open()
    {
        listener = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (listener == INVALID_SOCKET) {
            return false;
        }
        sockaddr_in addr{};
        addr.sin_family = AF_INET;
        addr.sin_addr.s_addr = ::htonl(INADDR_LOOPBACK);
        addr.sin_port = 0;
        if (::bind(listener, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) != 0) {
            return false;
        }
        if (::listen(listener, 1) != 0) {
            return false;
        }
        int len = sizeof(addr);
        if (::getsockname(listener, reinterpret_cast<sockaddr*>(&addr), &len) != 0) {
            return false;
        }
        port = ::ntohs(addr.sin_port);
        client = ::socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (client == INVALID_SOCKET) {
            return false;
        }
        addr.sin_port = ::htons(port);
        if (::connect(client, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) != 0) {
            return false;
        }
        server = ::accept(listener, nullptr, nullptr);
        if (server == INVALID_SOCKET) {
            return false;
        }
        u_long nonBlocking = 1;
        if (::ioctlsocket(server, FIONBIO, &nonBlocking) != 0 ||
            ::ioctlsocket(client, FIONBIO, &nonBlocking) != 0) {
            return false;
        }
        // Bounded waits: a stuck pump must fail the test, never hang it.
        DWORD timeoutMs = 5000;
        ::setsockopt(client, SOL_SOCKET, SO_RCVTIMEO,
                     reinterpret_cast<const char*>(&timeoutMs), sizeof(timeoutMs));
        ::setsockopt(server, SOL_SOCKET, SO_RCVTIMEO,
                     reinterpret_cast<const char*>(&timeoutMs), sizeof(timeoutMs));
        return true;
    }

    void close()
    {
        for (SOCKET* s : {&client, &server, &listener}) {
            if (*s != INVALID_SOCKET) {
                ::closesocket(*s);
                *s = INVALID_SOCKET;
            }
        }
    }
};

// Real-socket pump endpoint (owns nothing; the pair outlives the test).
class SocketTestEndpoint final : public IPumpEndpoint {
public:
    explicit SocketTestEndpoint(SOCKET socket) : socket_(socket) {}

    PumpReadResult read(size_t maxLen) override
    {
        char buffer[32768];
        const size_t want = maxLen < sizeof(buffer) ? maxLen : sizeof(buffer);
        const int received =
            ::recv(socket_, buffer, static_cast<int>(want), 0);
        if (received > 0) {
            return PumpReadResult::data(
                std::string(buffer, static_cast<size_t>(received)));
        }
        if (received == 0) {
            return PumpReadResult::eof();
        }
        return ::WSAGetLastError() == WSAEWOULDBLOCK
                   ? PumpReadResult::stalled()
                   : PumpReadResult::error("loopback recv failed");
    }

    PumpWriteResult write(const char* data, size_t len) override
    {
        const int sent = ::send(socket_, data, static_cast<int>(len), 0);
        if (sent > 0) {
            return PumpWriteResult::data(static_cast<size_t>(sent));
        }
        if (sent == 0) {
            return PumpWriteResult::stalled();
        }
        return ::WSAGetLastError() == WSAEWOULDBLOCK
                   ? PumpWriteResult::stalled()
                   : PumpWriteResult::error("loopback send failed");
    }

    EofSendResult sendEof() override
    {
        return ::shutdown(socket_, SD_SEND) == 0 ? EofSendResult::Done
                                                 : EofSendResult::GiveUp;
    }

private:
    SOCKET socket_;
};

bool sendAll(SOCKET socket, const std::string& data)
{
    size_t done = 0;
    while (done < data.size()) {
        const int sent =
            ::send(socket, data.data() + done, static_cast<int>(data.size() - done), 0);
        if (sent > 0) {
            done += static_cast<size_t>(sent);
            continue;
        }
        if (sent == 0) {
            continue;
        }
        if (::WSAGetLastError() != WSAEWOULDBLOCK) {
            return false;
        }
        WSAPOLLFD fd{socket, POLLWRNORM, 0};
        if (::WSAPoll(&fd, 1, 5000) <= 0) {
            return false;
        }
    }
    return true;
}

} // namespace

TEST(PumpLoopbackTest, ExchangesAndHalfClosesGracefully)
{
    sshclient::io::WinsockInit winsock; // no SessionThread here: init directly
    ASSERT_TRUE(winsock.isValid());
    LoopbackPair pair;
    ASSERT_TRUE(pair.open());
    SocketTestEndpoint sideA(pair.server); // "socket" side: the accepted end
    ScriptEndpoint sideB;                  // "channel" side: scripted far end
    const std::string down = PatternData(1024 * 1024); // far end -> client (1 MB)
    sideB.readScript.push_back(PumpReadResult::data(down));
    sideB.readScript.push_back(PumpReadResult::eof());
    TunnelStats stats;
    BidirectionalPump pump(sideA, sideB, &stats);

    // Client -> far end first (small, exercises the A->B direction).
    ASSERT_TRUE(sendAll(pair.client, "up-data"));
    // Then run the pump while pulling the client's inbound into a sink.
    std::string sink;
    bool clientEof = false;
    const auto deadline = std::chrono::steady_clock::now() + 30s;
    while (!pump.finished() && std::chrono::steady_clock::now() < deadline) {
        WSAPOLLFD fds[2]{{pair.server, POLLRDNORM | POLLWRNORM, 0},
                         {pair.client, POLLRDNORM, 0}};
        ::WSAPoll(fds, 2, 50);
        pump.drive();
        // Drain whatever arrived at the client (non-blocking: stop at stall).
        for (;;) {
            char buffer[65536];
            const int received =
                ::recv(pair.client, buffer, sizeof(buffer), 0);
            if (received > 0) {
                sink.append(buffer, static_cast<size_t>(received));
                continue;
            }
            if (received == 0) {
                clientEof = true;
            }
            break;
        }
        if (sink.size() >= down.size() && !clientEof) {
            // Whole reply arrived: half-close the client so the A side EOFs.
            ::shutdown(pair.client, SD_SEND);
        }
    }
    ASSERT_TRUE(pump.finished()) << "pump stuck: bufferedAtoB=" << pump.bufferedAtoB()
                                 << " bufferedBtoA=" << pump.bufferedBtoA();
    EXPECT_EQ(pump.finishReason(), PumpFinishReason::GracefulEof);
    EXPECT_EQ(sink, down);
    EXPECT_EQ(sideB.written, "up-data");
    EXPECT_EQ(snapshotTunnelStats(stats).bytesUp, 7u);
    EXPECT_EQ(snapshotTunnelStats(stats).bytesDown, down.size());
    pair.close();
}

// ==================================================== integration (live server)

namespace {

bool ParseHostPort(const char* text, std::string& hostOut, std::uint16_t& portOut)
{
    if (text == nullptr || text[0] == '\0') {
        return false;
    }
    const std::string whole = text;
    const size_t colon = whole.rfind(':');
    if (colon == std::string::npos || colon == 0 || colon + 1 >= whole.size()) {
        return false;
    }
    try {
        const int port = std::stoi(whole.substr(colon + 1));
        if (port < 1 || port > 65535) {
            return false;
        }
        portOut = static_cast<std::uint16_t>(port);
    } catch (...) {
        return false;
    }
    hostOut = whole.substr(0, colon);
    return !hostOut.empty();
}

// Connect + password-auth into Established (sftp_test.cpp convention).
class LivePasswordSession {
public:
    ~LivePasswordSession() { shutdown(); }

    bool establish()
    {
        if (!LoadSshIntegrationEnvironment(env_)) {
            return false;
        }
        if (!thread_.start()) {
            return false;
        }
        session_ = std::make_unique<SshSession>(thread_, sshclient::ssh::SshSessionOptions{},
                                                std::ref(recorder_));
        if (!session_->connect(env_.host, env_.port, env_.user)) {
            return false;
        }
        if (!recorder_.waitFor(SshSessionState::Authenticating, 20s)) {
            return false;
        }
        std::string password = env_.password;
        std::mutex mutex;
        std::condition_variable cv;
        bool authed = false;
        bool ok = false;
        if (!session_->authenticatePassword(
                password, [&](const sshclient::ssh::AuthResult& result) {
                    {
                        std::lock_guard<std::mutex> lock(mutex);
                        authed = true;
                        ok = result.success;
                    }
                    cv.notify_all();
                })) {
            return false;
        }
        {
            std::unique_lock<std::mutex> lock(mutex);
            if (!cv.wait_for(lock, 15s, [&] { return authed; })) {
                return false;
            }
        }
        return ok && recorder_.waitFor(SshSessionState::Established, 10s);
    }

    void shutdown()
    {
        if (session_) {
            session_->close();
            recorder_.waitFor(SshSessionState::Closed, 10s);
            session_.reset();
        }
        thread_.stop();
    }

    SshSession& session() { return *session_; }
    const SshIntegrationEnvironment& env() const { return env_; }

private:
    SshIntegrationEnvironment env_;
    SessionThread thread_;
    StateRecorder recorder_;
    std::unique_ptr<SshSession> session_;
};

SOCKET BlockingConnect(const std::string& host, std::uint16_t port, DWORD timeoutMs = 10000)
{
    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;
    addrinfo* addresses = nullptr;
    if (::getaddrinfo(host.c_str(), std::to_string(port).c_str(), &hints, &addresses) != 0) {
        return INVALID_SOCKET;
    }
    SOCKET connected = INVALID_SOCKET;
    for (addrinfo* address = addresses; address != nullptr; address = address->ai_next) {
        SOCKET candidate = ::socket(address->ai_family, address->ai_socktype,
                                    address->ai_protocol);
        if (candidate == INVALID_SOCKET) {
            continue;
        }
        ::setsockopt(candidate, SOL_SOCKET, SO_RCVTIMEO,
                     reinterpret_cast<const char*>(&timeoutMs), sizeof(timeoutMs));
        ::setsockopt(candidate, SOL_SOCKET, SO_SNDTIMEO,
                     reinterpret_cast<const char*>(&timeoutMs), sizeof(timeoutMs));
        if (::connect(candidate, address->ai_addr,
                      static_cast<int>(address->ai_addrlen)) == 0) {
            connected = candidate;
            break;
        }
        ::closesocket(candidate);
    }
    ::freeaddrinfo(addresses);
    return connected;
}

bool WaitForActiveZero(const TunnelStats& stats, std::chrono::milliseconds timeout)
{
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    while (std::chrono::steady_clock::now() < deadline) {
        if (stats.activeConnections.load(std::memory_order_acquire) == 0) {
            return true;
        }
        std::this_thread::sleep_for(20ms);
    }
    return false;
}

} // namespace

TEST(FwdIntegrationTest, LocalForwardToHttpFetchesContent)
{
    std::string httpHost;
    std::uint16_t httpPort = 0;
    if (!ParseHostPort(std::getenv("SSH_TEST_FWD_HTTP"), httpHost, httpPort)) {
        GTEST_SKIP() << "set SSH_TEST_FWD_HTTP=host:port (as the SSH server sees it, "
                        "e.g. 127.0.0.1:8000 with `python3 -m http.server` on the "
                        "server) to enable the local-forward integration test";
    }
    LivePasswordSession live;
    if (!live.establish()) {
        GTEST_SKIP() << "set SSH_TEST_HOST/PORT/USER/PASSWORD to enable live forwarding";
    }

    std::mutex mutex;
    std::condition_variable cv;
    bool listeningFired = false;
    LocalListenResult listenResult;
    bool stoppedFired = false;
    LocalListenerCallbacks callbacks;
    callbacks.onListening = [&](const LocalListenResult& result) {
        {
            std::lock_guard<std::mutex> lock(mutex);
            listeningFired = true;
            listenResult = result;
        }
        cv.notify_all();
    };
    callbacks.onStopped = [&] {
        {
            std::lock_guard<std::mutex> lock(mutex);
            stoppedFired = true;
        }
        cv.notify_all();
    };
    auto stats = std::make_shared<TunnelStats>();
    LocalListener listener(live.session(), httpHost, httpPort, stats, std::move(callbacks));
    ASSERT_TRUE(listener.start("127.0.0.1", 0));
    {
        std::unique_lock<std::mutex> lock(mutex);
        ASSERT_TRUE(cv.wait_for(lock, 15s, [&] { return listeningFired; }));
        ASSERT_TRUE(listenResult.success) << listenResult.message;
        ASSERT_NE(listenResult.boundPort, 0);
    }

    // Fetch through the tunnel: the bytes travel client -> direct-tcpip ->
    // server -> HTTP service and back.
    const SOCKET client = BlockingConnect("127.0.0.1", listenResult.boundPort);
    ASSERT_NE(client, INVALID_SOCKET);
    const std::string request = "GET / HTTP/1.0\r\nHost: " + httpHost +
                                "\r\nConnection: close\r\n\r\n";
    size_t sent = 0;
    while (sent < request.size()) {
        const int n =
            ::send(client, request.data() + sent, static_cast<int>(request.size() - sent), 0);
        ASSERT_GT(n, 0);
        sent += static_cast<size_t>(n);
    }
    std::string response;
    for (;;) {
        char buffer[65536];
        const int n = ::recv(client, buffer, sizeof(buffer), 0);
        if (n > 0) {
            response.append(buffer, static_cast<size_t>(n));
            continue;
        }
        break; // 0 = server closed after the reply; timeout/error ends it too
    }
    ::closesocket(client);
    EXPECT_NE(response.find("HTTP/"), std::string::npos) << response.substr(0, 200);

    // Graceful close: the connection drains (active back to 0) with byte
    // counters flowing both ways.
    EXPECT_TRUE(WaitForActiveZero(*stats, 10s));
    const auto snapshot = snapshotTunnelStats(*stats);
    EXPECT_GE(snapshot.totalConnections, 1u);
    EXPECT_GT(snapshot.bytesUp, 0u);
    EXPECT_GT(snapshot.bytesDown, 0u);

    listener.stop();
    {
        std::unique_lock<std::mutex> lock(mutex);
        EXPECT_TRUE(cv.wait_for(lock, 10s, [&] { return stoppedFired; }));
    }
}

TEST(FwdIntegrationTest, RemoteForwardEchoRoundTrip)
{
    if (std::getenv("SSH_TEST_FWD_REMOTE") == nullptr) {
        GTEST_SKIP() << "set SSH_TEST_FWD_REMOTE=1 (only when the SSH server is "
                        "localhost-reachable, e.g. WSL sshd on this machine) to enable "
                        "the remote-forward integration test";
    }
    LivePasswordSession live;
    if (!live.establish()) {
        GTEST_SKIP() << "set SSH_TEST_HOST/PORT/USER/PASSWORD to enable live forwarding";
    }

    // Echo target on this machine: whatever the accepted channel delivers
    // comes straight back.
    LoopbackPair echo;
    ASSERT_TRUE(echo.open());
    std::thread echoThread([&] {
        char buffer[65536];
        for (;;) {
            const int n = ::recv(echo.server, buffer, sizeof(buffer), 0);
            if (n <= 0) {
                break;
            }
            size_t done = 0;
            while (done < static_cast<size_t>(n)) {
                const int sent = ::send(echo.server, buffer + done, n - done, 0);
                if (sent <= 0) {
                    break;
                }
                done += static_cast<size_t>(sent);
            }
        }
    });

    std::mutex mutex;
    std::condition_variable cv;
    bool listeningFired = false;
    RemoteListenResult listenResult;
    bool stoppedFired = false;
    RemoteListenerCallbacks callbacks;
    callbacks.onListening = [&](const RemoteListenResult& result) {
        {
            std::lock_guard<std::mutex> lock(mutex);
            listeningFired = true;
            listenResult = result;
        }
        cv.notify_all();
    };
    callbacks.onStopped = [&] {
        {
            std::lock_guard<std::mutex> lock(mutex);
            stoppedFired = true;
        }
        cv.notify_all();
    };
    auto stats = std::make_shared<TunnelStats>();
    RemoteListener listener(live.session(), "127.0.0.1", echo.port, stats,
                            std::move(callbacks));
    ASSERT_TRUE(listener.start("127.0.0.1", 0));
    {
        std::unique_lock<std::mutex> lock(mutex);
        ASSERT_TRUE(cv.wait_for(lock, 15s, [&] { return listeningFired; }));
        ASSERT_TRUE(listenResult.success) << listenResult.message;
        ASSERT_NE(listenResult.boundPort, 0);
    }

    // Dial the server-side port: server -> channel -> target echo -> back.
    // Reachable only when the server binds loopback on this same machine.
    const SOCKET probe = BlockingConnect(live.env().host, listenResult.boundPort);
    ASSERT_NE(probe, INVALID_SOCKET)
        << "is the SSH server localhost-reachable? (binds are loopback by default)";
    const std::string marker = "f04-remote-echo-marker";
    ASSERT_TRUE(sendAll(probe, marker));
    std::string reply;
    const auto deadline = std::chrono::steady_clock::now() + 15s;
    while (reply.size() < marker.size() && std::chrono::steady_clock::now() < deadline) {
        char buffer[1024];
        const int n = ::recv(probe, buffer, sizeof(buffer), 0);
        if (n > 0) {
            reply.append(buffer, static_cast<size_t>(n));
            continue;
        }
        if (n == 0) {
            break;
        }
        if (::WSAGetLastError() != WSAEWOULDBLOCK &&
            ::WSAGetLastError() != WSAETIMEDOUT) {
            break;
        }
    }
    ::closesocket(probe);
    EXPECT_EQ(reply, marker);

    EXPECT_TRUE(WaitForActiveZero(*stats, 10s));
    const auto snapshot = snapshotTunnelStats(*stats);
    EXPECT_GE(snapshot.totalConnections, 1u);
    EXPECT_GT(snapshot.bytesUp + snapshot.bytesDown, 0u);

    listener.stop();
    {
        std::unique_lock<std::mutex> lock(mutex);
        EXPECT_TRUE(cv.wait_for(lock, 10s, [&] { return stoppedFired; }));
    }
    ::shutdown(echo.server, SD_BOTH);
    echoThread.join();
    echo.close();
}
