// N06: SSH channel and PTY — openShell/exec/execWithPty, the non-blocking
// read/write pumps, resize, EOF and exit-status.
//
// Offline unit tests cover the state-machine legality table, admission
// rejection outside Established, the PendingWriteQueue EAGAIN/partial-write
// semantics with a scripted writer, and the read-result classification.
// Integration tests follow the N05 pattern: SSH_TEST_HOST/PORT/USER/PASSWORD
// plus SSH_TEST_FIXTURE_KEYS=1 (server authorizes native/tests/fixtures/keys).
// POSIX-specific cases (stty size, cat + sendEof) additionally need
// SSH_TEST_POSIX=1.
#include "io/SessionThread.h" // winsock2.h must precede windows.h/gtest.
#include "ssh/channel.h"
#include "ssh/session.h"

#include <gtest/gtest.h>

#include <algorithm>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstdlib>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

#include <libssh2.h>

#include "sshd_testkit.h"

using namespace std::chrono_literals;
using sshclient::io::SessionThread;
using sshclient::ssh::ChannelCloseInfo;
using sshclient::ssh::ChannelCloseReason;
using sshclient::ssh::ChannelOpenResult;
using sshclient::ssh::ChannelReadKind;
using sshclient::ssh::ChannelState;
using sshclient::ssh::ChannelStream;
using sshclient::ssh::ExecResult;
using sshclient::ssh::PendingWriteQueue;
using sshclient::ssh::PtySpec;
using sshclient::ssh::SshChannel;
using sshclient::ssh::SshChannelCallbacks;
using sshclient::ssh::SshSession;
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

// Records every callback of one channel; all callbacks fire on the session's
// loop thread, the test thread waits through the CV.
class ChannelRecorder {
public:
    SshChannelCallbacks callbacks()
    {
        SshChannelCallbacks cb;
        cb.onOpen = [this](const ChannelOpenResult& result) {
            {
                std::lock_guard<std::mutex> lock(mutex_);
                openFired_ = true;
                openResult_ = result;
            }
            changed_.notify_all();
        };
        cb.onData = [this](const std::string& data, ChannelStream stream) {
            {
                std::lock_guard<std::mutex> lock(mutex_);
                if (stream == ChannelStream::Stdout) {
                    stdoutData_ += data;
                } else {
                    stderrData_ += data;
                }
            }
            changed_.notify_all();
        };
        cb.onClose = [this](const ChannelCloseInfo& info) {
            {
                std::lock_guard<std::mutex> lock(mutex_);
                closeFired_ = true;
                closeInfo_ = info;
            }
            changed_.notify_all();
        };
        return cb;
    }

    bool waitForOpen(std::chrono::milliseconds timeout)
    {
        std::unique_lock<std::mutex> lock(mutex_);
        return changed_.wait_for(lock, timeout, [&] { return openFired_; });
    }

    bool waitForClose(std::chrono::milliseconds timeout)
    {
        std::unique_lock<std::mutex> lock(mutex_);
        return changed_.wait_for(lock, timeout, [&] { return closeFired_; });
    }

    bool waitForStdout(const std::string& needle, std::chrono::milliseconds timeout)
    {
        std::unique_lock<std::mutex> lock(mutex_);
        return changed_.wait_for(lock, timeout,
                                 [&] { return stdoutData_.find(needle) != std::string::npos; });
    }

    bool openSucceeded() const
    {
        std::lock_guard<std::mutex> lock(mutex_);
        return openFired_ && openResult_.success;
    }

    ChannelCloseInfo closeInfo() const
    {
        std::lock_guard<std::mutex> lock(mutex_);
        return closeInfo_;
    }

    std::string stdoutData() const
    {
        std::lock_guard<std::mutex> lock(mutex_);
        return stdoutData_;
    }

    std::string stderrData() const
    {
        std::lock_guard<std::mutex> lock(mutex_);
        return stderrData_;
    }

private:
    mutable std::mutex mutex_;
    std::condition_variable changed_;
    bool openFired_ = false;
    bool closeFired_ = false;
    ChannelOpenResult openResult_;
    ChannelCloseInfo closeInfo_;
    std::string stdoutData_;
    std::string stderrData_;
};

// Live-session fixture: connect + authenticate with the unencrypted ed25519
// fixture key, reaching Established. establish() returns false when the
// environment is not configured (caller GTEST_SKIPs).
class LiveSession {
public:
    ~LiveSession() { shutdown(); }

