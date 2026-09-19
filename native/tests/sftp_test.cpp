// F01: SFTP native layer — sftp/sftp_session.{h,cpp} + sftp/transfer.{h,cpp}.
//
// Offline unit tests (no server):
//   - FX status -> unified code mapping (every LIBSSH2_FX_* value);
//   - entry type from permission bits (all S_IFMT kinds + missing flag);
//   - remote path join edge cases;
//   - transfer pump over fault-injecting fakes: chunking at 32 KiB, resume
//     offsets, cancel-at-N, short reads/writes, mid-transfer errors, empty;
//   - admission guards on an Idle session (no IO thread needed).
// Integration tests (SSH_TEST_HOST/PORT/USER/PASSWORD, else GTEST_SKIP):
//   - metadata roundtrip: mkdir/stat/rename/listDir/removeFile/removeDir;
//   - upload then download a 50 MB file, SHA256 identical;
//   - interrupted upload (cancel at 8 MiB) then resume, SHA256 identical;
//   - POSIX-only (SSH_TEST_POSIX=1): symlink readlink + chmod roundtrip.
#include "io/SessionThread.h" // winsock2.h must precede windows.h/gtest.
#include "sftp/sftp_session.h"
#include "sftp/transfer.h"
#include "ssh/channel.h"
#include "ssh/error_codes.h"
#include "ssh/session.h"

#include <gtest/gtest.h>

#include <libssh2_sftp.h>

#include <openssl/sha.h>

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <random>
#include <string>
#include <vector>

#include "sshd_testkit.h"

using namespace std::chrono_literals;
using sshclient::io::SessionThread;
using sshclient::sftp::IBlockReader;
using sshclient::sftp::IBlockWriter;
using sshclient::sftp::joinRemotePath;
using sshclient::sftp::SftpAttrs;
using sshclient::sftp::SftpCallOptions;
using sshclient::sftp::SftpEntry;
using sshclient::sftp::SftpEntryType;
using sshclient::sftp::SftpResult;
using sshclient::sftp::SftpSession;
using sshclient::sftp::TransferHooks;
using sshclient::sftp::TransferProgress;
using sshclient::sftp::TransferResult;
using sshclient::ssh::SshSession;
using sshclient::ssh::SshSessionState;

namespace codes = sshclient::ssh;

// ================================================================== FX mapping

TEST(SftpStatusMapTest, EveryKnownStatusMapsToItsBucket)
{
    using sshclient::sftp::sftpStatusToCode;
    EXPECT_EQ(sftpStatusToCode(LIBSSH2_FX_OK), codes::kSshErrorCodeNone);
    EXPECT_EQ(sftpStatusToCode(LIBSSH2_FX_NO_SUCH_FILE),
              codes::kSshErrorCodeSftpNoSuchFile);
    EXPECT_EQ(sftpStatusToCode(LIBSSH2_FX_NO_SUCH_PATH),
              codes::kSshErrorCodeSftpNoSuchFile);
    EXPECT_EQ(sftpStatusToCode(LIBSSH2_FX_INVALID_HANDLE),
              codes::kSshErrorCodeSftpNoSuchFile);
    EXPECT_EQ(sftpStatusToCode(LIBSSH2_FX_PERMISSION_DENIED),
              codes::kSshErrorCodeSftpPermissionDenied);
    EXPECT_EQ(sftpStatusToCode(LIBSSH2_FX_WRITE_PROTECT),
              codes::kSshErrorCodeSftpPermissionDenied);
    EXPECT_EQ(sftpStatusToCode(LIBSSH2_FX_FILE_ALREADY_EXISTS),
              codes::kSshErrorCodeSftpAlreadyExists);
    EXPECT_EQ(sftpStatusToCode(LIBSSH2_FX_DIR_NOT_EMPTY),
              codes::kSshErrorCodeSftpAlreadyExists);
    // Generic bucket (EOF included: a bare FX_EOF outside a read loop is an
    // unexpected truncation, never a success signal here).
    const unsigned long generic[] = {
        LIBSSH2_FX_EOF,          LIBSSH2_FX_FAILURE,      LIBSSH2_FX_BAD_MESSAGE,
        LIBSSH2_FX_NO_CONNECTION, LIBSSH2_FX_CONNECTION_LOST,
        LIBSSH2_FX_OP_UNSUPPORTED, LIBSSH2_FX_INVALID_FILENAME,
        LIBSSH2_FX_LINK_LOOP,    LIBSSH2_FX_NO_SPACE_ON_FILESYSTEM,
        LIBSSH2_FX_QUOTA_EXCEEDED, LIBSSH2_FX_NO_MEDIA,
        LIBSSH2_FX_UNKNOWN_PRINCIPAL, LIBSSH2_FX_LOCK_CONFLICT,
        LIBSSH2_FX_NOT_A_DIRECTORY,
    };
    for (unsigned long status : generic) {
        EXPECT_EQ(sftpStatusToCode(status), codes::kSshErrorCodeSftpTransferFailed)
            << "FX status " << status;
    }
    EXPECT_EQ(sftpStatusToCode(9999), codes::kSshErrorCodeUnknown);
}

