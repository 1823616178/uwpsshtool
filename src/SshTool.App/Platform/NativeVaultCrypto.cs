using System;
using System.Diagnostics;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Sync.Vault;
using NativeBridge = SshTool.Native.Bridge;

namespace SshTool.App.Platform
{
    // S05：IVaultCrypto 的原生适配（01-DESIGN.md §4.1：Core 不引用 Native，
    // WinRT 类型转换与 IAsyncOperation→Task 都在本层；模式见 NativeSshSession）。
    //
    // 线程：native 方法经 create_async 已在后台线程执行，本层只做转换，
    // 全程 ConfigureAwait(false)，绝不回 UI 线程。
    // 失败语义：native 返回 null → 本层返回 null；WinRT 异常同样转 null
    // （记类型名，不记消息与参数）。
    // 日志脱敏：只记方法名 + 成功与否 + 耗时毫秒；绝不记密码、恢复密钥、
    // vaultKey、信封内容、明文与密文。
    public sealed class NativeVaultCrypto : IVaultCrypto
    {
        private readonly ILogger _logger;

        public NativeVaultCrypto(ILogger logger)
        {
            _logger = logger;
        }

        public NativeVaultCrypto()
            : this(null)
        {
        }

        public async Task<VaultSetupResult> CreateAsync(string syncPassword, int keyVersion)
        {
            Stopwatch watch = Stopwatch.StartNew();
            bool ok = false;
            try
            {
                if (string.IsNullOrEmpty(syncPassword) || keyVersion < 1)
                {
                    return null;
                }
                NativeBridge.VaultSetupResult result =
                    await NativeBridge.VaultCrypto.CreateAsync(syncPassword, keyVersion)
                        .AsTask().ConfigureAwait(false);
                if (result == null || result.Envelope == null
                    || string.IsNullOrEmpty(result.VaultKeyBase64)
                    || string.IsNullOrEmpty(result.RecoveryKey))
                {
                    return null;
                }
                ok = true;
                return new VaultSetupResult(
                    FromNative(result.Envelope), result.RecoveryKey, result.VaultKeyBase64);
            }
            catch (Exception ex)
            {
                Log("CreateAsync", "异常 " + ex.GetType().Name, watch);
                return null;
            }
            finally
            {
                if (ok)
                {
                    Log("CreateAsync", "成功", watch);
                }
                else
                {
                    Log("CreateAsync", "失败", watch);
                }
            }
        }

