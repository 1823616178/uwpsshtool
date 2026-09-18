// N04: host key fingerprints, randomart and the TOFU gate in SshSession.
//
// Pure-logic tests need no server. The golden vectors come from a fixed
// ed25519 public key blob generated once with ssh-keygen; the expected
// SHA256/MD5 fingerprints and the randomart block are the verbatim output of
// `ssh-keygen -l` / `ssh-keygen -lv` / `ssh-keygen -E md5 -l` for that blob.
// Integration tests follow the N03 pattern: they run only when
// SSH_TEST_HOST/PORT/USER/PASSWORD are set, otherwise GTEST_SKIP.
#include "io/SessionThread.h" // winsock2.h must precede windows.h/gtest.
#include "ssh/hostkey.h"
#include "ssh/session.h"

#include <gtest/gtest.h>

#include <cctype>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

#include <libssh2.h>

#include "sshd_testkit.h"

using namespace std::chrono_literals;
using sshclient::io::SessionThread;
using sshclient::ssh::HostKeyCallback;
using sshclient::ssh::HostKeyCheckResult;
using sshclient::ssh::HostKeyDecision;
using sshclient::ssh::HostKeyInfo;
using sshclient::ssh::SshSession;
using sshclient::ssh::SshSessionError;
using sshclient::ssh::SshSessionOptions;
using sshclient::ssh::SshSessionState;

namespace {

// base64 decode for clean test input (authorized_keys field / fingerprint
// payload); stops at '=', returns empty on any other invalid character.
std::vector<uint8_t> Base64Decode(const std::string& in)
{
    const auto valOf = [](char c) -> int {
        if (c >= 'A' && c <= 'Z') {
            return c - 'A';
        }
        if (c >= 'a' && c <= 'z') {
            return c - 'a' + 26;
        }
        if (c >= '0' && c <= '9') {
            return c - '0' + 52;
        }
        if (c == '+') {
            return 62;
        }
        if (c == '/') {
            return 63;
        }
        return -1;
    };
    std::vector<uint8_t> out;
    uint32_t acc = 0;
    int nbits = 0;
    for (const char c : in) {
        if (c == '=') {
            break;
        }
        if (std::isspace(static_cast<unsigned char>(c)) != 0) {
            continue;
        }
        const int v = valOf(c);
        if (v < 0) {
            return {};
        }
        acc = ((acc << 6) | static_cast<uint32_t>(v)) & 0xFFFFFFu;
        nbits += 6;
        if (nbits >= 8) {
            nbits -= 8;
            out.push_back(static_cast<uint8_t>((acc >> nbits) & 0xFF));
        }
    }
    return out;
}

std::vector<std::string> SplitLines(const std::string& text)
{
    std::vector<std::string> lines;
    size_t begin = 0;
    for (size_t i = 0; i < text.size(); ++i) {
        if (text[i] == '\n') {
            lines.push_back(text.substr(begin, i - begin));
            begin = i + 1;
        }
    }
    lines.push_back(text.substr(begin));
    return lines;
}

bool Visited(const StateRecorder& recorder, SshSessionState state)
{
    for (const SshSessionState visited : recorder.sequence()) {
        if (visited == state) {
            return true;
        }
    }
    return false;
}

void EnsureLibssh2Init()
{
    static std::once_flag once;
    static int rc = -1;
    std::call_once(once, [] { rc = ::libssh2_init(0); });
    ASSERT_EQ(rc, 0);
}

// Fixed ed25519 public key blob (base64 field of the .pub line) and the
// ssh-keygen output for it. Regenerate with:
//   ssh-keygen -t ed25519 -N '' -f hostkey && ssh-keygen -lvf hostkey.pub
constexpr const char* kGoldenBlobBase64 =
    "AAAAC3NzaC1lZDI1NTE5AAAAIO92Ql8bOPTHqGqvvGPnxJHvsyWijqQAOZCNlGOA8QBs";
constexpr const char* kGoldenSha256 = "SHA256:FAhAT5z2Wpc70K7jpu5oAQA8XY89/++C/pwtDsgsHF8";
constexpr const char* kGoldenMd5 = "MD5:92:bd:1f:9e:0c:97:be:d7:31:03:09:68:59:ec:5e:29";
// Verbatim `ssh-keygen -lvf` randomart block; explicit "\n" joins keep the
// trailing spaces immune to CRLF checkouts.
const std::string kGoldenArt = std::string()
    + "+--[ED25519 256]--+\n"
    + "|+.+o++ ..        |\n"
    + "|.o ++ =  .       |\n"
    + "|. ...o =..       |\n"
    + "|.     +.*        |\n"
    + "| .   + +SE       |\n"
    + "|  . o = * .      |\n"
    + "|   . o * o..     |\n"
    + "|  ..  =  .oo+    |\n"
    + "| ..o++....o=++   |\n"
    + "+----[SHA256]-----+";

} // namespace