TEST(SftpStatusMapTest, NewConstantsMatchCSharpTable)
{
    // Anchors the 6xx values against SshErrorCode.cs (the ps1 contract check
    // covers names; this pins the numbers here too).
    EXPECT_EQ(codes::kSshErrorCodeSftpInitFailed, 601);
    EXPECT_EQ(codes::kSshErrorCodeSftpNoSuchFile, 602);
    EXPECT_EQ(codes::kSshErrorCodeSftpPermissionDenied, 603);
    EXPECT_EQ(codes::kSshErrorCodeSftpAlreadyExists, 604);
    EXPECT_EQ(codes::kSshErrorCodeSftpTransferFailed, 605);
    EXPECT_EQ(codes::kSshErrorCodeSftpCancelled, 606);
}

// ================================================================== entry types + paths

TEST(SftpEntryTypeTest, AllFileKinds)
{
    using sshclient::sftp::entryTypeFromPermissions;
    EXPECT_EQ(entryTypeFromPermissions(LIBSSH2_SFTP_S_IFREG | 0644, true),
              SftpEntryType::File);
    EXPECT_EQ(entryTypeFromPermissions(LIBSSH2_SFTP_S_IFDIR | 0755, true),
              SftpEntryType::Directory);
    EXPECT_EQ(entryTypeFromPermissions(LIBSSH2_SFTP_S_IFLNK | 0777, true),
              SftpEntryType::Symlink);
    EXPECT_EQ(entryTypeFromPermissions(LIBSSH2_SFTP_S_IFIFO | 0644, true),
              SftpEntryType::Fifo);
    EXPECT_EQ(entryTypeFromPermissions(LIBSSH2_SFTP_S_IFSOCK | 0755, true),
              SftpEntryType::Socket);
    EXPECT_EQ(entryTypeFromPermissions(LIBSSH2_SFTP_S_IFCHR | 0600, true),
              SftpEntryType::CharDevice);
    EXPECT_EQ(entryTypeFromPermissions(LIBSSH2_SFTP_S_IFBLK | 0600, true),
              SftpEntryType::BlockDevice);
    EXPECT_EQ(entryTypeFromPermissions(0644, true), SftpEntryType::Unknown); // no type bits
    EXPECT_EQ(entryTypeFromPermissions(0, false), SftpEntryType::Unknown);   // flag absent
    EXPECT_NE(sshclient::sftp::toString(SftpEntryType::File), nullptr);
    EXPECT_STREQ(sshclient::sftp::toString(SftpEntryType::Directory), "directory");
    EXPECT_STREQ(sshclient::sftp::toString(SftpEntryType::Symlink), "symlink");
    EXPECT_STREQ(sshclient::sftp::toString(SftpEntryType::Unknown), "unknown");
}

TEST(JoinRemotePathTest, SeparatorCases)
{
    EXPECT_EQ(joinRemotePath("/tmp", "a"), "/tmp/a");
    EXPECT_EQ(joinRemotePath("/tmp/", "a"), "/tmp/a");
    EXPECT_EQ(joinRemotePath("/", "a"), "/a");
    EXPECT_EQ(joinRemotePath("", "a"), "a");
    EXPECT_EQ(joinRemotePath("/a/b", ".hidden"), "/a/b/.hidden");
}

TEST(SftpOpenFlagsTest, ListCombinesBits)
{
    using F = SftpSession::OpenFlags;
    EXPECT_EQ(SftpSession::toOpenFlags(F::Read), LIBSSH2_FXF_READ);
    EXPECT_EQ(SftpSession::toOpenFlags(F::Write), LIBSSH2_FXF_WRITE);
    EXPECT_EQ(SftpSession::toOpenFlags({F::Read, F::Write}),
              LIBSSH2_FXF_READ | LIBSSH2_FXF_WRITE);
    EXPECT_EQ(SftpSession::toOpenFlags({F::Write, F::Create, F::Truncate}),
              LIBSSH2_FXF_WRITE | LIBSSH2_FXF_CREAT | LIBSSH2_FXF_TRUNC);
}

// ================================================================== pump fakes

namespace {

class MemoryReader final : public IBlockReader {
public:
    explicit MemoryReader(std::string data, std::size_t maxPerCall = 0)
        : data_(std::move(data)), maxPerCall_(maxPerCall)
    {
    }
    // Fail the read once pos >= failAt (failAt==npos disables).
    std::size_t failAt = std::string::npos;
    std::uint64_t lastSeek = 0;
    bool seekCalled = false;