    bool establish()
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
        session_ = std::make_unique<SshSession>(thread_, sshclient::ssh::SshSessionOptions{},
                                                std::ref(recorder_));
        if (!session_->connect(env_.host, env_.port, env_.user) ||
            !recorder_.waitFor(SshSessionState::Authenticating, 20s)) {
            return false;
        }
        std::mutex mutex;
        std::condition_variable cv;
        std::optional<bool> authOk;
        if (!session_->authenticatePublicKey(privateKey, publicKey, passphrase,
                                             [&](const sshclient::ssh::AuthResult& result) {
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
        return authOk.value() &&
               recorder_.waitFor(SshSessionState::Established, 5s);
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

    SshSession& session() { return *session_; }

private:
    SshIntegrationEnvironment env_;
    SessionThread thread_;
    StateRecorder recorder_;
    std::unique_ptr<SshSession> session_;
};

bool PosixGateOpen()
{
    const char* gate = std::getenv("SSH_TEST_POSIX");
    return gate != nullptr && gate[0] != '\0';
}

} // namespace

// ================================================================== pure logic

TEST(ChannelTransitionTest, LegalityTable)
{
    using S = ChannelState;
    const S states[] = {S::Idle,     S::Opening, S::RequestingPty, S::Starting,
                        S::Open,     S::Closing, S::Closed};
    auto expected = [](S from, S to) {
        switch (from) {
        case S::Idle:
            return to == S::Opening;
        case S::Opening:
            return to == S::RequestingPty || to == S::Starting || to == S::Closing ||
                   to == S::Closed;
        case S::RequestingPty:
            return to == S::Starting || to == S::Closing || to == S::Closed;
        case S::Starting:
            return to == S::Open || to == S::Closing || to == S::Closed;
        case S::Open:
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
            EXPECT_EQ(SshChannel::isLegalTransition(from, to), expected(from, to))
                << "from=" << static_cast<int>(from) << " to=" << static_cast<int>(to);
        }
    }
}

TEST(ClassifyChannelReadTest, FourBranches)
{
    using sshclient::ssh::classifyChannelRead;
    EXPECT_EQ(classifyChannelRead(1), ChannelReadKind::Data);
    EXPECT_EQ(classifyChannelRead(32768), ChannelReadKind::Data);
    EXPECT_EQ(classifyChannelRead(0), ChannelReadKind::Eof);
    EXPECT_EQ(classifyChannelRead(LIBSSH2_ERROR_EAGAIN), ChannelReadKind::Stalled);
    EXPECT_EQ(classifyChannelRead(LIBSSH2_ERROR_CHANNEL_CLOSED), ChannelReadKind::Error);
    EXPECT_EQ(classifyChannelRead(-1), ChannelReadKind::Error);
}

TEST(PendingWriteQueueTest, PartialWritesReassembleExactly)
{
    PendingWriteQueue queue;
    queue.push("hello ");
    queue.push("world!");
    EXPECT_EQ(queue.chunkCount(), 2u);
    EXPECT_EQ(queue.remainingBytes(), 12u);

    // Scripted writer: at most 3 bytes per call -> partial-write path.
    std::string sent;
    size_t flushed = 0;
    EXPECT_TRUE(queue.flush(
        [&](const char* data, size_t len) -> ptrdiff_t {
            const size_t n = std::min<size_t>(len, 3);
            sent.append(data, n);
            return static_cast<ptrdiff_t>(n);
        },
        nullptr, &flushed));
    EXPECT_TRUE(queue.empty());
    EXPECT_EQ(queue.headOffset(), 0u);
    EXPECT_EQ(flushed, 12u);
    EXPECT_EQ(sent, "hello world!");
}

TEST(PendingWriteQueueTest, EagainStallKeepsRemainder)
{
    PendingWriteQueue queue;
    queue.push("abcdef");
    int calls = 0;
    bool stalled = false;
    size_t flushed = 0;
    EXPECT_TRUE(queue.flush(
        [&](const char*, size_t) -> ptrdiff_t {
            ++calls;
            if (calls == 1) {
                return 2; // partial send: "ab"
            }
            return LIBSSH2_ERROR_EAGAIN; // then stall
        },
        &stalled, &flushed));
    EXPECT_TRUE(stalled);
    EXPECT_EQ(flushed, 2u);
    EXPECT_EQ(queue.chunkCount(), 1u);
    EXPECT_EQ(queue.headOffset(), 2u);
    EXPECT_EQ(queue.remainingBytes(), 4u);

    // The next flush (socket writable again) sends exactly the remainder.
    std::string sent;
    stalled = false;
    EXPECT_TRUE(queue.flush(
        [&](const char* data, size_t len) -> ptrdiff_t {
            sent.append(data, len);
            return static_cast<ptrdiff_t>(len);
        },
        &stalled, nullptr));
    EXPECT_FALSE(stalled);
    EXPECT_EQ(sent, "cdef");
    EXPECT_TRUE(queue.empty());
    EXPECT_EQ(queue.remainingBytes(), 0u);
}

TEST(PendingWriteQueueTest, ImmediateStallReportsNoProgress)
{
    PendingWriteQueue queue;
    queue.push("x");
    bool stalled = false;
    EXPECT_FALSE(queue.flush([](const char*, size_t) -> ptrdiff_t { return LIBSSH2_ERROR_EAGAIN; },
                             &stalled));
    EXPECT_TRUE(stalled);
    EXPECT_EQ(queue.remainingBytes(), 1u);
}

TEST(PendingWriteQueueTest, ZeroProgressGuardStopsLoop)
{
    PendingWriteQueue queue;
    queue.push("x");
    bool stalled = true;
    EXPECT_FALSE(queue.flush([](const char*, size_t) -> ptrdiff_t { return 0; }, &stalled));
    EXPECT_FALSE(stalled); // a zero send is not an EAGAIN stall
    EXPECT_EQ(queue.remainingBytes(), 1u);
}

TEST(PendingWriteQueueTest, ClearResetsAccountingBasis)
{
    PendingWriteQueue queue;
    queue.push("abcd");
    // Send 1 byte, then stall: the head offset persists across the stall.
    int calls = 0;
    queue.flush(
        [&](const char*, size_t) -> ptrdiff_t {
            return ++calls == 1 ? 1 : LIBSSH2_ERROR_EAGAIN;
        },
        nullptr, nullptr);
    EXPECT_EQ(queue.headOffset(), 1u);
    EXPECT_EQ(queue.remainingBytes(), 3u);
    queue.clear();
    EXPECT_TRUE(queue.empty());
    EXPECT_EQ(queue.chunkCount(), 0u);
    EXPECT_EQ(queue.headOffset(), 0u);
    EXPECT_EQ(queue.remainingBytes(), 0u);
}

// ================================================================== admission (offline)

TEST(ChannelAdmissionTest, RejectedWhenNotEstablished)
{
    SessionThread thread;
    ASSERT_TRUE(thread.start());
    {
        SshSession session(thread, {}, nullptr); // Idle: never connected
        SshChannel channel(session, {});
        EXPECT_EQ(channel.state(), ChannelState::Idle);
        EXPECT_FALSE(channel.openShell(PtySpec{}));
        EXPECT_FALSE(channel.exec("ls"));
        EXPECT_FALSE(channel.execWithPty(PtySpec{}, "ls"));
        // Data-plane calls are not admitted before an open either.
        EXPECT_FALSE(channel.write("abc", 3));
        EXPECT_FALSE(channel.resize(80, 24));
        EXPECT_FALSE(channel.setenv("N06", "1"));
        EXPECT_FALSE(channel.sendEof());
        channel.close(); // no-op on a never-opened channel
        EXPECT_EQ(channel.state(), ChannelState::Idle);
        thread.stop();
    }
}

TEST(ChannelAdmissionTest, InvalidArgsRejectedBeforeStateCheck)
{
    SessionThread thread;
    ASSERT_TRUE(thread.start());
    {
        SshSession session(thread, {}, nullptr);
        SshChannel channel(session, {});
        PtySpec emptyTerm;
        emptyTerm.termType = "";
        EXPECT_FALSE(channel.openShell(emptyTerm));
        PtySpec zeroSize;
        zeroSize.cols = 0;
        EXPECT_FALSE(channel.openShell(zeroSize));
        EXPECT_FALSE(channel.exec(""));
        EXPECT_FALSE(channel.execWithPty(PtySpec{}, ""));
        EXPECT_EQ(channel.state(), ChannelState::Idle);
        thread.stop();
    }
}

// ==================================================== integration (live server)

TEST(ChannelIntegrationTest, ExecReportsExitStatus)
{
    LiveSession live;
    if (!live.establish()) {
        GTEST_SKIP() << "set SSH_TEST_* plus SSH_TEST_FIXTURE_KEYS=1 to enable live channels";
    }
    ChannelRecorder recorder;
    {
        SshChannel channel(live.session(), recorder.callbacks());
        ASSERT_TRUE(channel.exec("exit 42"));
        ASSERT_TRUE(recorder.waitForClose(15s));
    }
    EXPECT_TRUE(recorder.openSucceeded());
    const ChannelCloseInfo info = recorder.closeInfo();
    EXPECT_EQ(info.reason, ChannelCloseReason::ExitStatus) << info.message;
    EXPECT_EQ(info.exitStatus, 42);
}

TEST(ChannelIntegrationTest, ExecCommandBlockingRoundTrip)
{
    LiveSession live;
    if (!live.establish()) {
        GTEST_SKIP() << "set SSH_TEST_* plus SSH_TEST_FIXTURE_KEYS=1 to enable live channels";
    }
    const ExecResult result = sshclient::ssh::execCommand(live.session(), "echo n06-marker");
    EXPECT_TRUE(result.ok) << result.message;
    EXPECT_EQ(result.exitStatus, 0);
    EXPECT_NE(result.stdoutData.find("n06-marker"), std::string::npos)
        << "stdout=" << result.stdoutData;
}

TEST(ChannelIntegrationTest, ExecSeparatesStdoutAndStderr)
{
    LiveSession live;
    if (!live.establish()) {
        GTEST_SKIP() << "set SSH_TEST_* plus SSH_TEST_FIXTURE_KEYS=1 to enable live channels";
    }
    // `&` separates commands in cmd.exe (Windows sshd default shell) and
    // backgrounds the first echo in POSIX sh — both streams carry their
    // marker either way.
    const ExecResult result =
        sshclient::ssh::execCommand(live.session(), "echo n06-out & echo n06-err 1>&2");
    EXPECT_TRUE(result.ok) << result.message;
    EXPECT_NE(result.stdoutData.find("n06-out"), std::string::npos)
        << "stdout=" << result.stdoutData;
    EXPECT_NE(result.stderrData.find("n06-err"), std::string::npos)
        << "stderr=" << result.stderrData;
}

TEST(ChannelIntegrationTest, PtySizeMatchesRequest)
{
    if (!PosixGateOpen()) {
        GTEST_SKIP() << "set SSH_TEST_POSIX=1 on a POSIX server (stty) to enable PTY size checks";
    }
    LiveSession live;
    if (!live.establish()) {
        GTEST_SKIP() << "set SSH_TEST_* plus SSH_TEST_FIXTURE_KEYS=1 to enable live channels";
    }
    PtySpec pty;
    pty.cols = 80;
    pty.rows = 24;
    ChannelRecorder recorder;
    {
        SshChannel channel(live.session(), recorder.callbacks());
        ASSERT_TRUE(channel.execWithPty(pty, "stty size"));
        ASSERT_TRUE(recorder.waitForClose(15s));
    }
    EXPECT_TRUE(recorder.openSucceeded());
    // stty size prints "rows cols"; PTY output is CRLF-terminated.
    EXPECT_NE(recorder.stdoutData().find("24 80"), std::string::npos)
        << "stdout=" << recorder.stdoutData();
}

// N06 acceptance: after request_pty_size the reported size changes too.
TEST(ChannelIntegrationTest, PtyResizeChangesReportedSize)
{
    if (!PosixGateOpen()) {
        GTEST_SKIP() << "set SSH_TEST_POSIX=1 on a POSIX server (stty) to enable PTY size checks";
    }
    LiveSession live;
    if (!live.establish()) {
        GTEST_SKIP() << "set SSH_TEST_* plus SSH_TEST_FIXTURE_KEYS=1 to enable live channels";
    }
    PtySpec pty;
    pty.cols = 80;
    pty.rows = 24;
    ChannelRecorder recorder;
    {
        SshChannel channel(live.session(), recorder.callbacks());
        ASSERT_TRUE(channel.openShell(pty));
        ASSERT_TRUE(recorder.waitForOpen(15s));
        ASSERT_TRUE(recorder.openSucceeded());

        ASSERT_TRUE(channel.write("stty size\n"));
        ASSERT_TRUE(recorder.waitForStdout("24 80", 10s))
            << "stdout=" << recorder.stdoutData();

        ASSERT_TRUE(channel.resize(100, 40));
        ASSERT_TRUE(channel.write("stty size\n"));
        ASSERT_TRUE(recorder.waitForStdout("40 100", 10s))
            << "stdout=" << recorder.stdoutData();

        channel.close();
        ASSERT_TRUE(recorder.waitForClose(10s));
    }
}

TEST(ChannelIntegrationTest, SendEofLetsPeerFinish)
{
    if (!PosixGateOpen()) {
        GTEST_SKIP() << "set SSH_TEST_POSIX=1 on a POSIX server (cat) to enable the EOF check";
    }
    LiveSession live;
    if (!live.establish()) {
        GTEST_SKIP() << "set SSH_TEST_* plus SSH_TEST_FIXTURE_KEYS=1 to enable live channels";
    }
    ChannelRecorder recorder;
    {
        SshChannel channel(live.session(), recorder.callbacks());
        ASSERT_TRUE(channel.exec("cat"));
        ASSERT_TRUE(recorder.waitForOpen(15s));
        ASSERT_TRUE(recorder.openSucceeded());
        ASSERT_TRUE(channel.write("n06-eof-check\n"));
        ASSERT_TRUE(channel.sendEof()); // cat sees stdin EOF and exits
        ASSERT_TRUE(recorder.waitForClose(15s));
    }
    EXPECT_NE(recorder.stdoutData().find("n06-eof-check"), std::string::npos)
        << "stdout=" << recorder.stdoutData();
    const ChannelCloseInfo info = recorder.closeInfo();
    EXPECT_EQ(info.reason, ChannelCloseReason::ExitStatus) << info.message;
    EXPECT_EQ(info.exitStatus, 0);
}

// Windows sshd PTY probe (ConPTY), opt-in via SSH_TEST_CONPTY=1: requests an
// uncommon size and lets PowerShell report the console window it actually got.
TEST(ChannelIntegrationTest, PtyProbeWindowsConpty)
{
    const char* gate = std::getenv("SSH_TEST_CONPTY");
    if (gate == nullptr || gate[0] == '\0') {
        GTEST_SKIP() << "set SSH_TEST_CONPTY=1 against a Windows sshd to probe ConPTY sizes";
    }
    LiveSession live;
    if (!live.establish()) {
        GTEST_SKIP() << "set SSH_TEST_* plus SSH_TEST_FIXTURE_KEYS=1 to enable live channels";
    }
    PtySpec pty;
    pty.cols = 113;
    pty.rows = 37;
    ChannelRecorder recorder;
    {
        SshChannel channel(live.session(), recorder.callbacks());
        ASSERT_TRUE(channel.execWithPty(
            pty, "powershell -NoProfile -Command \"$s=$Host.UI.RawUI.WindowSize; "
                 "Write-Output ($s.Width.ToString() + 'x' + $s.Height.ToString())\""));
        ASSERT_TRUE(recorder.waitForClose(30s));
    }
    // Diagnostic probe: log whatever came back, assert only the transport.
    const ChannelCloseInfo info = recorder.closeInfo();
    std::fprintf(stderr, "[test] conpty size probe: open=%d reason=%s exit=%d stdout=%s stderr=%s\n",
                 recorder.openSucceeded() ? 1 : 0,
                 sshclient::ssh::toString(info.reason), info.exitStatus,
                 recorder.stdoutData().c_str(), recorder.stderrData().c_str());
    EXPECT_TRUE(recorder.openSucceeded());
    EXPECT_NE(info.reason, ChannelCloseReason::Error) << info.message;
}

// Windows resize probe (ConPTY), opt-in via SSH_TEST_CONPTY=1: an interactive
// shell is resized mid-flight and PowerShell reports the new window size.
TEST(ChannelIntegrationTest, PtyResizeProbeWindowsConpty)
{
    const char* gate = std::getenv("SSH_TEST_CONPTY");
    if (gate == nullptr || gate[0] == '\0') {
        GTEST_SKIP() << "set SSH_TEST_CONPTY=1 against a Windows sshd to probe ConPTY sizes";
    }
    LiveSession live;
    if (!live.establish()) {
        GTEST_SKIP() << "set SSH_TEST_* plus SSH_TEST_FIXTURE_KEYS=1 to enable live channels";
    }
    const char* probe =
        "powershell -NoProfile -Command \"$s=$Host.UI.RawUI.WindowSize; "
        "Write-Output ($s.Width.ToString() + 'x' + $s.Height.ToString())\"\r";
    PtySpec pty;
    pty.cols = 80;
    pty.rows = 24;
    ChannelRecorder recorder;
    {
        SshChannel channel(live.session(), recorder.callbacks());
        ASSERT_TRUE(channel.openShell(pty));
        ASSERT_TRUE(recorder.waitForOpen(15s));
        ASSERT_TRUE(recorder.openSucceeded());
        ASSERT_TRUE(channel.write(probe));
        ASSERT_TRUE(recorder.waitForStdout("80x24", 20s))
            << "stdout=" << recorder.stdoutData();

        ASSERT_TRUE(channel.resize(113, 37));
        ASSERT_TRUE(channel.write(probe));
        ASSERT_TRUE(recorder.waitForStdout("113x37", 20s))
            << "stdout=" << recorder.stdoutData();

        ASSERT_TRUE(channel.write("exit\r"));
        ASSERT_TRUE(recorder.waitForClose(15s));
    }
    const ChannelCloseInfo info = recorder.closeInfo();
    EXPECT_NE(info.reason, ChannelCloseReason::Error) << info.message;
}