// ================================================================== pure logic

TEST(HostKeyBase64Test, BoundaryLengths)
{
    const auto enc = [](std::initializer_list<uint8_t> bytes) {
        return sshclient::ssh::base64EncodeNoPadding(bytes.begin(), bytes.size());
    };
    // 0/1/2/3 bytes cover every mod-3 branch; expected values are standard
    // base64 with padding stripped.
    EXPECT_EQ(enc({}), "");
    EXPECT_EQ(enc({0x00}), "AA");
    EXPECT_EQ(enc({0xFF}), "/w");
    EXPECT_EQ(enc({0x00, 0x00}), "AAA");
    EXPECT_EQ(enc({0xFF, 0xFF}), "//8");
    EXPECT_EQ(enc({0x00, 0x00, 0x00}), "AAAA");
    EXPECT_EQ(enc({0xFF, 0xFF, 0xFF}), "////");
    EXPECT_EQ(enc({0xDE, 0xAD, 0xBE, 0xEF}), "3q2+7w");
    const auto encStr = [](const char* s) {
        return sshclient::ssh::base64EncodeNoPadding(
            reinterpret_cast<const uint8_t*>(s), std::strlen(s));
    };
    EXPECT_EQ(encStr("f"), "Zg");
    EXPECT_EQ(encStr("fo"), "Zm8");
    EXPECT_EQ(encStr("foo"), "Zm9v");
    EXPECT_EQ(encStr("foob"), "Zm9vYg");
    EXPECT_EQ(encStr("fooba"), "Zm9vYmE");
    EXPECT_EQ(encStr("foobar"), "Zm9vYmFy");
}

TEST(HostKeyFingerprintTest, Sha256GoldenVectorsAndFormat)
{
    const auto fp = [](const char* s) {
        return sshclient::ssh::fingerprintSha256(reinterpret_cast<const uint8_t*>(s),
                                                 std::strlen(s));
    };
    EXPECT_EQ(fp(""), "SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU");
    EXPECT_EQ(fp("abc"), "SHA256:ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0");

    const std::string f = fp("abc");
    EXPECT_EQ(f.rfind("SHA256:", 0), 0u);      // prefix
    ASSERT_EQ(f.size(), 7u + 43u);             // 32 bytes -> 43 base64 chars
    EXPECT_EQ(f.find('='), std::string::npos); // no padding
    for (size_t i = 7; i < f.size(); ++i) {
        const char c = f[i];
        EXPECT_TRUE(std::isalnum(static_cast<unsigned char>(c)) != 0 || c == '+' || c == '/')
            << "illegal base64 character: " << c;
    }
}

TEST(HostKeyFingerprintTest, Md5GoldenVector)
{
    const auto fp = [](const char* s) {
        return sshclient::ssh::fingerprintMd5(reinterpret_cast<const uint8_t*>(s),
                                              std::strlen(s));
    };
    EXPECT_EQ(fp(""), "MD5:d4:1d:8c:d9:8f:00:b2:04:e9:80:09:98:ec:f8:42:7e");
    EXPECT_EQ(fp("abc"), "MD5:90:01:50:98:3c:d2:4f:b0:d6:96:3f:7d:28:e1:7f:72");
}

