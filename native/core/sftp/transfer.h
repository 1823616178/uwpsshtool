#pragma once

// F01: chunked SFTP file transfer (01-DESIGN.md section 11.1).
//
// Layering: pumpTransfer() is the testable core — it copies 32 KiB blocks
// between an IBlockReader and an IBlockWriter, honoring a resume offset (both
// sides pre-seeked by the caller), a caller-owned atomic cancel flag, and a
// per-chunk progress callback. The SFTP + FILE adapters below bind it to
// SftpSession::File and stdio; uploadFile()/downloadFile() are the
// integration-test entry points (and the future F02 bridge's model).
//
// Block size is fixed at 32 KiB (design section 11.1): one SFTP READ/WRITE
// packet per chunk, matching OpenSSH's own window. Progress is reported
// unthrottled per completed chunk; F02 throttles to 200 ms for the UI.
// No timeout inside the pump — cancel is the abort path; each underlying
// remote call carries its own SftpCallOptions deadline.
//
// Pure logic except the adapters' actual IO; the pump is fully covered by
// offline unit tests with fault-injecting fakes (short reads/writes,
// mid-transfer errors, cancel-at-N).

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <functional>
#include <string>

#include "sftp_session.h"

namespace sshclient {
namespace sftp {

inline constexpr std::size_t kSftpTransferBlockSize = 32u * 1024u;

struct TransferProgress {
    std::uint64_t bytesDone = 0;
    std::uint64_t bytesTotalHint = 0; // 0 = unknown
};

struct TransferHooks {
    std::function<void(const TransferProgress&)> onProgress; // per chunk
    const std::atomic<bool>* cancel = nullptr;               // -> SftpCancelled
};

struct TransferResult {
    bool ok = false;
    int code = 0; // unified code (0 on success; 605/606 for pump failures)
    std::string message;
    std::uint64_t bytesTransferred = 0;
    static TransferResult success(std::uint64_t bytes)
    {
        TransferResult result;
        result.ok = true;
        result.bytesTransferred = bytes;
        return result;
    }
};

// ---- block IO interfaces (pump operands) ----

class IBlockReader {
public:
    virtual ~IBlockReader() = default;
    // Up to maxLen bytes; bytesReadOut==0 means EOF. false = hard error.
    virtual bool readSome(char* buffer, std::size_t maxLen, std::size_t& bytesReadOut,
                          std::string& errorOut) = 0;
    virtual bool seek(std::uint64_t offset, std::string& errorOut) = 0;
};

class IBlockWriter {
public:
    virtual ~IBlockWriter() = default;
    // Must persist all len bytes (loop internally over short writes).
    virtual bool writeAll(const char* data, std::size_t len, std::string& errorOut) = 0;
    virtual bool seek(std::uint64_t offset, std::string& errorOut) = 0;
};

// Core copy loop: reads chunks (<= kSftpTransferBlockSize) until EOF, writes
// each, accounts progress. Cancel is checked before every chunk.
TransferResult pumpTransfer(IBlockReader& src, IBlockWriter& dst,
                            std::uint64_t totalHint, const TransferHooks& hooks);

// ---- adapters ----

class SftpFileReader final : public IBlockReader {
public:
    SftpFileReader(SftpSession::File& file, SftpCallOptions options = {});
    bool readSome(char* buffer, std::size_t maxLen, std::size_t& bytesReadOut,
                  std::string& errorOut) override;
    bool seek(std::uint64_t offset, std::string& errorOut) override;

private:
    SftpSession::File& file_;
    SftpCallOptions options_;
};

class SftpFileWriter final : public IBlockWriter {
public:
    SftpFileWriter(SftpSession::File& file, SftpCallOptions options = {});
    bool writeAll(const char* data, std::size_t len, std::string& errorOut) override;
    bool seek(std::uint64_t offset, std::string& errorOut) override;

private:
    SftpSession::File& file_;
    SftpCallOptions options_;
};

// stdio adapters for host tests and tooling (offsets limited to long range;
// files beyond 2 GiB need the F02 stream adapters, not these).
class LocalFileReader final : public IBlockReader {
public:
    LocalFileReader();
    ~LocalFileReader() override;
    bool open(const std::string& path, std::string& errorOut);
    bool size(std::uint64_t& sizeOut, std::string& errorOut);
    bool readSome(char* buffer, std::size_t maxLen, std::size_t& bytesReadOut,
                  std::string& errorOut) override;
    bool seek(std::uint64_t offset, std::string& errorOut) override;
    void close();

private:
    void* file_ = nullptr; // FILE*, opaque to keep <cstdio> out of the header
};

class LocalFileWriter final : public IBlockWriter {
public:
    LocalFileWriter();
    ~LocalFileWriter() override;
    // truncate=true -> "wb", false -> open existing "r+b" (create if missing).
    bool open(const std::string& path, bool truncate, std::string& errorOut);
    bool writeAll(const char* data, std::size_t len, std::string& errorOut) override;
    bool seek(std::uint64_t offset, std::string& errorOut) override;
    void close();

private:
    void* file_ = nullptr;
};

// ---- convenience file transfers over an open SftpSession ----
//
// resumeOffset>0 continues at that byte on both sides (caller typically stats
// the remote side first): upload rejects offset > local size, and offset >
// remote size (a gap write would zero-fill server-side); download opens the
// local file without truncating and seeks. offset==0 truncates/creates.
TransferResult uploadFile(SftpSession& sftp, const std::string& localPath,
                          const std::string& remotePath, std::uint64_t resumeOffset,
                          const TransferHooks& hooks,
                          SftpCallOptions remoteOptions = {},
                          long remoteMode = 0644);
TransferResult downloadFile(SftpSession& sftp, const std::string& remotePath,
                            const std::string& localPath, std::uint64_t resumeOffset,
                            const TransferHooks& hooks,
                            SftpCallOptions remoteOptions = {});

} // namespace sftp
} // namespace sshclient
