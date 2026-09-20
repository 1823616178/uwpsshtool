using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SshTool.Core.Common;

namespace SshTool.Core.Sftp
{
    // F02：SFTP 传输常量（01-DESIGN.md §11.1）。
    public static class SftpConstants
    {
        // native 线程分块：每块一次 SFTP READ/WRITE 包（与 OpenSSH 窗口一致）。
        public const int ChunkSize = 32 * 1024;

        // 进度事件节流：UI 最多每 200 ms 收到一次（完成时强制最后一次）。
        public const int ProgressThrottleMs = 200;

        // 新建远端文件的默认 mode（0644，与 native uploadFile remoteMode 默认一致；
        // C# 无八进制字面量，0644=420=0x1A4）。
        public const int DefaultRemoteMode = 0x1A4;
    }

    // F02：SFTP 进度（IProgress<T> 负载）。BytesTotal 为 -1 表示未知。
    public sealed class SftpProgress
    {
        public SftpProgress(long bytesDone, long bytesTotal)
        {
            BytesDone = bytesDone;
            BytesTotal = bytesTotal;
        }

        public long BytesDone { get; }
        public long BytesTotal { get; }
    }

    // F02：SFTP 调用结果基类（Ok=false 时 Code 为 SshErrorCode，Message 为诊断文本）。
    public class SftpResult
    {
        public SftpResult(bool ok, SshErrorCode code, string message)
        {
            Ok = ok;
            Code = code;
            Message = message ?? string.Empty;
        }

        public bool Ok { get; }
        public SshErrorCode Code { get; }
        public string Message { get; }

        public static SftpResult Success()
        {
            return new SftpResult(true, SshErrorCode.None, string.Empty);
        }

        public static SftpResult Fail(SshErrorCode code, string message)
        {
            return new SftpResult(false, code, message ?? string.Empty);
        }
    }

    public sealed class SftpDirResult : SftpResult
    {
        public SftpDirResult(bool ok, SshErrorCode code, string message,
                             IReadOnlyList<RemoteEntry> entries)
            : base(ok, code, message)
        {
            Entries = entries;
        }

        public IReadOnlyList<RemoteEntry> Entries { get; }

        public static SftpDirResult Success(IReadOnlyList<RemoteEntry> entries)
        {
            return new SftpDirResult(true, SshErrorCode.None, string.Empty, entries);
        }

        public static new SftpDirResult Fail(SshErrorCode code, string message)
        {
            return new SftpDirResult(false, code, message, null);
        }
    }

    public sealed class SftpEntryResult : SftpResult
    {
        public SftpEntryResult(bool ok, SshErrorCode code, string message, RemoteEntry entry)
            : base(ok, code, message)
        {
            Entry = entry;
        }

        public RemoteEntry Entry { get; }

        public static SftpEntryResult Success(RemoteEntry entry)
        {
            return new SftpEntryResult(true, SshErrorCode.None, string.Empty, entry);
        }

        public static new SftpEntryResult Fail(SshErrorCode code, string message)
        {
            return new SftpEntryResult(false, code, message, null);
        }
    }

    public sealed class SftpTextResult : SftpResult
    {
        public SftpTextResult(bool ok, SshErrorCode code, string message, string text)
            : base(ok, code, message)
        {
            Text = text ?? string.Empty;
        }

        public string Text { get; }

        public static SftpTextResult Success(string text)
        {
            return new SftpTextResult(true, SshErrorCode.None, string.Empty, text);
        }

        public static new SftpTextResult Fail(SshErrorCode code, string message)
        {
            return new SftpTextResult(false, code, message, string.Empty);
        }
    }

    // F02：SFTP 客户端抽象（N09b ISshSession 模式：Core 只定义接口，
    // App 层 NativeSftpClient 注入原生实现；单测用假实现）。
    //
    // 线程：所有方法为异步（native 经 create_async 在后台线程执行），调用方
    // 全程 ConfigureAwait(false)，绝不回 UI 线程。
    // 路径：实现入口必须先 RemotePath.Normalize（桥层同样归一，双保险）。
    // 本地流：Upload 要求 source 可读、可定位（Position/Length）；
    // Download 要求 destination 可写、可定位；resumeOffset>0 时双方从该字节续写。
    // 取消：CancellationToken 在块间裁决（32 KiB 一块），并经桥 Cancel 中断飞行块。
    // 日志脱敏：实现只记方法名 + 码 + 字节数 + 耗时，不记路径与文件内容。
    public interface ISftpClient : IDisposable
    {
        // 打开 SFTP 子系统（复用已认证连接，不重复认证；幂等）。
        Task<SftpResult> OpenAsync();

        // 关闭子系统并代关未关的远端句柄（幂等）。
        void Close();

        Task<SftpDirResult> ListDirectoryAsync(string path);
        Task<SftpEntryResult> StatAsync(string path, bool followSymlink);
        Task<SftpTextResult> ReadLinkAsync(string path);
        Task<SftpResult> CreateDirectoryAsync(string path, int mode);
        Task<SftpResult> RenameAsync(string oldPath, string newPath);
        Task<SftpResult> DeleteFileAsync(string path);
        Task<SftpResult> RemoveDirectoryAsync(string path);
        Task<SftpResult> SetPermissionsAsync(string path, int mode);

        Task<SftpResult> UploadAsync(string remotePath, Stream source,
                                     IProgress<SftpProgress> progress,
                                     CancellationToken cancellationToken,
                                     long resumeOffset, int remoteMode);
        Task<SftpResult> DownloadAsync(string remotePath, Stream destination,
                                       IProgress<SftpProgress> progress,
                                       CancellationToken cancellationToken,
                                       long resumeOffset);
    }
}
