using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Sftp;

namespace SshTool.Core.Tests.Fakes
{
    // F02：ISftpClient 的内存假实现（供 TransferQueue 集成式单测与 F03 ViewModel 单测）。
    // 文件系统为内存字典（远端规范路径→字节）；传输按 ChunkSize 分块推进并上报进度；
    // 每个方法的失败可通过 *Result 预设；调用记录在 Calls。
    public sealed class FakeSftpClient : ISftpClient
    {
        public readonly Dictionary<string, byte[]> Files =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);

        public SftpErrorPreset OpenResult;
        public SftpErrorPreset DefaultOpResult;
        public Func<TransferDirection, string, SftpErrorPreset> TransferResultProvider;
        public int ProgressReports;
        public bool IsOpen { get; private set; }

        public readonly List<string> Calls = new List<string>();
        public int DisposeCount;

        public struct SftpErrorPreset
        {
            public bool Fail;
            public SshErrorCode Code;
        }

        public Task<SftpResult> OpenAsync()
        {
            Calls.Add("Open");
            if (OpenResult.Fail)
            {
                return Task.FromResult(SftpResult.Fail(OpenResult.Code, "open"));
            }
            IsOpen = true;
            return Task.FromResult(SftpResult.Success());
        }

        public void Close()
        {
            Calls.Add("Close");
            IsOpen = false;
        }

        public Task<SftpDirResult> ListDirectoryAsync(string path)
        {
            Calls.Add("List:" + path);
            if (DefaultOpResult.Fail)
            {
                return Task.FromResult(SftpDirResult.Fail(DefaultOpResult.Code, "op"));
            }
            string dir = RemotePath.Normalize(path);
            var entries = new List<RemoteEntry>();
            foreach (KeyValuePair<string, byte[]> pair in Files)
            {
                if (RemotePath.GetDirectoryName(pair.Key) == dir)
                {
                    entries.Add(new RemoteEntry(RemotePath.GetFileName(pair.Key), pair.Key,
                        RemoteEntryType.File, pair.Value.Length, 0x1A4,
                        DateTime.MinValue, string.Empty));
                }
            }
            return Task.FromResult(SftpDirResult.Success(entries.AsReadOnly()));
        }

        public Task<SftpEntryResult> StatAsync(string path, bool followSymlink)
        {
            Calls.Add("Stat:" + path);
            if (DefaultOpResult.Fail)
            {
                return Task.FromResult(SftpEntryResult.Fail(DefaultOpResult.Code, "op"));
            }
            string normalized = RemotePath.Normalize(path);
            byte[] data;
            if (!Files.TryGetValue(normalized, out data))
            {
                return Task.FromResult(SftpEntryResult.Fail(SshErrorCode.SftpNoSuchFile, "x"));
            }
            return Task.FromResult(SftpEntryResult.Success(new RemoteEntry(
                RemotePath.GetFileName(normalized), normalized, RemoteEntryType.File,
                data.Length, 0x1A4, DateTime.MinValue, string.Empty)));
        }

        public Task<SftpTextResult> ReadLinkAsync(string path)
        {
            Calls.Add("ReadLink:" + path);
            return Task.FromResult(SftpTextResult.Fail(SshErrorCode.SftpNoSuchFile, "x"));
        }

        public Task<SftpResult> CreateDirectoryAsync(string path, int mode)
        {
            Calls.Add("MkDir:" + path);
            return Task.FromResult(DefaultOpResult.Fail
                ? SftpResult.Fail(DefaultOpResult.Code, "op") : SftpResult.Success());
        }

        public Task<SftpResult> RenameAsync(string oldPath, string newPath)
        {
            Calls.Add("Rename:" + oldPath);
            return Task.FromResult(DefaultOpResult.Fail
                ? SftpResult.Fail(DefaultOpResult.Code, "op") : SftpResult.Success());
        }

        public Task<SftpResult> DeleteFileAsync(string path)
        {
            Calls.Add("Delete:" + path);
            return Task.FromResult(DefaultOpResult.Fail
                ? SftpResult.Fail(DefaultOpResult.Code, "op") : SftpResult.Success());
        }

        public Task<SftpResult> RemoveDirectoryAsync(string path)
        {
            Calls.Add("RmDir:" + path);
            return Task.FromResult(DefaultOpResult.Fail
                ? SftpResult.Fail(DefaultOpResult.Code, "op") : SftpResult.Success());
        }

        public Task<SftpResult> SetPermissionsAsync(string path, int mode)
        {
            Calls.Add("Chmod:" + path);
            return Task.FromResult(DefaultOpResult.Fail
                ? SftpResult.Fail(DefaultOpResult.Code, "op") : SftpResult.Success());
        }

        public async Task<SftpResult> UploadAsync(string remotePath, Stream source,
                                                  IProgress<SftpProgress> progress,
                                                  CancellationToken cancellationToken,
                                                  long resumeOffset, int remoteMode)
        {
            Calls.Add("Upload:" + remotePath);
            SftpErrorPreset preset = TransferResultProvider != null
                ? TransferResultProvider(TransferDirection.Upload, remotePath)
                : default(SftpErrorPreset);
            if (preset.Fail)
            {
                return SftpResult.Fail(preset.Code, "xfer");
            }
            string normalized = RemotePath.Normalize(remotePath);
            long total = source.Length;
            var buffer = new byte[SftpConstants.ChunkSize];
            long done = resumeOffset;
            source.Position = resumeOffset;
            int read;
            while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                done += read;
                if (progress != null)
                {
                    progress.Report(new SftpProgress(done, total));
                    ProgressReports++;
                }
            }
            var stored = new byte[(int)done];
            source.Position = 0;
            int got = 0;
            while (got < stored.Length)
            {
                int n = await source.ReadAsync(stored, got, stored.Length - got, cancellationToken)
                    .ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }
                got += n;
            }
            Files[normalized] = stored;
            return SftpResult.Success();
        }

        public async Task<SftpResult> DownloadAsync(string remotePath, Stream destination,
                                                    IProgress<SftpProgress> progress,
                                                    CancellationToken cancellationToken,
                                                    long resumeOffset)
        {
            Calls.Add("Download:" + remotePath);
            SftpErrorPreset preset = TransferResultProvider != null
                ? TransferResultProvider(TransferDirection.Download, remotePath)
                : default(SftpErrorPreset);
            if (preset.Fail)
            {
                return SftpResult.Fail(preset.Code, "xfer");
            }
            string normalized = RemotePath.Normalize(remotePath);
            byte[] data;
            if (!Files.TryGetValue(normalized, out data))
            {
                return SftpResult.Fail(SshErrorCode.SftpNoSuchFile, "x");
            }
            destination.Position = resumeOffset;
            long done = resumeOffset;
            int offset = (int)resumeOffset;
            while (offset < data.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = Math.Min(SftpConstants.ChunkSize, data.Length - offset);
                await destination.WriteAsync(data, offset, n, cancellationToken).ConfigureAwait(false);
                offset += n;
                done += n;
                if (progress != null)
                {
                    progress.Report(new SftpProgress(done, data.Length));
                    ProgressReports++;
                }
            }
            return SftpResult.Success();
        }

        public void Dispose()
        {
            DisposeCount++;
            Close();
        }
    }
}