    bool readSome(char* buffer, std::size_t maxLen, std::size_t& bytesReadOut,
                  std::string& errorOut) override
    {
        if (pos_ >= failAt) {
            errorOut = "injected read error";
            return false;
        }
        if (pos_ >= data_.size()) {
            bytesReadOut = 0;
            return true; // EOF
        }
        std::size_t want = data_.size() - pos_;
        if (maxPerCall_ != 0 && want > maxPerCall_) {
            want = maxPerCall_;
        }
        if (want > maxLen) {
            want = maxLen;
        }
        std::memcpy(buffer, data_.data() + pos_, want);
        pos_ += want;
        bytesReadOut = want;
        return true;
    }

    bool seek(std::uint64_t offset, std::string& errorOut) override
    {
        (void)errorOut;
        seekCalled = true;
        lastSeek = offset;
        pos_ = static_cast<std::size_t>(offset);
        return true;
    }

private:
    std::string data_;
    std::size_t maxPerCall_;
    std::size_t pos_ = 0;
};

class MemoryWriter final : public IBlockWriter {
public:
    std::size_t maxPerCall = 0; // 0 = accept everything (short-write injection)
    std::size_t failAt = std::string::npos;
    std::uint64_t lastSeek = 0;
    bool seekCalled = false;
    std::string data;

    bool writeAll(const char* chunk, std::size_t len, std::string& errorOut) override
    {
        // Model short writes: persist in maxPerCall pieces (or all at once).
        std::size_t done = 0;
        while (done < len) {
            if (data.size() >= failAt) {
                errorOut = "injected write error";
                return false;
            }
            std::size_t piece = len - done;
            if (maxPerCall != 0 && piece > maxPerCall) {
                piece = maxPerCall;
            }
            // Bound the piece by the remaining budget before failAt.
            if (data.size() + piece > failAt) {
                piece = failAt - data.size();
            }
            data.append(chunk + done, piece);
            done += piece;
        }
        return true;
    }

    bool seek(std::uint64_t offset, std::string& errorOut) override
    {
        (void)errorOut;
        seekCalled = true;
        lastSeek = offset;
        if (offset > data.size()) {
            data.resize(static_cast<std::size_t>(offset), '\0');
        }
        return true;
    }
};

std::string PatternData(std::size_t bytes)
{
    std::string out;
    out.reserve(bytes);
    for (std::size_t i = 0; i < bytes; ++i) {
        out.push_back(static_cast<char>((i * 31 + 7) & 0xFF));
    }
    return out;
}

} // namespace

TEST(TransferBlockSizeTest, Is32KiB)
{
    EXPECT_EQ(sshclient::sftp::kSftpTransferBlockSize, 32u * 1024u);
}

TEST(TransferPumpTest, RoundTripExactBytesAndChunking)
{
    const std::string src = PatternData(100000);
    MemoryReader reader(src);
    MemoryWriter writer;
    std::vector<std::uint64_t> progress;
    TransferHooks hooks;
    hooks.onProgress = [&](const TransferProgress& p) {
        EXPECT_EQ(p.bytesTotalHint, 100000u);
        progress.push_back(p.bytesDone);
    };
    const TransferResult result =
        sshclient::sftp::pumpTransfer(reader, writer, src.size(), hooks);
    EXPECT_TRUE(result.ok) << result.message;
    EXPECT_EQ(result.code, 0);
    EXPECT_EQ(result.bytesTransferred, src.size());
    EXPECT_EQ(writer.data, src);
    // 100000 = 32768*3 + 1696: one progress report per completed chunk.
    ASSERT_EQ(progress.size(), 4u);
    EXPECT_EQ(progress[0], 32768u);
    EXPECT_EQ(progress[1], 65536u);
    EXPECT_EQ(progress[2], 98304u);
    EXPECT_EQ(progress[3], 100000u);
}

TEST(TransferPumpTest, EmptySourceSucceedsWithoutProgress)
{
    MemoryReader reader("");
    MemoryWriter writer;
    bool called = false;
    TransferHooks hooks;
    hooks.onProgress = [&](const TransferProgress&) { called = true; };
    const TransferResult result = sshclient::sftp::pumpTransfer(reader, writer, 0, hooks);
    EXPECT_TRUE(result.ok);
    EXPECT_EQ(result.bytesTransferred, 0u);
    EXPECT_FALSE(called);
    EXPECT_TRUE(writer.data.empty());
}

TEST(TransferPumpTest, ShortReadsStillReassemble)
{
    const std::string src = PatternData(70000);
    MemoryReader reader(src, 7000); // at most 7k per read call
    MemoryWriter writer;
    const TransferResult result = sshclient::sftp::pumpTransfer(reader, writer, 0, {});
    EXPECT_TRUE(result.ok) << result.message;
    EXPECT_EQ(writer.data, src);
}

TEST(TransferPumpTest, ShortWritesStillPersist)
{
    const std::string src = PatternData(50000);
    MemoryReader reader(src);
    MemoryWriter writer;
    writer.maxPerCall = 1000;
    const TransferResult result = sshclient::sftp::pumpTransfer(reader, writer, 0, {});
    EXPECT_TRUE(result.ok) << result.message;
    EXPECT_EQ(writer.data, src);
}

