#include "transfer.h"

#include "debug_log.h"
#include "ssh/error_codes.h"

#include <cstdio>
#include <vector>

#define SSH_LOG(...) sshclient::diagnostics::debugLog("sftp", __VA_ARGS__)

namespace sshclient {
namespace sftp {
namespace {

TransferResult pumpError(int code, const std::string& message, std::uint64_t done)
{
    TransferResult result;
    result.code = code;
    result.message = message;
    result.bytesTransferred = done;
    return result;
}

} // namespace

TransferResult pumpTransfer(IBlockReader& src, IBlockWriter& dst,
                            std::uint64_t totalHint, const TransferHooks& hooks)
{
    std::vector<char> chunk(kSftpTransferBlockSize);
    std::uint64_t done = 0;
    for (;;) {
        if (hooks.cancel != nullptr && hooks.cancel->load(std::memory_order_acquire)) {
            return pumpError(ssh::kSshErrorCodeSftpCancelled, "transfer cancelled", done);
        }
        std::size_t got = 0;
        std::string error;
        if (!src.readSome(chunk.data(), chunk.size(), got, error)) {
            return pumpError(ssh::kSshErrorCodeSftpTransferFailed, "read: " + error, done);
        }
        if (got == 0) {
            TransferResult result = TransferResult::success(done);
            return result; // EOF
        }
        if (!dst.writeAll(chunk.data(), got, error)) {
            return pumpError(ssh::kSshErrorCodeSftpTransferFailed, "write: " + error, done);
        }
        done += got;
        if (hooks.onProgress) {
            try {
                TransferProgress progress;
                progress.bytesDone = done;
                progress.bytesTotalHint = totalHint;
                hooks.onProgress(progress);
            } catch (...) {
                SSH_LOG("transfer progress callback threw; ignored");
            }
        }
    }
}

// ---------------------------------------------------------------- adapters

SftpFileReader::SftpFileReader(SftpSession::File& file, SftpCallOptions options)
    : file_(file), options_(options)
{
}

bool SftpFileReader::readSome(char* buffer, std::size_t maxLen, std::size_t& bytesReadOut,
                              std::string& errorOut)
{
    const SftpResult result = file_.readSome(buffer, maxLen, bytesReadOut, options_);
    if (!result.ok) {
        errorOut = result.message;
        return false;
    }
    return true;
}

bool SftpFileReader::seek(std::uint64_t offset, std::string& errorOut)
{
    const SftpResult result = file_.seek(offset, options_);
    if (!result.ok) {
        errorOut = result.message;
        return false;
    }
    return true;
}

SftpFileWriter::SftpFileWriter(SftpSession::File& file, SftpCallOptions options)
    : file_(file), options_(options)
{
}

bool SftpFileWriter::writeAll(const char* data, std::size_t len, std::string& errorOut)
{
    const SftpResult result = file_.writeAll(data, len, options_);
    if (!result.ok) {
        errorOut = result.message;
        return false;
    }
    return true;
}

bool SftpFileWriter::seek(std::uint64_t offset, std::string& errorOut)
{
    const SftpResult result = file_.seek(offset, options_);
    if (!result.ok) {
        errorOut = result.message;
        return false;
    }
    return true;
}

LocalFileReader::LocalFileReader() = default;

LocalFileReader::~LocalFileReader()
{
    close();
}

bool LocalFileReader::open(const std::string& path, std::string& errorOut)
{
    close();
    FILE* file = nullptr;
    if (::fopen_s(&file, path.c_str(), "rb") != 0 || file == nullptr) {
        errorOut = "cannot open for reading: " + path;
        return false;
    }
    file_ = file;
    return true;
}

bool LocalFileReader::size(std::uint64_t& sizeOut, std::string& errorOut)
{
    if (file_ == nullptr) {
        errorOut = "not open";
        return false;
    }
    FILE* file = static_cast<FILE*>(file_);
    if (::fseek(file, 0, SEEK_END) != 0) {
        errorOut = "seek to end failed";
        return false;
    }
    const long tell = ::ftell(file);
    if (tell < 0) {
        errorOut = "tell failed";
        return false;
    }
    if (::fseek(file, 0, SEEK_SET) != 0) {
        errorOut = "seek to start failed";
        return false;
    }
    sizeOut = static_cast<std::uint64_t>(tell);
    return true;
}

bool LocalFileReader::readSome(char* buffer, std::size_t maxLen, std::size_t& bytesReadOut,
                               std::string& errorOut)
{
    bytesReadOut = 0;
    if (file_ == nullptr) {
        errorOut = "not open";
        return false;
    }
    if (buffer == nullptr || maxLen == 0) {
        errorOut = "null buffer or zero length";
        return false;
    }
    const std::size_t got = ::fread(buffer, 1, maxLen, static_cast<FILE*>(file_));
    if (got == 0) {
        if (::ferror(static_cast<FILE*>(file_)) != 0) {
            errorOut = "local read failed";
            return false;
        }
    }
    bytesReadOut = got;
    return true;
}

bool LocalFileReader::seek(std::uint64_t offset, std::string& errorOut)
{
    if (file_ == nullptr) {
        errorOut = "not open";
        return false;
    }
    if (::fseek(static_cast<FILE*>(file_), static_cast<long>(offset), SEEK_SET) != 0) {
        errorOut = "local seek failed";
        return false;
    }
    return true;
}

void LocalFileReader::close()
{
    if (file_ != nullptr) {
        ::fclose(static_cast<FILE*>(file_));
        file_ = nullptr;
    }
}

LocalFileWriter::LocalFileWriter() = default;

LocalFileWriter::~LocalFileWriter()
{
    close();
}

bool LocalFileWriter::open(const std::string& path, bool truncate, std::string& errorOut)
{
    close();
    FILE* file = nullptr;
    if (truncate) {
        if (::fopen_s(&file, path.c_str(), "wb") != 0 || file == nullptr) {
            errorOut = "cannot open for writing: " + path;
            return false;
        }
    } else {
        if (::fopen_s(&file, path.c_str(), "r+b") != 0 || file == nullptr) {
            // Missing local part (first resume leg): create it, then reopen.
            if (::fopen_s(&file, path.c_str(), "wb") != 0 || file == nullptr) {
                errorOut = "cannot open for resume: " + path;
                return false;
            }
            ::fclose(file);
            file = nullptr;
            if (::fopen_s(&file, path.c_str(), "r+b") != 0 || file == nullptr) {
                errorOut = "cannot reopen for resume: " + path;
                return false;
            }
        }
    }
    file_ = file;
    return true;
}

bool LocalFileWriter::writeAll(const char* data, std::size_t len, std::string& errorOut)
{
    if (file_ == nullptr) {
        errorOut = "not open";
        return false;
    }
    if (len == 0) {
        return true;
    }
    if (data == nullptr) {
        errorOut = "null buffer";
        return false;
    }
    FILE* file = static_cast<FILE*>(file_);
    std::size_t done = 0;
    while (done < len) {
        const std::size_t n = ::fwrite(data + done, 1, len - done, file);
        if (n == 0) {
            errorOut = "local write failed (disk full?)";
            return false;
        }
        done += n;
    }
    return true;
}

bool LocalFileWriter::seek(std::uint64_t offset, std::string& errorOut)
{
    if (file_ == nullptr) {
        errorOut = "not open";
        return false;
    }
    if (::fseek(static_cast<FILE*>(file_), static_cast<long>(offset), SEEK_SET) != 0) {
        errorOut = "local seek failed";
        return false;
    }
    return true;
}

void LocalFileWriter::close()
{
    if (file_ != nullptr) {
        ::fclose(static_cast<FILE*>(file_));
        file_ = nullptr;
    }
}

// ---------------------------------------------------------------- file transfers

namespace {

unsigned long uploadOpenFlags(std::uint64_t resumeOffset)
{
    using F = SftpSession::OpenFlags;
    unsigned long flags = SftpSession::toOpenFlags({F::Write, F::Create});
    if (resumeOffset == 0) {
        flags |= SftpSession::toOpenFlags(F::Truncate);
    }
    return flags;
}

} // namespace

TransferResult uploadFile(SftpSession& sftp, const std::string& localPath,
                          const std::string& remotePath, std::uint64_t resumeOffset,
                          const TransferHooks& hooks, SftpCallOptions remoteOptions,
                          long remoteMode)
{
    if (localPath.empty() || remotePath.empty()) {
        return pumpError(ssh::kSshErrorCodeInternalError, "upload rejected: empty path", 0);
    }
    LocalFileReader local;
    std::string error;
    if (!local.open(localPath, error)) {
        return pumpError(ssh::kSshErrorCodeSftpTransferFailed, error, 0);
    }
    std::uint64_t localSize = 0;
    if (!local.size(localSize, error)) {
        return pumpError(ssh::kSshErrorCodeSftpTransferFailed, error, 0);
    }
    if (resumeOffset > localSize) {
        return pumpError(ssh::kSshErrorCodeSftpTransferFailed,
                         "upload rejected: resume offset beyond local size", 0);
    }
    if (resumeOffset > 0) {
        // Refuse gap writes (the server would zero-fill the hole): the remote
        // side must already hold at least resumeOffset bytes.
        SftpAttrs remoteAttrs;
        const SftpResult statted = sftp.stat(remotePath, true, remoteAttrs, remoteOptions);
        if (!statted.ok) {
            return pumpError(statted.code, "upload resume stat: " + statted.message, 0);
        }
        if (!remoteAttrs.hasSize || remoteAttrs.size < resumeOffset) {
            return pumpError(ssh::kSshErrorCodeSftpTransferFailed,
                             "upload rejected: resume offset beyond remote size", 0);
        }
        if (!local.seek(resumeOffset, error)) {
            return pumpError(ssh::kSshErrorCodeSftpTransferFailed, error, 0);
        }
    }

    std::unique_ptr<SftpSession::File> remote;
    const SftpResult opened = sftp.openFile(remotePath, uploadOpenFlags(resumeOffset),
                                            remoteMode, remote, remoteOptions);
    if (!opened.ok) {
        return pumpError(opened.code, "upload open: " + opened.message, 0);
    }
    SftpFileWriter writer(*remote, remoteOptions);
    if (resumeOffset > 0 && !writer.seek(resumeOffset, error)) {
        const SftpResult seekClosed = remote->close(remoteOptions);
        if (!seekClosed.ok) {
            return pumpError(seekClosed.code, "upload seek: " + error + " (close: " +
                                                  seekClosed.message + ")",
                             0);
        }
        return pumpError(ssh::kSshErrorCodeSftpTransferFailed, "upload seek: " + error, 0);
    }
    const TransferResult pumped = pumpTransfer(local, writer, localSize, hooks);
    const SftpResult closed = remote->close(remoteOptions);
    if (!pumped.ok) {
        if (!closed.ok) {
            TransferResult result = pumped;
            result.message += " (close: " + closed.message + ")";
            return result;
        }
        // Report progress reached even on cancel/failure (caller resumes from
        // the remote size anyway; bytesTransferred is informational).
        return pumped;
    }
    if (!closed.ok) {
        return pumpError(closed.code, "upload close: " + closed.message,
                         pumped.bytesTransferred);
    }
    return pumped;
}

TransferResult downloadFile(SftpSession& sftp, const std::string& remotePath,
                            const std::string& localPath, std::uint64_t resumeOffset,
                            const TransferHooks& hooks, SftpCallOptions remoteOptions)
{
    if (localPath.empty() || remotePath.empty()) {
        return pumpError(ssh::kSshErrorCodeInternalError, "download rejected: empty path", 0);
    }
    SftpAttrs remoteAttrs;
    const SftpResult statted = sftp.stat(remotePath, true, remoteAttrs, remoteOptions);
    if (!statted.ok) {
        return pumpError(statted.code, "download stat: " + statted.message, 0);
    }
    const std::uint64_t remoteSize = remoteAttrs.hasSize ? remoteAttrs.size : 0;
    if (remoteAttrs.hasSize && resumeOffset > remoteSize) {
        return pumpError(ssh::kSshErrorCodeSftpTransferFailed,
                         "download rejected: resume offset beyond remote size", 0);
    }
    std::unique_ptr<SftpSession::File> remote;
    const SftpResult opened =
        sftp.openFile(remotePath, SftpSession::toOpenFlags(SftpSession::OpenFlags::Read), 0,
                      remote, remoteOptions);
    if (!opened.ok) {
        return pumpError(opened.code, "download open: " + opened.message, 0);
    }
    SftpFileReader reader(*remote, remoteOptions);
    std::string error;
    if (resumeOffset > 0 && !reader.seek(resumeOffset, error)) {
        const SftpResult closed = remote->close(remoteOptions);
        if (!closed.ok) {
            return pumpError(closed.code, "download seek: " + error + " (close: " +
                                              closed.message + ")",
                             0);
        }
        return pumpError(ssh::kSshErrorCodeSftpTransferFailed, "download seek: " + error, 0);
    }
    LocalFileWriter local;
    if (!local.open(localPath, resumeOffset == 0, error)) {
        const SftpResult closed = remote->close(remoteOptions);
        if (!closed.ok) {
            return pumpError(closed.code, "download local open: " + error + " (close: " +
                                              closed.message + ")",
                             0);
        }
        return pumpError(ssh::kSshErrorCodeSftpTransferFailed, "download local open: " + error,
                         0);
    }
    if (resumeOffset > 0 && !local.seek(resumeOffset, error)) {
        const SftpResult closed = remote->close(remoteOptions);
        if (!closed.ok) {
            return pumpError(closed.code, "download local seek: " + error + " (close: " +
                                              closed.message + ")",
                             0);
        }
        return pumpError(ssh::kSshErrorCodeSftpTransferFailed,
                         "download local seek: " + error, 0);
    }
    const std::uint64_t hint =
        remoteAttrs.hasSize ? remoteSize - resumeOffset : 0;
    const TransferResult pumped = pumpTransfer(reader, local, hint, hooks);
    const SftpResult closed = remote->close(remoteOptions);
    if (!pumped.ok) {
        if (!closed.ok) {
            TransferResult result = pumped;
            result.message += " (close: " + closed.message + ")";
            return result;
        }
        return pumped;
    }
    if (!closed.ok) {
        return pumpError(closed.code, "download close: " + closed.message,
                         pumped.bytesTransferred);
    }
    return pumped;
}

} // namespace sftp
} // namespace sshclient
