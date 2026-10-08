using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.Core.Storage;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SshTool.App.Platform
{
    // D03 / D10：DataProtectionProvider("LOCAL=user") 加密整段明文，落 LocalFolder/secure/secrets.bin。
    // opt/full-pass：解密失败时重试一次；两次都属于「真损坏 / 密钥已不在」（HRESULT 判定见
    // Core SecureFileFailurePolicy：NTE_BAD_DATA、NTE_NO_KEY 等）才把文件改名隔离为
    // <name>.corrupt-<UTC 时间戳> 并返回空——不删除，便于人工恢复。
    // 评审（PR #1）：暂时性/未知的 DPAPI 或平台错误不隔离，原样上抛（与旧行为一致），
    // 避免有效凭据因一次偶发错误被当成「不存在」。读文件本身的 IO 异常也照旧上抛。
    public sealed class DpapiSecureFile : ISecureFile, ISecureFileQuarantine
    {
        public const string DefaultRelativePath = "secure/secrets.bin";
        private const string Descriptor = "LOCAL=user";

        private readonly StorageFolder _root;
        private readonly string _relativePath;

        public DpapiSecureFile()
            : this(ApplicationData.Current.LocalFolder, DefaultRelativePath)
        {
        }

        internal DpapiSecureFile(StorageFolder root, string relativePath)
        {
            _root = root;
            _relativePath = string.IsNullOrEmpty(relativePath) ? DefaultRelativePath : relativePath;
        }

        // fix/functional-pass（P2-3）：见 ISecureFileQuarantine.DiscardedOnLastRead。
        public bool DiscardedOnLastRead { get; private set; }

        public async Task<byte[]> ReadAsync()
        {
            DiscardedOnLastRead = false;
            StorageFile file = await TryGetFileAsync().ConfigureAwait(false);
            if (file == null)
            {
                return new byte[0];
            }
            IBuffer cipher = await FileIO.ReadBufferAsync(file);
            if (cipher == null || cipher.Length == 0)
            {
                return new byte[0];
            }
            var failures = new List<int>(UnprotectAttempts);
            Exception last = null;
            for (int attempt = 0; attempt < UnprotectAttempts; attempt++)
            {
                try
                {
                    IBuffer plain = await new DataProtectionProvider().UnprotectAsync(cipher);
                    byte[] bytes;
                    CryptographicBuffer.CopyToByteArray(plain, out bytes);
                    return bytes ?? new byte[0];
                }
                catch (Exception ex)
                {
                    last = ex;
                    failures.Add(ex.HResult);
                }
                if (attempt + 1 < UnprotectAttempts && !SecureFileFailurePolicy.IsGenuineCorruption(failures[attempt]))
                {
                    // 暂时性错误：稍等再试，给 DPAPI/系统服务一点恢复时间
                    await Task.Delay(TransientRetryDelayMs).ConfigureAwait(false);
                }
            }
            if (SecureFileFailurePolicy.ShouldQuarantine(failures))
            {
                await QuarantineAsync("decrypt 0x" + failures[failures.Count - 1].ToString("X8")).ConfigureAwait(false);
                DiscardedOnLastRead = true;
                return new byte[0];
            }
            ILogger log = AppLog.Logger;
            if (log != null)
            {
                log.Log(LogLevel.Warning, "SecureFile",
                    "解密暂时失败（0x" + failures[failures.Count - 1].ToString("X8") + "），保留原文件并上抛");
            }
            ExceptionDispatchInfo.Capture(last).Throw();
            throw last; // 不可达：满足编译器的返回路径检查
        }

        private const int UnprotectAttempts = 2;
        private const int TransientRetryDelayMs = 250;

        public async Task<bool> QuarantineAsync(string reason)
        {
            try
            {
                StorageFile file = await TryGetFileAsync().ConfigureAwait(false);
                if (file == null)
                {
                    return false;
                }
                string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
                string newName = file.Name + ".corrupt-" + stamp;
                await file.RenameAsync(newName, NameCollisionOption.GenerateUniqueName);
                ILogger log = AppLog.Logger;
                if (log != null)
                {
                    log.Log(LogLevel.Warning, "SecureFile",
                        "受保护文件无法使用（" + (reason ?? "?") + "），已隔离为 " + newName + "，从空开始");
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public async Task WriteAsync(byte[] plaintext)
        {
            byte[] data = plaintext ?? new byte[0];
            IBuffer plain = CryptographicBuffer.CreateFromByteArray(data);
            IBuffer cipher = await new DataProtectionProvider(Descriptor).ProtectAsync(plain);
            StorageFolder folder = await EnsureParentAsync().ConfigureAwait(false);
            string name = FileNameOf(_relativePath);
            string tmpName = name + ".tmp";
            StorageFile tmp = await folder.CreateFileAsync(tmpName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBufferAsync(tmp, cipher);
            IStorageItem existing = await folder.TryGetItemAsync(name);
            var target = existing as StorageFile;
            if (target != null)
            {
                await tmp.MoveAndReplaceAsync(target);
            }
            else
            {
                await tmp.MoveAsync(folder, name, NameCollisionOption.ReplaceExisting);
            }
        }

        private async Task<StorageFile> TryGetFileAsync()
        {
            string[] parts = _relativePath.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            IStorageItem item = _root;
            for (int i = 0; i < parts.Length; i++)
            {
                var folder = item as StorageFolder;
                if (folder == null)
                {
                    return null;
                }
                item = await folder.TryGetItemAsync(parts[i]);
                if (item == null)
                {
                    return null;
                }
            }
            return item as StorageFile;
        }

        private async Task<StorageFolder> EnsureParentAsync()
        {
            string[] parts = _relativePath.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            StorageFolder folder = _root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                folder = await folder.CreateFolderAsync(parts[i], CreationCollisionOption.OpenIfExists);
            }
            return folder;
        }

        private static string FileNameOf(string path)
        {
            string[] parts = path.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            return parts[parts.Length - 1];
        }
    }
}