TEST(TransferPumpTest, CancelAtThresholdReportsBytesSoFar)
{
    const std::string src = PatternData(200000);
    MemoryReader reader(src);
    MemoryWriter writer;
    std::atomic<bool> cancel{false};
    TransferHooks hooks;
    hooks.cancel = &cancel;
    hooks.onProgress = [&](const TransferProgress& p) {
        if (p.bytesDone >= 65536) {
            cancel.store(true);
        }
    };
    const TransferResult result = sshclient::sftp::pumpTransfer(reader, writer, src.size(),
                                                                hooks);
    EXPECT_FALSE(result.ok);
    EXPECT_EQ(result.code, codes::kSshErrorCodeSftpCancelled);
    EXPECT_EQ(result.bytesTransferred, 65536u);
    EXPECT_EQ(writer.data.size(), 65536u);
    EXPECT_EQ(writer.data, src.substr(0, 65536));
}

TEST(TransferPumpTest, PreTrippedCancelTransfersNothing)
{
    MemoryReader reader(PatternData(1000));
    MemoryWriter writer;
    std::atomic<bool> cancel{true};
    TransferHooks hooks;
    hooks.cancel = &cancel;
    const TransferResult result = sshclient::sftp::pumpTransfer(reader, writer, 0, hooks);
    EXPECT_FALSE(result.ok);
    EXPECT_EQ(result.code, codes::kSshErrorCodeSftpCancelled);
    EXPECT_EQ(result.bytesTransferred, 0u);
    EXPECT_TRUE(writer.data.empty());
}

TEST(TransferPumpTest, ReaderErrorMidwayKeepsBytesSoFar)
{
    const std::string src = PatternData(100000);
    MemoryReader reader(src);
    reader.failAt = 32768; // second chunk read fails at its start
    MemoryWriter writer;
    const TransferResult result = sshclient::sftp::pumpTransfer(reader, writer, 0, {});
    EXPECT_FALSE(result.ok);
    EXPECT_EQ(result.code, codes::kSshErrorCodeSftpTransferFailed);
    EXPECT_EQ(result.bytesTransferred, 32768u); // first full chunk landed
    EXPECT_EQ(writer.data, src.substr(0, 32768));
}

TEST(TransferPumpTest, WriterErrorMidwayKeepsBytesSoFar)
{
    const std::string src = PatternData(100000);
    MemoryReader reader(src);
    MemoryWriter writer;
    writer.failAt = 32768; // second chunk write fails before persisting
    const TransferResult result = sshclient::sftp::pumpTransfer(reader, writer, 0, {});
    EXPECT_FALSE(result.ok);
    EXPECT_EQ(result.code, codes::kSshErrorCodeSftpTransferFailed);
    EXPECT_EQ(result.bytesTransferred, 32768u);
    EXPECT_EQ(writer.data, src.substr(0, 32768));
}

// ================================================================== admission (Idle session, no IO thread)

TEST(SftpAdmissionTest, OpenRejectedOutsideEstablished)
{
    SessionThread thread; // never started: open() must fail before posting
    StateRecorder recorder;
    SshSession session(thread, {}, std::ref(recorder));
    SftpSession sftp(session);
    EXPECT_FALSE(sftp.isOpen());
    const SftpResult result = sftp.open();
    EXPECT_FALSE(result.ok);
    EXPECT_NE(result.code, 0);
    EXPECT_FALSE(sftp.isOpen());
    sftp.close(); // no-op, must not hang without a running loop
}

TEST(SftpAdmissionTest, OpsRejectedWhileUnopened)
{
    SessionThread thread;
    StateRecorder recorder;
    SshSession session(thread, {}, std::ref(recorder));
    SftpSession sftp(session);
    std::vector<SftpEntry> entries;
    EXPECT_FALSE(sftp.listDir("/tmp", entries).ok);
    SftpAttrs attrs;
    EXPECT_FALSE(sftp.stat("/tmp", true, attrs).ok);
    EXPECT_FALSE(sftp.stat("/tmp", false, attrs).ok);
    std::string target;
    EXPECT_FALSE(sftp.readLink("/tmp", target).ok);
    EXPECT_FALSE(sftp.makeDir("/tmp/x").ok);
    EXPECT_FALSE(sftp.rename("/a", "/b").ok);
    EXPECT_FALSE(sftp.removeFile("/a").ok);
    EXPECT_FALSE(sftp.removeDir("/a").ok);
    EXPECT_FALSE(sftp.setStat("/a", SftpAttrs{}).ok);
    std::unique_ptr<SftpSession::File> file;
    EXPECT_FALSE(
        sftp.openFile("/a", SftpSession::toOpenFlags(SftpSession::OpenFlags::Read), 0, file)
            .ok);
    EXPECT_EQ(file.get(), nullptr);
    // All rejections are local misuse (500), none touched the network.
    EXPECT_EQ(sftp.listDir("/tmp", entries).code, codes::kSshErrorCodeInternalError);
}

