#pragma once

// F02：SFTP WinRT 桥（01-DESIGN.md §11.1；04-TASKS F02）。
//
// 分层：本类是薄句柄——core SftpSession 对象与远端文件句柄表由所属
// SshSession 拥有（见 Bridge/SshSession.h 的 SFTP 挂载点），与 core
// SshSession 同寿命、同 sessionMutex_ 串行；Shutdown 先关 SFTP 再释会话，
// 因此 owner 先 Dispose 后本对象再调用只会 fail-fast（500），不会悬空。
//
// 线程：所有异步方法经 concurrency::create_async 在后台线程执行阻塞式
// core 调用（F01 线程契约：禁止在会话 I/O 线程上调用）；事件无，进度由
// C# 泵按 200 ms 节流后上报（§11.1）。
// 失败语义：返回值/结果对象的 Code 为 N08 统一错误码（0 = 成功），
// Message 为 core 诊断文本（不含敏感材料：路径非凭据，可记录）。
// 生命周期：Close() 幂等；析构调 Close。v141 /ZW 不接受 !T() 终结器
// （C3941，见 SshSession.h），C# 侧必须 using/Dispose 确定性释放。

#include <atomic>

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            ref class SshSession;

            public enum class SftpEntryKind
            {
                Unknown = 0,
                File = 1,
                Directory = 2,
                Symlink = 3,
                Other = 4
            };

            // 与 core OpenFlags 数值一致（Read=1 … Exclusive=32），C# 侧按位或
            // 后传 int（WinRT 非 Flags 枚举不指定 underlying type）。
            public enum class SftpOpenFlags
            {
                Read = 1,
                Write = 2,
                Append = 4,
                Create = 8,
                Truncate = 16,
                Exclusive = 32
            };

            public ref class SftpEntry sealed
            {
            public:
                property Platform::String^ Name { Platform::String^ get(); }
                property Platform::String^ FullPath { Platform::String^ get(); }
                property SftpEntryKind Kind { SftpEntryKind get(); }
                property bool HasSize { bool get(); }
                property uint64 Size { uint64 get(); }
                property bool HasPermissions { bool get(); }
                property int Permissions { int get(); }
                property bool HasMtime { bool get(); }
                property int64 Mtime { int64 get(); } // unix 秒
                property Platform::String^ LinkTarget { Platform::String^ get(); }

            internal:
                SftpEntry(Platform::String^ name, Platform::String^ fullPath,
                          SftpEntryKind kind, bool hasSize, uint64 size,
                          bool hasPermissions, int permissions,
                          bool hasMtime, int64 mtime, Platform::String^ linkTarget);

            private:
                Platform::String^ name_;
                Platform::String^ fullPath_;
                SftpEntryKind kind_;
                bool hasSize_;
                uint64 size_;
                bool hasPermissions_;
                int permissions_;
                bool hasMtime_;
                int64 mtime_;
                Platform::String^ linkTarget_;
            };

            // 简单操作的结果（Code 0 = 成功）。
            public ref class SftpResult sealed
            {
            public:
                property int Code { int get(); }
                property Platform::String^ Message { Platform::String^ get(); }

            internal:
                SftpResult(int code, Platform::String^ message);

            private:
                int code_;
                Platform::String^ message_;
            };

            public ref class SftpListResult sealed
            {
            public:
                property int Code { int get(); }
                property Platform::String^ Message { Platform::String^ get(); }
                property Windows::Foundation::Collections::IVectorView<SftpEntry^>^ Entries {
                    Windows::Foundation::Collections::IVectorView<SftpEntry^>^ get();
                }

            internal:
                SftpListResult(int code, Platform::String^ message,
                               Windows::Foundation::Collections::IVectorView<SftpEntry^>^ entries);

            private:
                int code_;
                Platform::String^ message_;
                Windows::Foundation::Collections::IVectorView<SftpEntry^>^ entries_;
            };

            public ref class SftpEntryResult sealed
            {
            public:
                property int Code { int get(); }
                property Platform::String^ Message { Platform::String^ get(); }
                property SftpEntry^ Entry { SftpEntry^ get(); } // 失败时为 nullptr

            internal:
                SftpEntryResult(int code, Platform::String^ message, SftpEntry^ entry);

            private:
                int code_;
                Platform::String^ message_;
                SftpEntry^ entry_;
            };

            public ref class SftpTextResult sealed
            {
            public:
                property int Code { int get(); }
                property Platform::String^ Message { Platform::String^ get(); }
                property Platform::String^ Text { Platform::String^ get(); }

            internal:
                SftpTextResult(int code, Platform::String^ message, Platform::String^ text);

            private:
                int code_;
                Platform::String^ message_;
                Platform::String^ text_;
            };

            // 块读结果：Data 为空（Length==0）表示 EOF。
            public ref class SftpDataResult sealed
            {
            public:
                property int Code { int get(); }
                property Platform::String^ Message { Platform::String^ get(); }
                property Windows::Storage::Streams::IBuffer^ Data {
                    Windows::Storage::Streams::IBuffer^ get();
                }

            internal:
                SftpDataResult(int code, Platform::String^ message,
                               Windows::Storage::Streams::IBuffer^ data);

            private:
                int code_;
                Platform::String^ message_;
                Windows::Storage::Streams::IBuffer^ data_;
            };

            public ref class SftpFileResult sealed
            {
            public:
                property int Code { int get(); }
                property Platform::String^ Message { Platform::String^ get(); }
                property int FileId { int get(); } // 失败时为 -1

            internal:
                SftpFileResult(int code, Platform::String^ message, int fileId);

            private:
                int code_;
                Platform::String^ message_;
                int fileId_;
            };

            public ref class SftpSession sealed
            {
            public:
                SftpSession(SshSession^ owner);
                virtual ~SftpSession(); // C++/CX：public 析构必须 virtual（Dispose）

                property bool IsOpen { bool get(); }

                Windows::Foundation::IAsyncOperation<int>^ OpenAsync();
                void Close();
                // 中断飞行中的块调用（consume-once，见 SshSession.h）。
                void Cancel();

                Windows::Foundation::IAsyncOperation<SftpListResult^>^ ListDirAsync(
                    Platform::String^ path);
                Windows::Foundation::IAsyncOperation<SftpEntryResult^>^ StatAsync(
                    Platform::String^ path, bool followSymlink);
                Windows::Foundation::IAsyncOperation<SftpTextResult^>^ ReadLinkAsync(
                    Platform::String^ path);
                Windows::Foundation::IAsyncOperation<SftpResult^>^ MakeDirAsync(
                    Platform::String^ path, int mode);
                Windows::Foundation::IAsyncOperation<SftpResult^>^ RenameAsync(
                    Platform::String^ oldPath, Platform::String^ newPath);
                Windows::Foundation::IAsyncOperation<SftpResult^>^ DeleteFileAsync(
                    Platform::String^ path);
                Windows::Foundation::IAsyncOperation<SftpResult^>^ RemoveDirAsync(
                    Platform::String^ path);
                Windows::Foundation::IAsyncOperation<SftpResult^>^ SetPermissionsAsync(
                    Platform::String^ path, int mode);

                Windows::Foundation::IAsyncOperation<SftpFileResult^>^ OpenFileAsync(
                    Platform::String^ path, SftpOpenFlags flags, int mode);
                // count 按 32 KiB 封顶（§11.1 传输分块）。
                Windows::Foundation::IAsyncOperation<SftpDataResult^>^ ReadAsync(
                    int fileId, unsigned count);
                Windows::Foundation::IAsyncOperation<SftpResult^>^ WriteAsync(
                    int fileId, Windows::Storage::Streams::IBuffer^ data);
                Windows::Foundation::IAsyncOperation<SftpResult^>^ SeekAsync(
                    int fileId, uint64 offset);
                Windows::Foundation::IAsyncOperation<SftpResult^>^ CloseFileAsync(int fileId);

            private:
                SshSession^ owner_;
                std::atomic<bool> closed_{false};
            };
        }
    }
}
