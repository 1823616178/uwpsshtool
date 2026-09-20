#include "pch.h"
#include "Bridge/SftpSession.h"

#include "Bridge/BridgeUtil.h"
#include "Bridge/SshSession.h"

#include "sftp/sftp_session.h"
#include "ssh/error_codes.h"

#include <algorithm>
#include <cstdint>
#include <string>
#include <vector>

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            namespace sftp = sshclient::sftp;
            namespace ssh = sshclient::ssh;
            using concurrency::task;
            using Windows::Foundation::Collections::IVectorView;
            using Windows::Foundation::IAsyncOperation;
            using Windows::Security::Cryptography::CryptographicBuffer;
            using Windows::Storage::Streams::IBuffer;

            namespace
            {
                // F02：各调用的超时（core SftpCallOptions，与 SshSession 侧一致）。
                constexpr unsigned kSftpTimeoutMs = 15000;
                // §11.1 传输分块：每块一次 SFTP 包；C# 泵恒用此大小。
                constexpr unsigned kSftpChunkBytes = 32u * 1024u;

                SftpEntryKind MapKind(sftp::SftpEntryType type)
                {
                    switch (type)
                    {
                    case sftp::SftpEntryType::File:
                        return SftpEntryKind::File;
                    case sftp::SftpEntryType::Directory:
                        return SftpEntryKind::Directory;
                    case sftp::SftpEntryType::Symlink:
                        return SftpEntryKind::Symlink;
                    case sftp::SftpEntryType::Unknown:
                        return SftpEntryKind::Unknown;
                    default:
                        return SftpEntryKind::Other; // fifo/socket/device
                    }
                }

                SftpEntry^ ToEntry(const sftp::SftpEntry& core, const std::string& fullPath)
                {
                    const bool hasSize = core.attrs.hasSize;
                    const bool hasPermissions = core.attrs.hasPermissions;
                    const bool hasMtime = core.attrs.hasTimes;
                    return ref new SftpEntry(
                        ToPlatform(core.name), ToPlatform(fullPath), MapKind(core.type),
                        hasSize, hasSize ? core.attrs.size : 0,
                        hasPermissions,
                        hasPermissions ? static_cast<int>(core.attrs.permissions) : 0,
                        hasMtime,
                        hasMtime ? static_cast<int64>(core.attrs.mtime) : 0,
                        ToPlatform(core.linkTarget));
                }

                // core 诊断文本（英文短句，不含敏感材料）。
                Platform::String^ FailMessage(int code)
                {
                    if (code == ssh::kSshErrorCodeNone)
                    {
                        return ref new Platform::String(L"");
                    }
                    return ToPlatform("sftp error " + std::to_string(code));
                }

                std::string BaseNameOf(const std::string& path)
                {
                    const size_t slash = path.find_last_of('/');
                    if (slash == std::string::npos)
                    {
                        return path;
                    }
                    if (slash + 1 >= path.size())
                    {
                        return std::string();
                    }
                    return path.substr(slash + 1);
                }
            }

            // ---------------------------------------------------------------- 结果类型

            SftpEntry::SftpEntry(Platform::String^ name, Platform::String^ fullPath,
                                 SftpEntryKind kind, bool hasSize, uint64 size,
                                 bool hasPermissions, int permissions,
                                 bool hasMtime, int64 mtime, Platform::String^ linkTarget)
                : name_(name), fullPath_(fullPath), kind_(kind), hasSize_(hasSize), size_(size),
                  hasPermissions_(hasPermissions), permissions_(permissions),
                  hasMtime_(hasMtime), mtime_(mtime), linkTarget_(linkTarget)
            {
            }

            Platform::String^ SftpEntry::Name::get() { return name_; }
            Platform::String^ SftpEntry::FullPath::get() { return fullPath_; }
            SftpEntryKind SftpEntry::Kind::get() { return kind_; }
            bool SftpEntry::HasSize::get() { return hasSize_; }
            uint64 SftpEntry::Size::get() { return size_; }
            bool SftpEntry::HasPermissions::get() { return hasPermissions_; }
            int SftpEntry::Permissions::get() { return permissions_; }
            bool SftpEntry::HasMtime::get() { return hasMtime_; }
            int64 SftpEntry::Mtime::get() { return mtime_; }
            Platform::String^ SftpEntry::LinkTarget::get() { return linkTarget_; }

            SftpResult::SftpResult(int code, Platform::String^ message)
                : code_(code), message_(message)
            {
            }

            int SftpResult::Code::get() { return code_; }
            Platform::String^ SftpResult::Message::get() { return message_; }

            SftpListResult::SftpListResult(int code, Platform::String^ message,
                                           IVectorView<SftpEntry^>^ entries)
                : code_(code), message_(message), entries_(entries)
            {
            }

            int SftpListResult::Code::get() { return code_; }
            Platform::String^ SftpListResult::Message::get() { return message_; }
            IVectorView<SftpEntry^>^ SftpListResult::Entries::get() { return entries_; }

            SftpEntryResult::SftpEntryResult(int code, Platform::String^ message, SftpEntry^ entry)
                : code_(code), message_(message), entry_(entry)
            {
            }

            int SftpEntryResult::Code::get() { return code_; }
            Platform::String^ SftpEntryResult::Message::get() { return message_; }
            SftpEntry^ SftpEntryResult::Entry::get() { return entry_; }

            SftpTextResult::SftpTextResult(int code, Platform::String^ message,
                                           Platform::String^ text)
                : code_(code), message_(message), text_(text)
            {
            }

            int SftpTextResult::Code::get() { return code_; }
            Platform::String^ SftpTextResult::Message::get() { return message_; }
            Platform::String^ SftpTextResult::Text::get() { return text_; }

            SftpDataResult::SftpDataResult(int code, Platform::String^ message, IBuffer^ data)
                : code_(code), message_(message), data_(data)
            {
            }

            int SftpDataResult::Code::get() { return code_; }
            Platform::String^ SftpDataResult::Message::get() { return message_; }
            IBuffer^ SftpDataResult::Data::get() { return data_; }

            SftpFileResult::SftpFileResult(int code, Platform::String^ message, int fileId)
                : code_(code), message_(message), fileId_(fileId)
            {
            }

            int SftpFileResult::Code::get() { return code_; }
            Platform::String^ SftpFileResult::Message::get() { return message_; }
            int SftpFileResult::FileId::get() { return fileId_; }

            // ---------------------------------------------------------------- 会话句柄

            SftpSession::SftpSession(SshSession^ owner) : owner_(owner)
            {
                if (owner == nullptr)
                {
                    throw ref new Platform::NullReferenceException();
                }
            }

            SftpSession::~SftpSession() { Close(); }

            bool SftpSession::IsOpen::get()
            {
                SshSession^ owner = owner_;
                if (owner == nullptr || closed_.load())
                {
                    return false;
                }
                return owner->SftpIsOpen();
            }

            void SftpSession::Close()
            {
                if (closed_.exchange(true))
                {
                    return; // 幂等
                }
                SshSession^ owner = owner_;
                if (owner != nullptr)
                {
                    owner->SftpClose(); // owner 存活由 hat 保证；core 侧同样幂等
                }
            }

            void SftpSession::Cancel()
            {
                SshSession^ owner = owner_;
                if (owner != nullptr)
                {
                    owner->SftpCancel();
                }
            }

            IAsyncOperation<int>^ SftpSession::OpenAsync()
            {
                SshSession^ owner = owner_;
                return concurrency::create_async([owner]() -> task<int> {
                    if (owner == nullptr)
                    {
                        return concurrency::task_from_result(ssh::kSshErrorCodeInternalError);
                    }
                    return concurrency::task_from_result(owner->SftpOpen(kSftpTimeoutMs));
                });
            }

            IAsyncOperation<SftpListResult^>^ SftpSession::ListDirAsync(Platform::String^ path)
            {
                SshSession^ owner = owner_;
                return concurrency::create_async([owner, dir = ToUtf8(path)]() -> SftpListResult^ {
                    if (owner == nullptr)
                    {
                        return ref new SftpListResult(ssh::kSshErrorCodeInternalError,
                                                      FailMessage(ssh::kSshErrorCodeInternalError),
                                                      nullptr);
                    }
                    std::vector<sftp::SftpEntry> entries;
                    const int code = owner->SftpListDir(dir, entries, kSftpTimeoutMs);
                    auto view = ref new Platform::Collections::Vector<SftpEntry^>();
                    if (code == ssh::kSshErrorCodeNone)
                    {
                        for (const auto& core : entries)
                        {
                            view->Append(ToEntry(core, sftp::joinRemotePath(dir, core.name)));
                        }
                    }
                    return ref new SftpListResult(code, FailMessage(code), view->GetView());
                });
            }

            IAsyncOperation<SftpEntryResult^>^ SftpSession::StatAsync(Platform::String^ path,
                                                                      bool followSymlink)
            {
                SshSession^ owner = owner_;
                return concurrency::create_async(
                    [owner, full = ToUtf8(path), followSymlink]() -> SftpEntryResult^ {
                        if (owner == nullptr)
                        {
                            return ref new SftpEntryResult(
                                ssh::kSshErrorCodeInternalError,
                                FailMessage(ssh::kSshErrorCodeInternalError), nullptr);
                        }
                        sftp::SftpAttrs attrs;
                        const int code =
                            owner->SftpStat(full, followSymlink, attrs, kSftpTimeoutMs);
                        SftpEntry^ entry = nullptr;
                        if (code == ssh::kSshErrorCodeNone)
                        {
                            sftp::SftpEntry core;
                            core.name = BaseNameOf(full);
                            core.type = sftp::entryTypeFromPermissions(
                                attrs.permissions, attrs.hasPermissions);
                            core.attrs = attrs;
                            entry = ToEntry(core, full);
                        }
                        return ref new SftpEntryResult(code, FailMessage(code), entry);
                    });
            }

            IAsyncOperation<SftpTextResult^>^ SftpSession::ReadLinkAsync(Platform::String^ path)
            {
                SshSession^ owner = owner_;
                return concurrency::create_async([owner, full = ToUtf8(path)]() -> SftpTextResult^ {
                    if (owner == nullptr)
                    {
                        return ref new SftpTextResult(ssh::kSshErrorCodeInternalError,
                                                      FailMessage(ssh::kSshErrorCodeInternalError),
                                                      ref new Platform::String(L""));
                    }
                    std::string target;
                    const int code = owner->SftpReadLink(full, target, kSftpTimeoutMs);
                    return ref new SftpTextResult(code, FailMessage(code), ToPlatform(target));
                });
            }

            IAsyncOperation<SftpResult^>^ SftpSession::MakeDirAsync(Platform::String^ path, int mode)
            {
                SshSession^ owner = owner_;
                return concurrency::create_async(
                    [owner, dir = ToUtf8(path), mode]() -> SftpResult^ {
                        if (owner == nullptr)
                        {
                            return ref new SftpResult(ssh::kSshErrorCodeInternalError,
                                                      FailMessage(ssh::kSshErrorCodeInternalError));
                        }
                        const int code = owner->SftpMakeDir(
                            dir, static_cast<unsigned>(mode), kSftpTimeoutMs);
                        return ref new SftpResult(code, FailMessage(code));
                    });
            }

            IAsyncOperation<SftpResult^>^ SftpSession::RenameAsync(Platform::String^ oldPath,
                                                                   Platform::String^ newPath)
            {
                SshSession^ owner = owner_;
                return concurrency::create_async(
                    [owner, from = ToUtf8(oldPath), to = ToUtf8(newPath)]() -> SftpResult^ {
                        if (owner == nullptr)
                        {
                            return ref new SftpResult(ssh::kSshErrorCodeInternalError,
                                                      FailMessage(ssh::kSshErrorCodeInternalError));
                        }
                        const int code = owner->SftpRename(from, to, kSftpTimeoutMs);
                        return ref new SftpResult(code, FailMessage(code));
                    });
            }

            IAsyncOperation<SftpResult^>^ SftpSession::DeleteFileAsync(Platform::String^ path)
            {
                SshSession^ owner = owner_;
                return concurrency::create_async([owner, full = ToUtf8(path)]() -> SftpResult^ {
                    if (owner == nullptr)
                    {
                        return ref new SftpResult(ssh::kSshErrorCodeInternalError,
                                                  FailMessage(ssh::kSshErrorCodeInternalError));
                    }
                    const int code = owner->SftpRemoveFile(full, kSftpTimeoutMs);
                    return ref new SftpResult(code, FailMessage(code));
                });
            }

            IAsyncOperation<SftpResult^>^ SftpSession::RemoveDirAsync(Platform::String^ path)
            {
                SshSession^ owner = owner_;
                return concurrency::create_async([owner, full = ToUtf8(path)]() -> SftpResult^ {
                    if (owner == nullptr)
                    {
                        return ref new SftpResult(ssh::kSshErrorCodeInternalError,
                                                  FailMessage(ssh::kSshErrorCodeInternalError));
                    }
                    const int code = owner->SftpRemoveDir(full, kSftpTimeoutMs);
                    return ref new SftpResult(code, FailMessage(code));
                });
            }

            IAsyncOperation<SftpResult^>^ SftpSession::SetPermissionsAsync(Platform::String^ path,
                                                                           int mode)
            {
                SshSession^ owner = owner_;
                return concurrency::create_async(
                    [owner, full = ToUtf8(path), mode]() -> SftpResult^ {
                        if (owner == nullptr)
                        {
                            return ref new SftpResult(ssh::kSshErrorCodeInternalError,
                                                      FailMessage(ssh::kSshErrorCodeInternalError));
                        }
                        const int code = owner->SftpSetPermissions(
                            full, static_cast<unsigned>(mode), kSftpTimeoutMs);
                        return ref new SftpResult(code, FailMessage(code));
                    });
            }

            IAsyncOperation<SftpFileResult^>^ SftpSession::OpenFileAsync(Platform::String^ path,
                                                                         SftpOpenFlags flags,
                                                                         int mode)
            {
                SshSession^ owner = owner_;
                return concurrency::create_async(
                    [owner, full = ToUtf8(path), flags, mode]() -> SftpFileResult^ {
                        if (owner == nullptr)
                        {
                            return ref new SftpFileResult(
                                ssh::kSshErrorCodeInternalError,
                                FailMessage(ssh::kSshErrorCodeInternalError), -1);
                        }
                        int fileId = -1;
                        const int code = owner->SftpOpenFile(
                            full, static_cast<unsigned long>(flags), static_cast<long>(mode),
                            fileId, kSftpTimeoutMs);
                        return ref new SftpFileResult(code, FailMessage(code), fileId);
                    });
            }

            IAsyncOperation<SftpDataResult^>^ SftpSession::ReadAsync(int fileId, unsigned count)
            {
                SshSession^ owner = owner_;
                return concurrency::create_async([owner, fileId, count]() -> SftpDataResult^ {
                    const unsigned capped = (std::min)(count, kSftpChunkBytes);
                    if (owner == nullptr)
                    {
                        return ref new SftpDataResult(ssh::kSshErrorCodeInternalError,
                                                      FailMessage(ssh::kSshErrorCodeInternalError),
                                                      nullptr);
                    }
                    if (capped == 0)
                    {
                        return ref new SftpDataResult(
                            ssh::kSshErrorCodeNone, FailMessage(ssh::kSshErrorCodeNone),
                            CryptographicBuffer::CreateFromByteArray(
                                ref new Platform::Array<uint8>(0)));
                    }
                    std::string chunk(capped, '\0');
                    size_t got = 0;
                    const int code =
                        owner->SftpReadFile(fileId, &chunk[0], chunk.size(), got, kSftpTimeoutMs);
                    IBuffer^ data = nullptr;
                    if (code == ssh::kSshErrorCodeNone)
                    {
                        auto bytes = ref new Platform::Array<uint8>(
                            static_cast<unsigned int>(got));
                        if (got > 0)
                        {
                            std::memcpy(bytes->Data, chunk.data(), got);
                        }
                        data = CryptographicBuffer::CreateFromByteArray(bytes);
                    }
                    return ref new SftpDataResult(code, FailMessage(code), data);
                });
            }

            IAsyncOperation<SftpResult^>^ SftpSession::WriteAsync(int fileId, IBuffer^ data)
            {
                SshSession^ owner = owner_;
                return concurrency::create_async([owner, fileId, data]() -> SftpResult^ {
                    if (owner == nullptr)
                    {
                        return ref new SftpResult(ssh::kSshErrorCodeInternalError,
                                                  FailMessage(ssh::kSshErrorCodeInternalError));
                    }
                    if (data == nullptr || data->Length == 0)
                    {
                        return ref new SftpResult(ssh::kSshErrorCodeNone,
                                                  FailMessage(ssh::kSshErrorCodeNone));
                    }
                    if (data->Length > kSftpChunkBytes)
                    {
                        return ref new SftpResult(ssh::kSshErrorCodeInternalError,
                                                  FailMessage(ssh::kSshErrorCodeInternalError));
                    }
                    Platform::Array<uint8>^ bytes = nullptr;
                    CryptographicBuffer::CopyToByteArray(data, &bytes);
                    const int code = owner->SftpWriteFile(
                        fileId, reinterpret_cast<const char*>(bytes->Data), bytes->Length,
                        kSftpTimeoutMs);
                    return ref new SftpResult(code, FailMessage(code));
                });
            }

            IAsyncOperation<SftpResult^>^ SftpSession::SeekAsync(int fileId, uint64 offset)
            {
                SshSession^ owner = owner_;
                return concurrency::create_async([owner, fileId, offset]() -> SftpResult^ {
                    if (owner == nullptr)
                    {
                        return ref new SftpResult(ssh::kSshErrorCodeInternalError,
                                                  FailMessage(ssh::kSshErrorCodeInternalError));
                    }
                    const int code = owner->SftpSeekFile(
                        fileId, static_cast<std::uint64_t>(offset), kSftpTimeoutMs);
                    return ref new SftpResult(code, FailMessage(code));
                });
            }

            IAsyncOperation<SftpResult^>^ SftpSession::CloseFileAsync(int fileId)
            {
                SshSession^ owner = owner_;
                return concurrency::create_async([owner, fileId]() -> SftpResult^ {
                    if (owner == nullptr)
                    {
                        return ref new SftpResult(ssh::kSshErrorCodeInternalError,
                                                  FailMessage(ssh::kSshErrorCodeInternalError));
                    }
                    const int code = owner->SftpCloseFile(fileId, kSftpTimeoutMs);
                    return ref new SftpResult(code, FailMessage(code));
                });
            }
        }
    }
}