// ==================================================== integration (live server)

namespace {

bool LoadSftpEnv(SshIntegrationEnvironment& env, std::string& remoteDir)
{
    if (!LoadSshIntegrationEnvironment(env)) {
        return false;
    }
    const char* dir = std::getenv("SSH_TEST_SFTP_DIR");
    remoteDir = (dir != nullptr && dir[0] != '\0') ? dir : "/tmp/uwpsshtool-sftp-test";
    return true;
}

bool IsPosixServer()
{
    const char* gate = std::getenv("SSH_TEST_POSIX");
    return gate != nullptr && gate[0] != '\0';
}

// Connect + password-auth into Established. Returns false (with a gtest
// message) on failure; the caller owns thread/session lifetimes.
bool ConnectForSftp(SessionThread& thread, StateRecorder& recorder, SshSession& session,
                    const SshIntegrationEnvironment& env)
{
    if (!session.connect(env.host, env.port, env.user)) {
        ADD_FAILURE() << "connect() not admitted";
        return false;
    }
    if (!recorder.waitFor(SshSessionState::Authenticating, 20s)) {
        ADD_FAILURE() << "never reached Authenticating";
        return false;
    }
    std::string password = env.password;
    std::mutex mutex;
    std::condition_variable cv;
    bool authed = false;
    bool ok = false;
    if (!session.authenticatePassword(
            password,
            [&](const sshclient::ssh::AuthResult& result) {
                {
                    std::lock_guard<std::mutex> lock(mutex);
                    authed = true;
                    ok = result.success;
                }
                cv.notify_all();
            })) {
        ADD_FAILURE() << "authenticatePassword() not admitted";
        return false;
    }
    {
        std::unique_lock<std::mutex> lock(mutex);
        if (!cv.wait_for(lock, 15s, [&] { return authed; })) {
            ADD_FAILURE() << "auth timed out";
            return false;
        }
    }
    if (!ok) {
        ADD_FAILURE() << "password auth rejected by " << env.host;
        return false;
    }
    if (!recorder.waitFor(SshSessionState::Established, 10s)) {
        ADD_FAILURE() << "never reached Established";
        return false;
    }
    return true;
}

std::string Sha256HexOfFile(const std::string& path)
{
    FILE* file = nullptr;
    if (::fopen_s(&file, path.c_str(), "rb") != 0 || file == nullptr) {
        return {};
    }
    SHA256_CTX ctx;
    ::SHA256_Init(&ctx);
    char buf[65536];
    std::size_t n = 0;
    while ((n = ::fread(buf, 1, sizeof(buf), file)) > 0) {
        ::SHA256_Update(&ctx, buf, n);
    }
    ::fclose(file);
    unsigned char digest[SHA256_DIGEST_LENGTH]{};
    ::SHA256_Final(digest, &ctx);
    static const char* hex = "0123456789abcdef";
    std::string out;
    out.reserve(SHA256_DIGEST_LENGTH * 2);
    for (unsigned char byte : digest) {
        out.push_back(hex[byte >> 4]);
        out.push_back(hex[byte & 0xF]);
    }
    return out;
}

bool WritePatternFile(const std::string& path, std::uint64_t bytes)
{
    FILE* file = nullptr;
    if (::fopen_s(&file, path.c_str(), "wb") != 0 || file == nullptr) {
        return false;
    }
    std::mt19937_64 rng(0x53F7u ^ bytes); // deterministic: same bytes every run
    char buf[65536];
    std::uint64_t left = bytes;
    while (left > 0) {
        const std::size_t step = left > sizeof(buf) ? sizeof(buf) : (std::size_t)left;
        for (std::size_t i = 0; i < step; ++i) {
            buf[i] = static_cast<char>(rng() & 0xFF);
        }
        if (::fwrite(buf, 1, step, file) != step) {
            ::fclose(file);
            return false;
        }
        left -= step;
    }
    ::fclose(file);
    return true;
}

void RemoveLocalFile(const std::string& path)
{
    ::remove(path.c_str());
}

// mkdir, tolerating leftovers from a killed run (stat to confirm instead).
// Creates missing parents like mkdir -p (SFTP has no recursive flag).
bool EnsureRemoteDir(SftpSession& sftp, const std::string& dir, const SftpCallOptions& opts)
{
    std::string prefix;
    for (std::size_t i = 0; i < dir.size(); ++i) {
        if (dir[i] != '/' || i == 0) {
            continue;
        }
        prefix = dir.substr(0, i);
        if (prefix.empty()) {
            continue;
        }
        const SftpResult made = sftp.makeDir(prefix, 0755, opts);
        if (!made.ok) {
            SftpAttrs probe;
            if (!sftp.stat(prefix, true, probe, opts).ok) {
                ADD_FAILURE() << "cannot create or stat remote dir " << prefix << ": "
                              << made.message;
                return false;
            }
        }
    }
    if (sftp.makeDir(dir, 0755, opts).ok) {
        return true;
    }
    SftpAttrs probe;
    if (!sftp.stat(dir, true, probe, opts).ok) {
        ADD_FAILURE() << "cannot create or stat remote dir " << dir;
        return false;
    }
    return true;
}

} // namespace