        public async Task<string> UnwrapWithPasswordAsync(VaultKeyEnvelope envelope, string syncPassword)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                if (envelope == null || string.IsNullOrEmpty(syncPassword))
                {
                    Log("UnwrapWithPasswordAsync", "失败", watch);
                    return null;
                }
                string key = await NativeBridge.VaultCrypto
                    .UnwrapWithPasswordAsync(ToNative(envelope), syncPassword)
                    .AsTask().ConfigureAwait(false);
                Log("UnwrapWithPasswordAsync", string.IsNullOrEmpty(key) ? "失败" : "成功", watch);
                return string.IsNullOrEmpty(key) ? null : key;
            }
            catch (Exception ex)
            {
                Log("UnwrapWithPasswordAsync", "异常 " + ex.GetType().Name, watch);
                return null;
            }
        }

        public async Task<string> UnwrapWithRecoveryKeyAsync(VaultKeyEnvelope envelope, string recoveryKey)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                if (envelope == null || string.IsNullOrEmpty(recoveryKey))
                {
                    Log("UnwrapWithRecoveryKeyAsync", "失败", watch);
                    return null;
                }
                string key = await NativeBridge.VaultCrypto
                    .UnwrapWithRecoveryKeyAsync(ToNative(envelope), recoveryKey)
                    .AsTask().ConfigureAwait(false);
                Log("UnwrapWithRecoveryKeyAsync", string.IsNullOrEmpty(key) ? "失败" : "成功", watch);
                return string.IsNullOrEmpty(key) ? null : key;
            }
            catch (Exception ex)
            {
                Log("UnwrapWithRecoveryKeyAsync", "异常 " + ex.GetType().Name, watch);
                return null;
            }
        }

        public async Task<EncryptedDocumentEnvelope> EncryptDocumentAsync(
            string vaultKeyBase64, string vaultId, int schemaVersion, int keyVersion, byte[] plaintextUtf8)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                if (string.IsNullOrEmpty(vaultKeyBase64) || string.IsNullOrEmpty(vaultId)
                    || schemaVersion < 1 || keyVersion < 1 || plaintextUtf8 == null)
                {
                    Log("EncryptDocumentAsync", "失败", watch);
                    return null;
                }
                NativeBridge.DocumentEnvelope result = await NativeBridge.VaultCrypto.EncryptDocumentAsync(
                    vaultKeyBase64, vaultId, schemaVersion, keyVersion, plaintextUtf8)
                    .AsTask().ConfigureAwait(false);
                if (result == null)
                {
                    Log("EncryptDocumentAsync", "失败", watch);
                    return null;
                }
                Log("EncryptDocumentAsync", "成功", watch);
                return FromNative(result);
            }
            catch (Exception ex)
            {
                Log("EncryptDocumentAsync", "异常 " + ex.GetType().Name, watch);
                return null;
            }
        }

        public async Task<byte[]> DecryptDocumentAsync(
            string vaultKeyBase64, string vaultId, EncryptedDocumentEnvelope envelope)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                if (string.IsNullOrEmpty(vaultKeyBase64) || string.IsNullOrEmpty(vaultId)
                    || envelope == null)
                {
                    Log("DecryptDocumentAsync", "失败", watch);
                    return null;
                }
                Windows.Storage.Streams.IBuffer buffer = await NativeBridge.VaultCrypto
                    .DecryptDocumentAsync(vaultKeyBase64, vaultId, ToNative(envelope))
                    .AsTask().ConfigureAwait(false);
                if (buffer == null)
                {
                    Log("DecryptDocumentAsync", "失败", watch);
                    return null;
                }
                if (buffer.Length == 0)
                {
                    Log("DecryptDocumentAsync", "成功", watch);
                    return new byte[0];
                }
                byte[] bytes;
                Windows.Security.Cryptography.CryptographicBuffer.CopyToByteArray(buffer, out bytes);
                Log("DecryptDocumentAsync", "成功", watch);
                return bytes;
            }
            catch (Exception ex)
            {
                Log("DecryptDocumentAsync", "异常 " + ex.GetType().Name, watch);
                return null;
            }
        }

        // ---- WinRT ↔ Core 转换（只搬字段，不解释内容） ----

        internal static NativeBridge.VaultEnvelope ToNative(VaultKeyEnvelope envelope)
        {
            var native = new NativeBridge.VaultEnvelope
            {
                KeyVersion = envelope.KeyVersion,
                PasswordWrappedKey = envelope.PasswordWrappedKey ?? string.Empty,
                PasswordWrapNonce = envelope.PasswordWrapNonce ?? string.Empty,
                RecoveryWrappedKey = envelope.RecoveryWrappedKey ?? string.Empty,
                RecoveryWrapNonce = envelope.RecoveryWrapNonce ?? string.Empty,
                KdfSalt = envelope.KdfSalt ?? string.Empty,
                KdfAlgorithm = envelope.KdfAlgorithm ?? string.Empty,
                KdfMemory = envelope.KdfMemory,
                KdfIterations = envelope.KdfIterations,
                KdfParallelism = envelope.KdfParallelism
            };
            return native;
        }

        internal static VaultKeyEnvelope FromNative(NativeBridge.VaultEnvelope native)
        {
            return new VaultKeyEnvelope
            {
                KeyVersion = native.KeyVersion,
                PasswordWrappedKey = native.PasswordWrappedKey ?? string.Empty,
                PasswordWrapNonce = native.PasswordWrapNonce ?? string.Empty,
                RecoveryWrappedKey = native.RecoveryWrappedKey ?? string.Empty,
                RecoveryWrapNonce = native.RecoveryWrapNonce ?? string.Empty,
                KdfSalt = native.KdfSalt ?? string.Empty,
                KdfAlgorithm = native.KdfAlgorithm ?? string.Empty,
                KdfMemory = native.KdfMemory,
                KdfIterations = native.KdfIterations,
                KdfParallelism = native.KdfParallelism
            };
        }

        internal static NativeBridge.DocumentEnvelope ToNative(EncryptedDocumentEnvelope envelope)
        {
            var native = new NativeBridge.DocumentEnvelope
            {
                SchemaVersion = envelope.SchemaVersion,
                KeyVersion = envelope.KeyVersion,
                Algorithm = envelope.Algorithm ?? string.Empty,
                Nonce = envelope.Nonce ?? string.Empty,
                Ciphertext = envelope.Ciphertext ?? string.Empty,
                CiphertextHash = envelope.CiphertextHash ?? string.Empty
            };
            return native;
        }

        internal static EncryptedDocumentEnvelope FromNative(NativeBridge.DocumentEnvelope native)
        {
            return new EncryptedDocumentEnvelope
            {
                SchemaVersion = native.SchemaVersion,
                KeyVersion = native.KeyVersion,
                Algorithm = native.Algorithm ?? string.Empty,
                Nonce = native.Nonce ?? string.Empty,
                Ciphertext = native.Ciphertext ?? string.Empty,
                CiphertextHash = native.CiphertextHash ?? string.Empty
            };
        }

        // 只记相位与耗时：LogRedactor 会二次兜底，但本层消息本来就不含敏感材料。
        private void Log(string method, string outcome, Stopwatch watch)
        {
            ILogger logger = _logger;
            if (logger == null)
            {
                return;
            }
            watch.Stop();
            logger.Log(LogLevel.Debug, "VaultCrypto",
                method + " " + outcome + " " + watch.ElapsedMilliseconds + "ms");
        }
    }
}
