using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Sftp;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;
using NativeBridge = SshTool.Native.Bridge;

namespace SshTool.App.Platform
{
    // F02：ISftpClient 的原生适配（01-DESIGN.md §4.1：Core 不引用 Native，
    // WinRT 类型转换与 IAsyncOperation→Task 都在本层；模式见 NativeKeyTool）。
    //
    // 会话绑定：一个实例绑定一个 NativeSshSession 的已认证连接（SFTP 子系统
    // 挂在该连接上，不重复认证；§11.1 建议 SFTP 独占一条连接——F03 建专用会话）。
    // 传输泵：32 KiB 一块（SftpConstants.ChunkSize），块间检查 ct 并经桥 Cancel
    // 中断飞行块；进度按 ProgressThrottleMs(200 ms)节流，收尾强制最后一次。
    // 线程：全程 ConfigureAwait(false)，绝不回 UI 线程。
    // 日志脱敏：只记方法名 + 码 + 字节数 + 耗时；不记路径与文件内容。
    public sealed class NativeSftpClient : ISftpClient
    {
        private static readonly DateTime UnixEpoch =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly NativeBridge.SftpSession _sftp;
        private readonly ILogger _logger;
        private bool _disposed;

        public NativeSftpClient(NativeSshSession session, ILogger logger)
        {
            if (session == null)
            {
                throw new ArgumentNullException("session");
            }
            if (session.Native == null)
            {
                throw new ArgumentNullException("session");
            }
            _sftp = new NativeBridge.SftpSession(session.Native);
            _logger = logger;
        }

        public NativeSftpClient(NativeSshSession session)
            : this(session, null)
        {
        }