TEST(SftpIntegrationTest, MetadataRoundtrip)
{
    SshIntegrationEnvironment env;
    std::string remoteDir;
    if (!LoadSftpEnv(env, remoteDir)) {
        GTEST_SKIP() << "set SSH_TEST_HOST/PORT/USER/PASSWORD to enable live SFTP";
    }
    SessionThread thread;
    ASSERT_TRUE(thread.start());
    StateRecorder recorder;
    SftpCallOptions opts;
    opts.timeoutMs = 15000;
    {
        SshSession session(thread, {}, std::ref(recorder));
        ASSERT_TRUE(ConnectForSftp(thread, recorder, session, env));
        SftpSession sftp(session);
        {
            const SftpResult opened = sftp.open(opts);
            ASSERT_TRUE(opened.ok) << opened.message;
        }

        const std::string dir = remoteDir + "/meta";
        // mkdir (tolerate leftovers from a killed run), then exercise.
        ASSERT_TRUE(EnsureRemoteDir(sftp, dir, opts));
        const std::string file = dir + "/hello.txt";
        sftp.removeFile(file, opts); // ignore leftovers
        // Create via upload of a tiny local file.
        const std::string tiny = "sftp-test-tiny.bin";
        ASSERT_TRUE(WritePatternFile(tiny, 100));
        {
            TransferHooks hooks;
            const TransferResult up =
                sshclient::sftp::uploadFile(sftp, tiny, file, 0, hooks, opts);
            EXPECT_TRUE(up.ok) << up.message;
            EXPECT_EQ(up.bytesTransferred, 100u);
        }
        // stat: size present.
        {
            SftpAttrs attrs;
            ASSERT_TRUE(sftp.stat(file, true, attrs, opts).ok);
            EXPECT_TRUE(attrs.hasSize);
            EXPECT_EQ(attrs.size, 100u);
        }
        // listDir: entry present with matching size; "." / ".." filtered.
        {
            std::vector<SftpEntry> entries;
            ASSERT_TRUE(sftp.listDir(dir, entries, opts).ok);
            bool found = false;
            for (const SftpEntry& entry : entries) {
                EXPECT_NE(entry.name, ".");
                EXPECT_NE(entry.name, "..");
                if (entry.name == "hello.txt") {
                    found = true;
                    EXPECT_EQ(entry.type, SftpEntryType::File);
                    if (entry.attrs.hasSize) {
                        EXPECT_EQ(entry.attrs.size, 100u);
                    }
                }
            }
            EXPECT_TRUE(found);
        }
        // rename roundtrip.
        {
            const std::string renamed = dir + "/hello2.txt";
            sftp.removeFile(renamed, opts);
            ASSERT_TRUE(sftp.rename(file, renamed, opts).ok);
            SftpAttrs gone;
            EXPECT_FALSE(sftp.stat(file, true, gone, opts).ok);
            EXPECT_EQ(sftp.stat(file, true, gone, opts).code,
                      codes::kSshErrorCodeSftpNoSuchFile);
            SftpAttrs attrs;
            ASSERT_TRUE(sftp.stat(renamed, true, attrs, opts).ok);
            ASSERT_TRUE(sftp.rename(renamed, file, opts).ok);
        }
        // Missing paths map to 602.
        {
            SftpAttrs attrs;
            EXPECT_EQ(sftp.stat(dir + "/nope-missing", true, attrs, opts).code,
                      codes::kSshErrorCodeSftpNoSuchFile);
            EXPECT_EQ(sftp.removeFile(dir + "/nope-missing", opts).code,
                      codes::kSshErrorCodeSftpNoSuchFile);
        }
        // Non-empty rmdir fails (server-dependent code: OpenSSH uses FAILURE).
        {
            const SftpResult rmdir = sftp.removeDir(dir, opts);
            EXPECT_FALSE(rmdir.ok);
        }
        EXPECT_TRUE(sftp.removeFile(file, opts).ok);
        EXPECT_TRUE(sftp.removeDir(dir, opts).ok);
        sftp.close();
        EXPECT_FALSE(sftp.isOpen());
        session.close();
        EXPECT_TRUE(recorder.waitFor(SshSessionState::Closed, 5s));
        thread.stop();
    }
    RemoveLocalFile("sftp-test-tiny.bin");
}