// N04 acceptance: fingerprints of a fixed public key blob must equal the
// hardcoded ssh-keygen -l / ssh-keygen -E md5 -l output.
TEST(HostKeyFingerprintTest, GoldenKeyBlobMatchesSshKeygen)
{
    const std::vector<uint8_t> blob = Base64Decode(kGoldenBlobBase64);
    ASSERT_FALSE(blob.empty());
    EXPECT_EQ(sshclient::ssh::fingerprintSha256(blob.data(), blob.size()), kGoldenSha256);
    EXPECT_EQ(sshclient::ssh::fingerprintMd5(blob.data(), blob.size()), kGoldenMd5);
}

TEST(HostKeyRandomartTest, SameDigestSameArtDifferentDigestDifferentArt)
{
    std::vector<uint8_t> a(32);
    std::vector<uint8_t> b(32, 0xFF);
    for (size_t i = 0; i < a.size(); ++i) {
        a[i] = static_cast<uint8_t>(i);
    }
    const auto art = [](const std::vector<uint8_t>& d) {
        return sshclient::ssh::randomartFromDigest(d.data(), d.size(), "[ED25519 256]",
                                                   "SHA256");
    };
    EXPECT_EQ(art(a), art(a)); // deterministic
    EXPECT_NE(art(a), art(b)); // different digest, different picture
}

TEST(HostKeyRandomartTest, ShapeMarkersBordersAndCharset)
{
    std::vector<uint8_t> digest(32);
    for (size_t i = 0; i < digest.size(); ++i) {
        digest[i] = static_cast<uint8_t>(i * 7 + 3);
    }
    const std::string art = sshclient::ssh::randomartFromDigest(
        digest.data(), digest.size(), "[ED25519 256]", "SHA256");
    const std::vector<std::string> lines = SplitLines(art);

    // 1 top border + 9 field rows + 1 bottom border; every line 19 columns.
    ASSERT_EQ(lines.size(), 11u);
    EXPECT_EQ(lines[0], "+--[ED25519 256]--+");
    EXPECT_EQ(lines[10], "+----[SHA256]-----+");
    const std::string charset = " .o+=*BOX@%&#/^SE";
    int countS = 0;
    int countE = 0;
    for (size_t row = 1; row <= 9; ++row) {
        ASSERT_EQ(lines[row].size(), 19u);
        EXPECT_EQ(lines[row].front(), '|');
        EXPECT_EQ(lines[row].back(), '|');
        for (size_t col = 1; col <= 17; ++col) {
            const char c = lines[row][col];
            EXPECT_NE(charset.find(c), std::string::npos) << "character outside the field: " << c;
            countS += c == 'S' ? 1 : 0;
            countE += c == 'E' ? 1 : 0;
        }
    }
    // Exactly one end marker E; the start marker S sits at field (8,4) ->
    // lines[5][9], unless the worm ended on the start cell and E overwrote it.
    EXPECT_EQ(countE, 1);
    if (countS == 1) {
        EXPECT_EQ(lines[5][9], 'S');
    } else {
        EXPECT_EQ(lines[5][9], 'E');
    }
}

// N04 acceptance: randomart of the fixed blob's digest must equal the
// hardcoded ssh-keygen -lv block, character for character.
TEST(HostKeyRandomartTest, GoldenArtMatchesSshKeygenVisual)
{
    // randomart draws the SHA256 digest; recover it from the fingerprint's
    // base64 payload instead of re-implementing SHA256 in the test.
    const std::string payload = std::string(kGoldenSha256).substr(7);
    const std::vector<uint8_t> digest = Base64Decode(payload);
    ASSERT_EQ(digest.size(), 32u);
    EXPECT_EQ(sshclient::ssh::randomartFromDigest(digest.data(), digest.size(),
                                                  "[ED25519 256]", "SHA256"),
              kGoldenArt);
}