        public async Task<SftpResult> OpenAsync()
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                ThrowIfDisposed();
                int code = await _sftp.OpenAsync().AsTask().ConfigureAwait(false);
                Log("OpenAsync", code, 0, watch);
                return ToResult(code);
            }
            catch (Exception ex)
            {
                return Fail("OpenAsync", ex, watch);
            }
        }

        public void Close()
        {
            try
            {
                _sftp.Close(); // native 侧幂等
            }
            catch (Exception ex)
            {
                Log("Close", ex, watch: null);
            }
        }

        public async Task<SftpDirResult> ListDirectoryAsync(string path)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                ThrowIfDisposed();
                string dir = RemotePath.Normalize(path);
                NativeBridge.SftpListResult result =
                    await _sftp.ListDirAsync(dir).AsTask().ConfigureAwait(false);
                SshErrorCode code = (SshErrorCode)result.Code;
                Log("ListDirectoryAsync", result.Code, 0, watch);
                if (code != SshErrorCode.None || result.Entries == null)
                {
                    return SftpDirResult.Fail(code, result.Message ?? string.Empty);
                }
                var entries = new List<RemoteEntry>((int)result.Entries.Count);
                foreach (NativeBridge.SftpEntry entry in result.Entries)
                {
                    entries.Add(FromNative(entry, dir));
                }
                return SftpDirResult.Success(entries.AsReadOnly());
            }
            catch (Exception ex)
            {
                SftpResult fail = Fail("ListDirectoryAsync", ex, watch);
                return SftpDirResult.Fail(fail.Code, fail.Message);
            }
        }

        public async Task<SftpEntryResult> StatAsync(string path, bool followSymlink)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                ThrowIfDisposed();
                string full = RemotePath.Normalize(path);
                NativeBridge.SftpEntryResult result =
                    await _sftp.StatAsync(full, followSymlink).AsTask().ConfigureAwait(false);
                SshErrorCode code = (SshErrorCode)result.Code;
                Log("StatAsync", result.Code, 0, watch);
                if (code != SshErrorCode.None || result.Entry == null)
                {
                    return SftpEntryResult.Fail(code, result.Message ?? string.Empty);
                }
                return SftpEntryResult.Success(FromNative(result.Entry, full));
            }
            catch (Exception ex)
            {
                SftpResult fail = Fail("StatAsync", ex, watch);
                return SftpEntryResult.Fail(fail.Code, fail.Message);
            }
        }

        public async Task<SftpTextResult> ReadLinkAsync(string path)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                ThrowIfDisposed();
                string full = RemotePath.Normalize(path);
                NativeBridge.SftpTextResult result =
                    await _sftp.ReadLinkAsync(full).AsTask().ConfigureAwait(false);
                SshErrorCode code = (SshErrorCode)result.Code;
                Log("ReadLinkAsync", result.Code, 0, watch);
                if (code != SshErrorCode.None)
                {
                    return SftpTextResult.Fail(code, result.Message ?? string.Empty);
                }
                return SftpTextResult.Success(result.Text ?? string.Empty);
            }
            catch (Exception ex)
            {
                SftpResult fail = Fail("ReadLinkAsync", ex, watch);
                return SftpTextResult.Fail(fail.Code, fail.Message);
            }
        }

        public async Task<SftpResult> CreateDirectoryAsync(string path, int mode)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                ThrowIfDisposed();
                string full = RemotePath.Normalize(path);
                NativeBridge.SftpResult result =
                    await _sftp.MakeDirAsync(full, mode & PermissionBits.ModeMask)
                        .AsTask().ConfigureAwait(false);
                Log("CreateDirectoryAsync", result.Code, 0, watch);
                return ToResult(result);
            }
            catch (Exception ex)
            {
                return Fail("CreateDirectoryAsync", ex, watch);
            }
        }

        public async Task<SftpResult> RenameAsync(string oldPath, string newPath)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                ThrowIfDisposed();
                NativeBridge.SftpResult result = await _sftp.RenameAsync(
                    RemotePath.Normalize(oldPath), RemotePath.Normalize(newPath))
                    .AsTask().ConfigureAwait(false);
                Log("RenameAsync", result.Code, 0, watch);
                return ToResult(result);
            }
            catch (Exception ex)
            {
                return Fail("RenameAsync", ex, watch);
            }
        }

        public async Task<SftpResult> DeleteFileAsync(string path)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                ThrowIfDisposed();
                NativeBridge.SftpResult result = await _sftp
                    .DeleteFileAsync(RemotePath.Normalize(path)).AsTask().ConfigureAwait(false);
                Log("DeleteFileAsync", result.Code, 0, watch);
                return ToResult(result);
            }
            catch (Exception ex)
            {
                return Fail("DeleteFileAsync", ex, watch);
            }
        }

        public async Task<SftpResult> RemoveDirectoryAsync(string path)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                ThrowIfDisposed();
                NativeBridge.SftpResult result = await _sftp
                    .RemoveDirAsync(RemotePath.Normalize(path)).AsTask().ConfigureAwait(false);
                Log("RemoveDirectoryAsync", result.Code, 0, watch);
                return ToResult(result);
            }
            catch (Exception ex)
            {
                return Fail("RemoveDirectoryAsync", ex, watch);
            }
        }

        public async Task<SftpResult> SetPermissionsAsync(string path, int mode)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                ThrowIfDisposed();
                NativeBridge.SftpResult result = await _sftp.SetPermissionsAsync(
                    RemotePath.Normalize(path), mode & PermissionBits.ModeMask)
                    .AsTask().ConfigureAwait(false);
                Log("SetPermissionsAsync", result.Code, 0, watch);
                return ToResult(result);
            }
            catch (Exception ex)
            {
                return Fail("SetPermissionsAsync", ex, watch);
            }
        }

        public async Task<SftpResult> UploadAsync(string remotePath, Stream source,
                                                  IProgress<SftpProgress> progress,
                                                  CancellationToken cancellationToken,
                                                  long resumeOffset, int remoteMode)
        {
            Stopwatch watch = Stopwatch.StartNew();
            long done = resumeOffset;
            try
            {
                ThrowIfDisposed();
                string full = RemotePath.Normalize(remotePath);
                SftpResult invalid = ValidateTransferStream(source, resumeOffset);
                if (invalid != null)
                {
                    return invalid;
                }
                if (!source.CanRead)
                {
                    return SftpResult.Fail(SshErrorCode.InternalError, "source not readable");
                }
                long total = source.Length;
                source.Position = resumeOffset;
                int flags = (int)(NativeBridge.SftpOpenFlags.Write | NativeBridge.SftpOpenFlags.Create);
                if (resumeOffset == 0)
                {
                    flags |= (int)NativeBridge.SftpOpenFlags.Truncate;
                }
                NativeBridge.SftpFileResult opened = await _sftp.OpenFileAsync(
                    full, (NativeBridge.SftpOpenFlags)flags, remoteMode & PermissionBits.ModeMask)
                    .AsTask().ConfigureAwait(false);
                if (opened.Code != 0)
                {
                    Log("UploadAsync", opened.Code, 0, watch);
                    return SftpResult.Fail((SshErrorCode)opened.Code, opened.Message);
                }
                int fileId = opened.FileId;
                try
                {
                    if (resumeOffset > 0)
                    {
                        NativeBridge.SftpResult seeked =
                            await _sftp.SeekAsync(fileId, (ulong)resumeOffset)
                                .AsTask().ConfigureAwait(false);
                        if (seeked.Code != 0)
                        {
                            Log("UploadAsync", seeked.Code, done, watch);
                            return SftpResult.Fail((SshErrorCode)seeked.Code, seeked.Message);
                        }
                    }
                    var throttle = new ProgressThrottle(progress, total);
                    using (cancellationToken.Register(CancelInFlight))
                    {
                        byte[] chunk = new byte[SftpConstants.ChunkSize];
                        for (;;)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            int read = await source.ReadAsync(chunk, 0, chunk.Length,
                                cancellationToken).ConfigureAwait(false);
                            if (read == 0)
                            {
                                break;
                            }
                            byte[] slice = read == chunk.Length ? chunk : SubArray(chunk, read);
                            IBuffer winrt = CryptographicBuffer.CreateFromByteArray(slice);
                            Array.Clear(slice, 0, slice.Length);
                            NativeBridge.SftpResult written =
                                await _sftp.WriteAsync(fileId, winrt).AsTask()
                                    .ConfigureAwait(false);
                            if (written.Code != 0)
                            {
                                Log("UploadAsync", written.Code, done, watch);
                                return SftpResult.Fail(
                                    (SshErrorCode)written.Code, written.Message);
                            }
                            done += read;
                            throttle.Report(done, false);
                        }
                    }
                    throttle.Report(done, true);
                }
                finally
                {
                    await CloseRemoteFile(fileId).ConfigureAwait(false);
                }
                Log("UploadAsync", 0, done, watch);
                return SftpResult.Success();
            }
            catch (OperationCanceledException)
            {
                Log("UploadAsync", (int)SshErrorCode.SftpCancelled, done, watch);
                return SftpResult.Fail(SshErrorCode.SftpCancelled, string.Empty);
            }
            catch (Exception ex)
            {
                return Fail("UploadAsync", ex, watch);
            }
        }

        public async Task<SftpResult> DownloadAsync(string remotePath, Stream destination,
                                                    IProgress<SftpProgress> progress,
                                                    CancellationToken cancellationToken,
                                                    long resumeOffset)
        {
            Stopwatch watch = Stopwatch.StartNew();
            long done = resumeOffset;
            try
            {
                ThrowIfDisposed();
                string full = RemotePath.Normalize(remotePath);
                SftpResult invalid = ValidateTransferStream(destination, resumeOffset);
                if (invalid != null)
                {
                    return invalid;
                }
                if (!destination.CanWrite)
                {
                    return SftpResult.Fail(SshErrorCode.InternalError, "destination not writable");
                }
                // 总量：Stat 失败（不存在等）直接返回，不开空文件。
                NativeBridge.SftpEntryResult stat =
                    await _sftp.StatAsync(full, true).AsTask().ConfigureAwait(false);
                if (stat.Code != 0 || stat.Entry == null || !stat.Entry.HasSize)
                {
                    Log("DownloadAsync", stat.Code, 0, watch);
                    return SftpResult.Fail((SshErrorCode)stat.Code, stat.Message);
                }
                long total = (long)stat.Entry.Size;
                if (resumeOffset > total)
                {
                    return SftpResult.Fail(SshErrorCode.InternalError, "resume beyond EOF");
                }
                if (resumeOffset == 0)
                {
                    destination.SetLength(0);
                }
                destination.Position = resumeOffset;
                NativeBridge.SftpFileResult opened = await _sftp.OpenFileAsync(
                    full, NativeBridge.SftpOpenFlags.Read, 0).AsTask().ConfigureAwait(false);
                if (opened.Code != 0)
                {
                    Log("DownloadAsync", opened.Code, 0, watch);
                    return SftpResult.Fail((SshErrorCode)opened.Code, opened.Message);
                }
                int fileId = opened.FileId;
                try
                {
                    if (resumeOffset > 0)
                    {
                        NativeBridge.SftpResult seeked =
                            await _sftp.SeekAsync(fileId, (ulong)resumeOffset)
                                .AsTask().ConfigureAwait(false);
                        if (seeked.Code != 0)
                        {
                            Log("DownloadAsync", seeked.Code, done, watch);
                            return SftpResult.Fail((SshErrorCode)seeked.Code, seeked.Message);
                        }
                    }
                    var throttle = new ProgressThrottle(progress, total);
                    using (cancellationToken.Register(CancelInFlight))
                    {
                        for (;;)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            NativeBridge.SftpDataResult chunk = await _sftp
                                .ReadAsync(fileId, (uint)SftpConstants.ChunkSize)
                                .AsTask().ConfigureAwait(false);
                            if (chunk.Code != 0)
                            {
                                Log("DownloadAsync", chunk.Code, done, watch);
                                return SftpResult.Fail(
                                    (SshErrorCode)chunk.Code, chunk.Message);
                            }
                            byte[] bytes;
                            if (chunk.Data == null)
                            {
                                bytes = new byte[0];
                            }
                            else
                            {
                                CryptographicBuffer.CopyToByteArray(chunk.Data, out bytes);
                            }
                            if (bytes.Length == 0)
                            {
                                break; // EOF
                            }
                            await destination.WriteAsync(bytes, 0, bytes.Length,
                                cancellationToken).ConfigureAwait(false);
                            Array.Clear(bytes, 0, bytes.Length);
                            done += bytes.Length;
                            throttle.Report(done, false);
                        }
                    }
                    throttle.Report(done, true);
                }
                finally
                {
                    await CloseRemoteFile(fileId).ConfigureAwait(false);
                }
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                Log("DownloadAsync", 0, done, watch);
                return SftpResult.Success();
            }
            catch (OperationCanceledException)
            {
                Log("DownloadAsync", (int)SshErrorCode.SftpCancelled, done, watch);
                return SftpResult.Fail(SshErrorCode.SftpCancelled, string.Empty);
            }
            catch (Exception ex)
            {
                return Fail("DownloadAsync", ex, watch);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try
            {
                _sftp.Close();
            }
            catch (Exception)
            {
                // 析构路径：忽略（已记过相位日志）。
            }
            ((IDisposable)_sftp).Dispose();
        }

        // ---- 内部 ----

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("NativeSftpClient");
            }
        }

        private void CancelInFlight()
        {
            try
            {
                _sftp.Cancel();
            }
            catch (Exception)
            {
                // 取消路径不抛（块间 ct 检查兜底）。
            }
        }

        private async Task CloseRemoteFile(int fileId)
        {
            try
            {
                await _sftp.CloseFileAsync(fileId).AsTask().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 收尾 best-effort：owner 拆除时代关。
            }
        }

        private static SftpResult ValidateTransferStream(Stream stream, long resumeOffset)
        {
            if (stream == null)
            {
                return SftpResult.Fail(SshErrorCode.InternalError, "null stream");
            }
            if (!stream.CanSeek)
            {
                return SftpResult.Fail(SshErrorCode.InternalError, "stream not seekable");
            }
            if (resumeOffset < 0)
            {
                return SftpResult.Fail(SshErrorCode.InternalError, "negative resume");
            }
            try
            {
                if (resumeOffset > stream.Length)
                {
                    return SftpResult.Fail(SshErrorCode.InternalError, "resume beyond EOF");
                }
            }
            catch (Exception ex)
            {
                return SftpResult.Fail(SshErrorCode.SftpTransferFailed, ex.GetType().Name);
            }
            return null;
        }

        private static byte[] SubArray(byte[] source, int count)
        {
            byte[] slice = new byte[count];
            Array.Copy(source, 0, slice, 0, count);
            return slice;
        }

        internal static RemoteEntry FromNative(NativeBridge.SftpEntry entry, string dir)
        {
            RemoteEntryType type;
            switch (entry.Kind)
            {
                case NativeBridge.SftpEntryKind.File:
                    type = RemoteEntryType.File;
                    break;
                case NativeBridge.SftpEntryKind.Directory:
                    type = RemoteEntryType.Directory;
                    break;
                case NativeBridge.SftpEntryKind.Symlink:
                    type = RemoteEntryType.Symlink;
                    break;
                case NativeBridge.SftpEntryKind.Other:
                    type = RemoteEntryType.Other;
                    break;
                default:
                    type = RemoteEntryType.Unknown;
                    break;
            }
            string name = entry.Name ?? string.Empty;
            return new RemoteEntry(
                name,
                RemotePath.Combine(dir, name),
                type,
                entry.HasSize ? (long)entry.Size : -1,
                entry.HasPermissions ? entry.Permissions : -1,
                entry.HasMtime ? UnixEpoch.AddSeconds((double)entry.Mtime) : DateTime.MinValue,
                entry.LinkTarget ?? string.Empty);
        }

        private static SftpResult ToResult(NativeBridge.SftpResult result)
        {
            SshErrorCode code = (SshErrorCode)result.Code;
            if (code == SshErrorCode.None)
            {
                return SftpResult.Success();
            }
            return SftpResult.Fail(code, result.Message ?? string.Empty);
        }

        private static SftpResult ToResult(int code)
        {
            if (code == 0)
            {
                return SftpResult.Success();
            }
            return SftpResult.Fail((SshErrorCode)code, string.Empty);
        }

        // 进度节流（§11.1：200 ms；收尾强制上报）。
        private sealed class ProgressThrottle
        {
            private readonly IProgress<SftpProgress> _sink;
            private readonly long _total;
            private readonly Stopwatch _watch = Stopwatch.StartNew();
            private bool _reported;

            public ProgressThrottle(IProgress<SftpProgress> sink, long total)
            {
                _sink = sink;
                _total = total;
            }

            public void Report(long done, bool final)
            {
                if (_sink == null)
                {
                    return;
                }
                if (!_reported || final
                    || _watch.ElapsedMilliseconds >= SftpConstants.ProgressThrottleMs)
                {
                    _watch.Restart();
                    _reported = true;
                    _sink.Report(new SftpProgress(done, _total));
                }
            }
        }

        // 只记相位与耗时：LogRedactor 会二次兜底，但本层消息本来就不含敏感材料。
        private void Log(string method, int code, long bytes, Stopwatch watch)
        {
            ILogger logger = _logger;
            if (logger == null)
            {
                return;
            }
            watch.Stop();
            logger.Log(LogLevel.Debug, "Sftp",
                method + " code=" + code + " bytes=" + bytes + " " + watch.ElapsedMilliseconds + "ms");
        }

        private void Log(string method, Exception ex, Stopwatch watch)
        {
            ILogger logger = _logger;
            if (logger == null)
            {
                return;
            }
            if (watch != null)
            {
                watch.Stop();
            }
            logger.Log(LogLevel.Debug, "Sftp", method + " 异常 " + ex.GetType().Name);
        }

        private SftpResult Fail(string method, Exception ex, Stopwatch watch)
        {
            if (ex is OperationCanceledException)
            {
                Log(method, (int)SshErrorCode.SftpCancelled, 0, watch);
                return SftpResult.Fail(SshErrorCode.SftpCancelled, string.Empty);
            }
            Log(method, ex, watch);
            return SftpResult.Fail(SshErrorCode.SftpTransferFailed, ex.GetType().Name);
        }
    }
}