TEST(SftpIntegrationTest, UploadDownload50MBIdentical)
{
    SshIntegrationEnvironment env;
    std::string remoteDir;
    if (!LoadSftpEnv(env, remoteDir)) {
        GTEST_SKIP() << "set SSH_TEST_HOST/PORT/USER/PASSWORD to enable live SFTP";
    }
    const std::string src = "sftp-test-50m-src.bin";
    const std::string dst = "sftp-test-50m-dst.bin";
    ASSERT_TRUE(WritePatternFile(src, 50ull * 1024 * 1024));
    const std::string want = Sha256HexOfFile(src);
    ASSERT_EQ(want.size(), 64u);

    SessionThread thread;
    ASSERT_TRUE(thread.start());
    StateRecorder recorder;
    SftpCallOptions opts;
    opts.timeoutMs = 30000;
    {
        SshSession session(thread, {}, std::ref(recorder));
        ASSERT_TRUE(ConnectForSftp(thread, recorder, session, env));
        SftpSession sftp(session);
        const SftpResult opened = sftp.open(opts);
        ASSERT_TRUE(opened.ok) << opened.message;
        ASSERT_TRUE(EnsureRemoteDir(sftp, remoteDir, opts));

        const std::string remote = remoteDir + "/big50.bin";
        sftp.removeFile(remote, opts);
        std::uint64_t progressLast = 0;
        std::uint64_t progressCalls = 0;
        TransferHooks upHooks;
        upHooks.onProgress = [&](const TransferProgress& p) {
            ++progressCalls;
            progressLast = p.bytesDone;
        };
        const TransferResult up =
            sshclient::sftp::uploadFile(sftp, src, remote, 0, upHooks, opts);
        ASSERT_TRUE(up.ok) << up.message;
        EXPECT_EQ(up.bytesTransferred, 50ull * 1024 * 1024);
        EXPECT_EQ(progressLast, up.bytesTransferred);
        EXPECT_GT(progressCalls, 0u);

        const TransferResult down =
            sshclient::sftp::downloadFile(sftp, remote, dst, 0, {}, opts);
        ASSERT_TRUE(down.ok) << down.message;
        EXPECT_EQ(down.bytesTransferred, 50ull * 1024 * 1024);
        EXPECT_EQ(Sha256HexOfFile(dst), want);

        EXPECT_TRUE(sftp.removeFile(remote, opts).ok);
        sftp.close();
        session.close();
        EXPECT_TRUE(recorder.waitFor(SshSessionState::Closed, 5s));
        thread.stop();
    }
    RemoveLocalFile(src);
    RemoveLocalFile(dst);
}

TEST(SftpIntegrationTest, InterruptedUploadResumesIdentical)
{
    SshIntegrationEnvironment env;
    std::string remoteDir;
    if (!LoadSftpEnv(env, remoteDir)) {
        GTEST_SKIP() << "set SSH_TEST_HOST/PORT/USER/PASSWORD to enable live SFTP";
    }
    const std::string src = "sftp-test-resume-src.bin";
    const std::string dst = "sftp-test-resume-dst.bin";
    const std::uint64_t kTotal = 50ull * 1024 * 1024;
    const std::uint64_t kCutAt = 8ull * 1024 * 1024; // exact multiple of 32 KiB
    ASSERT_TRUE(WritePatternFile(src, kTotal));
    const std::string want = Sha256HexOfFile(src);
    ASSERT_EQ(want.size(), 64u);

    SessionThread thread;
    ASSERT_TRUE(thread.start());
    StateRecorder recorder;
    SftpCallOptions opts;
    opts.timeoutMs = 30000;
    {
        SshSession session(thread, {}, std::ref(recorder));
        ASSERT_TRUE(ConnectForSftp(thread, recorder, session, env));
        SftpSession sftp(session);
        ASSERT_TRUE(sftp.open(opts).ok);
        ASSERT_TRUE(EnsureRemoteDir(sftp, remoteDir, opts));

        const std::string remote = remoteDir + "/resume50.bin";
        sftp.removeFile(remote, opts);
        // Leg 1: cancel mid-transfer.
        std::atomic<bool> cancel{false};
        TransferHooks cutHooks;
        cutHooks.cancel = &cancel;
        cutHooks.onProgress = [&](const TransferProgress& p) {
            if (p.bytesDone >= kCutAt) {
                cancel.store(true);
            }
        };
        const TransferResult cut =
            sshclient::sftp::uploadFile(sftp, src, remote, 0, cutHooks, opts);
        EXPECT_FALSE(cut.ok);
        EXPECT_EQ(cut.code, codes::kSshErrorCodeSftpCancelled);
        // The remote partial must hold exactly the acknowledged prefix.
        {
            SftpAttrs attrs;
            ASSERT_TRUE(sftp.stat(remote, true, attrs, opts).ok);
            ASSERT_TRUE(attrs.hasSize);
            EXPECT_EQ(attrs.size, kCutAt);
            EXPECT_EQ(cut.bytesTransferred, kCutAt);
        }
        // Leg 2: resume from the partial size.
        {
            const TransferResult resumed =
                sshclient::sftp::uploadFile(sftp, src, remote, kCutAt, {}, opts);
            ASSERT_TRUE(resumed.ok) << resumed.message;
            EXPECT_EQ(resumed.bytesTransferred, kTotal - kCutAt);
        }
        const TransferResult down =
            sshclient::sftp::downloadFile(sftp, remote, dst, 0, {}, opts);
        ASSERT_TRUE(down.ok) << down.message;
        EXPECT_EQ(Sha256HexOfFile(dst), want);

        EXPECT_TRUE(sftp.removeFile(remote, opts).ok);
        sftp.close();
        session.close();
        EXPECT_TRUE(recorder.waitFor(SshSessionState::Closed, 5s));
        thread.stop();
    }
    RemoveLocalFile(src);
    RemoveLocalFile(dst);
}