TEST(HostKeyCheckTest, PureFingerprintThreeStates)
{
    using sshclient::ssh::checkFingerprintSha256;
    EXPECT_EQ(checkFingerprintSha256("SHA256:abc", "SHA256:abc"), HostKeyCheckResult::Ok);
    EXPECT_EQ(checkFingerprintSha256("SHA256:abc", ""), HostKeyCheckResult::Unknown);
    EXPECT_EQ(checkFingerprintSha256("SHA256:abc", "SHA256:abd"), HostKeyCheckResult::Mismatch);
    EXPECT_EQ(checkFingerprintSha256("", "SHA256:abc"), HostKeyCheckResult::Mismatch);
}

TEST(HostKeyCheckTest, SessionWithoutHandshakeFailsClosed)
{
    EnsureLibssh2Init();
    LIBSSH2_SESSION* session = ::libssh2_session_init_ex(nullptr, nullptr, nullptr, nullptr);
    ASSERT_NE(session, nullptr);
    // No handshake: no host key, fail-closed -> Mismatch.
    EXPECT_EQ(sshclient::ssh::extractHostKey(session), std::nullopt);
    EXPECT_EQ(sshclient::ssh::checkHostKey(session, "SHA256:anything"),
              HostKeyCheckResult::Mismatch);
    ::libssh2_session_free(session);
}

// ==================================================== integration (live server)

TEST(HostKeyIntegrationTest, DefaultPolicyAcceptsAndReportsKey)
{
    SshIntegrationEnvironment environment;
    if (!LoadSshIntegrationEnvironment(environment)) {
        GTEST_SKIP() << "set SSH_TEST_HOST/PORT/USER/PASSWORD to enable live SSH handshake";
    }

    SessionThread thread;
    ASSERT_TRUE(thread.start());
    StateRecorder recorder;
    std::optional<HostKeyInfo> info;
    {
        SshSession session(thread, {}, std::ref(recorder));
        ASSERT_TRUE(session.connect(environment.host, environment.port, environment.user));
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Authenticating, 20s));
        EXPECT_EQ(SshSessionError::None, session.lastError());
        info = session.hostKeyInfo();
        session.close();
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Closed, 5s));
        thread.stop();
    }

    ASSERT_TRUE(info.has_value());
    EXPECT_FALSE(info->keyType.empty());
    EXPECT_FALSE(info->rawKey.empty());
    EXPECT_EQ(info->fingerprintSha256.rfind("SHA256:", 0), 0u);
    EXPECT_EQ(SplitLines(info->randomart).size(), 11u);

    // Optional pinning: SSH_TEST_HOSTKEY_SHA256 pins the expected fingerprint.
    if (const char* expected = std::getenv("SSH_TEST_HOSTKEY_SHA256")) {
        if (expected[0] != '\0') {
            EXPECT_EQ(info->fingerprintSha256, expected);
        }
    }
    std::fprintf(stderr, "[test] host key: %s %s\n", info->keyType.c_str(),
                 info->fingerprintSha256.c_str());
}

TEST(HostKeyIntegrationTest, CallbackAcceptsMatchingFingerprint)
{
    SshIntegrationEnvironment environment;
    if (!LoadSshIntegrationEnvironment(environment)) {
        GTEST_SKIP() << "set SSH_TEST_HOST/PORT/USER/PASSWORD to enable live SSH handshake";
    }

    // Learn the real fingerprint with a first (default-accept) connection.
    std::string learned;
    {
        SessionThread thread;
        ASSERT_TRUE(thread.start());
        StateRecorder recorder;
        {
            SshSession session(thread, {}, std::ref(recorder));
            ASSERT_TRUE(session.connect(environment.host, environment.port, environment.user));
            ASSERT_TRUE(recorder.waitFor(SshSessionState::Authenticating, 20s));
            const std::optional<HostKeyInfo> info = session.hostKeyInfo();
            ASSERT_TRUE(info.has_value());
            learned = info->fingerprintSha256;
            session.close();
            ASSERT_TRUE(recorder.waitFor(SshSessionState::Closed, 5s));
            thread.stop();
        }
    }

    std::mutex callbackMutex;
    std::optional<HostKeyCheckResult> callbackResult;
    SshSessionOptions options;
    options.hostKeyCallback = [&](const HostKeyInfo& info) {
        std::lock_guard<std::mutex> lock(callbackMutex);
        callbackResult =
            sshclient::ssh::checkFingerprintSha256(info.fingerprintSha256, learned);
        return *callbackResult == HostKeyCheckResult::Ok ? HostKeyDecision::Accept
                                                         : HostKeyDecision::Reject;
    };

    SessionThread thread;
    ASSERT_TRUE(thread.start());
    StateRecorder recorder;
    {
        SshSession session(thread, options, std::ref(recorder));
        ASSERT_TRUE(session.connect(environment.host, environment.port, environment.user));
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Authenticating, 20s));
        EXPECT_EQ(SshSessionError::None, session.lastError());
        session.close();
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Closed, 5s));
        thread.stop();
    }

    {
        std::lock_guard<std::mutex> lock(callbackMutex);
        ASSERT_TRUE(callbackResult.has_value());
        EXPECT_EQ(*callbackResult, HostKeyCheckResult::Ok);
    }
    EXPECT_EQ((std::vector<SshSessionState>{SshSessionState::Connecting,
                                             SshSessionState::Handshaking,
                                             SshSessionState::Authenticating,
                                             SshSessionState::Closing,
                                             SshSessionState::Closed}),
              recorder.sequence());
}

// N04 acceptance: a mismatched fingerprint must never reach Authenticating.
TEST(HostKeyIntegrationTest, ForgedFingerprintRejectedBeforeAuthenticating)
{
    SshIntegrationEnvironment environment;
    if (!LoadSshIntegrationEnvironment(environment)) {
        GTEST_SKIP() << "set SSH_TEST_HOST/PORT/USER/PASSWORD to enable live SSH handshake";
    }

    // A fingerprint that cannot match the real host key.
    const std::string forged = sshclient::ssh::fingerprintSha256(
        reinterpret_cast<const uint8_t*>("forged-host-key"), sizeof("forged-host-key") - 1);

    std::mutex callbackMutex;
    std::optional<HostKeyCheckResult> callbackResult;
    SshSessionOptions options;
    options.hostKeyCallback = [&](const HostKeyInfo& info) {
        std::lock_guard<std::mutex> lock(callbackMutex);
        callbackResult =
            sshclient::ssh::checkFingerprintSha256(info.fingerprintSha256, forged);
        return *callbackResult == HostKeyCheckResult::Ok ? HostKeyDecision::Accept
                                                         : HostKeyDecision::Reject;
    };

    SessionThread thread;
    ASSERT_TRUE(thread.start());
    StateRecorder recorder;
    std::optional<HostKeyInfo> info;
    SshSessionError error = SshSessionError::None;
    {
        SshSession session(thread, options, std::ref(recorder));
        ASSERT_TRUE(session.connect(environment.host, environment.port, environment.user));
        // Rejection ends in Closed via a graceful Closing -> Closed shutdown.
        ASSERT_TRUE(recorder.waitFor(SshSessionState::Closed, 10s));
        error = session.lastError();
        info = session.hostKeyInfo(); // kept for the UI's actual-vs-expected view
        thread.stop();
    }

    EXPECT_EQ(error, SshSessionError::HostKeyMismatch)
        << "unexpected error: " << sshclient::ssh::toString(error);
    EXPECT_FALSE(Visited(recorder, SshSessionState::Authenticating));
    {
        std::lock_guard<std::mutex> lock(callbackMutex);
        ASSERT_TRUE(callbackResult.has_value());
        EXPECT_EQ(*callbackResult, HostKeyCheckResult::Mismatch);
    }
    ASSERT_TRUE(info.has_value());
    EXPECT_NE(info->fingerprintSha256, forged);
    EXPECT_EQ((std::vector<SshSessionState>{SshSessionState::Connecting,
                                             SshSessionState::Handshaking,
                                             SshSessionState::Closing,
                                             SshSessionState::Closed}),
              recorder.sequence());
}