TEST(SftpIntegrationTest, PosixSymlinkAndChmod)
{
    const char* gate = std::getenv("SSH_TEST_POSIX");
    if (gate == nullptr || gate[0] == '\0') {
        GTEST_SKIP() << "set SSH_TEST_POSIX=1 on a POSIX server to enable symlink/chmod checks";
    }
    SshIntegrationEnvironment env;
    std::string remoteDir;
    if (!LoadSftpEnv(env, remoteDir)) {
        GTEST_SKIP() << "set SSH_TEST_HOST/PORT/USER/PASSWORD to enable live SFTP";
    }
    SessionThread thread;
    ASSERT_TRUE(thread.start());
    StateRecorder recorder;
    SftpCallOptions opts;
    opts.timeoutMs = 15000;
    {
        SshSession session(thread, {}, std::ref(recorder));
        ASSERT_TRUE(ConnectForSftp(thread, recorder, session, env));
        SftpSession sftp(session);
        ASSERT_TRUE(sftp.open(opts).ok);
        const std::string dir = remoteDir + "/posix";
        ASSERT_TRUE(EnsureRemoteDir(sftp, dir, opts));
        // chmod roundtrip via setStat + stat.
        const std::string file = dir + "/mode.txt";
        sftp.removeFile(file, opts);
        const std::string tiny = "sftp-test-posix.bin";
        ASSERT_TRUE(WritePatternFile(tiny, 64));
        {
            const TransferResult up =
                sshclient::sftp::uploadFile(sftp, tiny, file, 0, {}, opts);
            ASSERT_TRUE(up.ok) << up.message;
        }
        {
            SftpAttrs chmod;
            chmod.hasPermissions = true;
            chmod.permissions = 0600;
            ASSERT_TRUE(sftp.setStat(file, chmod, opts).ok);
            SftpAttrs attrs;
            ASSERT_TRUE(sftp.stat(file, true, attrs, opts).ok);
            ASSERT_TRUE(attrs.hasPermissions);
            EXPECT_EQ(attrs.permissions & 0777u, 0600u);
        }
        // Symlink: create with execCommand (no symlink-create op in F01),
        // then lstat/readLink/listDir must agree.
        const std::string link = dir + "/mode-link";
        sftp.removeFile(link, opts);
        {
            const sshclient::ssh::ExecResult made =
                sshclient::ssh::execCommand(session, "ln -s mode.txt '" + link + "'");
            ASSERT_TRUE(made.ok) << made.message;
        }
        {
            SftpAttrs lstat;
            ASSERT_TRUE(sftp.stat(link, false, lstat, opts).ok);
            ASSERT_TRUE(lstat.hasPermissions);
            EXPECT_EQ(sshclient::sftp::entryTypeFromPermissions(lstat.permissions, true),
                      SftpEntryType::Symlink);
            SftpAttrs follow;
            ASSERT_TRUE(sftp.stat(link, true, follow, opts).ok);
            EXPECT_EQ(sshclient::sftp::entryTypeFromPermissions(
                          follow.permissions, follow.hasPermissions),
                      SftpEntryType::File);
        }
        {
            std::string got;
            ASSERT_TRUE(sftp.readLink(link, got, opts).ok);
            EXPECT_EQ(got, "mode.txt");
        }
        {
            std::vector<SftpEntry> entries;
            ASSERT_TRUE(sftp.listDir(dir, entries, opts).ok);
            bool found = false;
            for (const SftpEntry& entry : entries) {
                if (entry.name == "mode-link") {
                    found = true;
                    EXPECT_EQ(entry.type, SftpEntryType::Symlink);
                    EXPECT_EQ(entry.linkTarget, "mode.txt");
                }
            }
            EXPECT_TRUE(found);
        }
        EXPECT_TRUE(sftp.removeFile(link, opts).ok);
        EXPECT_TRUE(sftp.removeFile(file, opts).ok);
        EXPECT_TRUE(sftp.removeDir(dir, opts).ok);
        sftp.close();
        session.close();
        EXPECT_TRUE(recorder.waitFor(SshSessionState::Closed, 5s));
        thread.stop();
        RemoveLocalFile(tiny);
    }
}
