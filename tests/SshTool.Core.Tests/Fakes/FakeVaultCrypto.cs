using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using SshTool.Core.Sync.Protocol;
using SshTool.Core.Sync.Vault;

namespace SshTool.Core.Tests.Fakes
{
    // S05：IVaultCrypto 的假实现（供 S11 协调器等 Core 逻辑单测）。
    //
    // 无 Argon2/AES，真实现在 native + NativeVaultCrypto，真机由「保险库自检」页覆盖。
    // 可逆编码模拟：包裹体是带前缀的 Base64（"PW1|…" / "RC1|…" / "DOC1|…"），
    // 解包时拆开比对——密码不对、vaultId/版本号对不上、密文被篡改一律返回 null，
    // 与真身「失败返回 null、不区分原因」的语义一致。
    // 密码错误模拟：(1) 传错密码自然返回 null；(2) FailPasswordUnwrap=true 时
    // 密码解包恒返回 null；FailRecoveryUnwrap 同理。调用记录在 Calls 与 Last*。
    public sealed class FakeVaultCrypto : IVaultCrypto
    {
        // ---- 调用记录 ----
        public readonly List<string> Calls = new List<string>();
        public string LastPassword;
        public string LastRecoveryKey;
        public int CreateCount;

        // ---- 错误注入 ----
        public bool FailPasswordUnwrap;
        public bool FailRecoveryUnwrap;
        public bool FailCreate;
        public bool FailEncrypt;
        public bool FailDecrypt;

        private int _counter;

        public Task<VaultSetupResult> CreateAsync(string syncPassword, int keyVersion)
        {
            Calls.Add("Create");
            LastPassword = syncPassword;
            CreateCount++;
            if (FailCreate || string.IsNullOrEmpty(syncPassword) || keyVersion < 1)
            {
                return Task.FromResult<VaultSetupResult>(null);
            }
            _counter++;
            string vaultKeyBase64 = DeriveFakeKey(syncPassword, keyVersion, _counter);
            string recoveryKey = MakeRecoveryKey(_counter);
            var envelope = new VaultKeyEnvelope
            {
                KeyVersion = keyVersion,
                PasswordWrappedKey = B64("PW1|" + keyVersion + "|" + vaultKeyBase64 + "|" + syncPassword),
                PasswordWrapNonce = B64("nonce-pw|" + keyVersion + "|" + _counter),
                RecoveryWrappedKey = B64("RC1|" + keyVersion + "|" + vaultKeyBase64 + "|" + recoveryKey),
                RecoveryWrapNonce = B64("nonce-rc|" + keyVersion + "|" + _counter),
                KdfSalt = B64("salt|" + keyVersion + "|" + _counter),
                KdfAlgorithm = SyncConstants.KdfAlgorithm,
                KdfMemory = SyncConstants.DefaultKdfMemoryKib,
                KdfIterations = SyncConstants.DefaultKdfIterations,
                KdfParallelism = SyncConstants.DefaultKdfParallelism
            };
            return Task.FromResult(new VaultSetupResult(envelope, recoveryKey, vaultKeyBase64));
        }

        public Task<string> UnwrapWithPasswordAsync(VaultKeyEnvelope envelope, string syncPassword)
        {
            Calls.Add("UnwrapWithPassword");
            LastPassword = syncPassword;
            if (FailPasswordUnwrap || envelope == null || string.IsNullOrEmpty(syncPassword))
            {
                return Task.FromResult<string>(null);
            }
            string[] parts = SplitWrapped(envelope.PasswordWrappedKey, "PW1");
            if (parts == null || parts.Length != 4)
            {
                return Task.FromResult<string>(null);
            }
            int wrappedVersion;
            if (!int.TryParse(parts[1], out wrappedVersion) || wrappedVersion != envelope.KeyVersion)
            {
                return Task.FromResult<string>(null);
            }
            // parts[2] = vaultKey，parts[3] = 创建时密码：对不上即「密码错误」。
            if (!string.Equals(parts[3], syncPassword, StringComparison.Ordinal))
            {
                return Task.FromResult<string>(null);
            }
            return Task.FromResult(parts[2]);
        }

        public Task<string> UnwrapWithRecoveryKeyAsync(VaultKeyEnvelope envelope, string recoveryKey)
        {
            Calls.Add("UnwrapWithRecoveryKey");
            LastRecoveryKey = recoveryKey;
            if (FailRecoveryUnwrap || envelope == null || string.IsNullOrEmpty(recoveryKey))
            {
                return Task.FromResult<string>(null);
            }
            string[] parts = SplitWrapped(envelope.RecoveryWrappedKey, "RC1");
            if (parts == null || parts.Length != 4)
            {
                return Task.FromResult<string>(null);
            }
            int wrappedVersion;
            if (!int.TryParse(parts[1], out wrappedVersion) || wrappedVersion != envelope.KeyVersion)
            {
                return Task.FromResult<string>(null);
            }
            if (!string.Equals(parts[3], recoveryKey, StringComparison.Ordinal))
            {
                return Task.FromResult<string>(null);
            }
            return Task.FromResult(parts[2]);
        }

        public Task<EncryptedDocumentEnvelope> EncryptDocumentAsync(
            string vaultKeyBase64, string vaultId, int schemaVersion, int keyVersion, byte[] plaintextUtf8)
        {
            Calls.Add("Encrypt");
            if (FailEncrypt || string.IsNullOrEmpty(vaultKeyBase64) || string.IsNullOrEmpty(vaultId)
                || schemaVersion < 1 || keyVersion < 1 || plaintextUtf8 == null)
            {
                return Task.FromResult<EncryptedDocumentEnvelope>(null);
            }
            string inner = B64(plaintextUtf8);
            string payload = "DOC1|" + vaultId + "|" + schemaVersion + "|" + keyVersion
                + "|" + vaultKeyBase64 + "|" + inner;
            string ciphertext = B64(payload);
            var envelope = new EncryptedDocumentEnvelope
            {
                SchemaVersion = schemaVersion,
                KeyVersion = keyVersion,
                Algorithm = SyncConstants.AesAlgorithm,
                Nonce = B64("nonce-doc|" + vaultId + "|" + keyVersion),
                Ciphertext = ciphertext,
                CiphertextHash = B64("HASH|" + ciphertext)
            };
            return Task.FromResult(envelope);
        }

        public Task<byte[]> DecryptDocumentAsync(
            string vaultKeyBase64, string vaultId, EncryptedDocumentEnvelope envelope)
        {
            Calls.Add("Decrypt");
            if (FailDecrypt || string.IsNullOrEmpty(vaultKeyBase64) || string.IsNullOrEmpty(vaultId)
                || envelope == null)
            {
                return Task.FromResult<byte[]>(null);
            }
            // 先验 hash（顺序与真身一致：hash 不对直接失败）。
            if (!string.Equals(envelope.CiphertextHash, B64("HASH|" + envelope.Ciphertext),
                    StringComparison.Ordinal))
            {
                return Task.FromResult<byte[]>(null);
            }
            string payload;
            try
            {
                payload = Str(B64Decode(envelope.Ciphertext));
            }
            catch
            {
                return Task.FromResult<byte[]>(null);
            }
            // 限 6 段：vaultId 含分隔符时仍能解析（base64 内段不含 '|'）。
            string[] parts = payload.Split(new[] { '|' }, 6);
            if (parts.Length != 6 || parts[0] != "DOC1")
            {
                return Task.FromResult<byte[]>(null);
            }
            int schemaVersion;
            int keyVersion;
            if (!string.Equals(parts[1], vaultId, StringComparison.Ordinal)
                || !int.TryParse(parts[2], out schemaVersion) || schemaVersion != envelope.SchemaVersion
                || !int.TryParse(parts[3], out keyVersion) || keyVersion != envelope.KeyVersion
                || !string.Equals(parts[4], vaultKeyBase64, StringComparison.Ordinal))
            {
                return Task.FromResult<byte[]>(null);
            }
            try
            {
                return Task.FromResult(B64Decode(parts[5]));
            }
            catch
            {
                return Task.FromResult<byte[]>(null);
            }
        }

        // ---- 可逆编码工具（确定性、无随机，便于单测断言） ----

        private static string B64(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        }

        private static string B64(byte[] bytes)
        {
            return Convert.ToBase64String(bytes);
        }

        private static byte[] B64Decode(string b64)
        {
            return Convert.FromBase64String(b64);
        }

        private static string Str(byte[] bytes)
        {
            return Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }

        private static string[] SplitWrapped(string wrapped, string prefix)
        {
            if (string.IsNullOrEmpty(wrapped))
            {
                return null;
            }
            string payload;
            try
            {
                payload = Str(B64Decode(wrapped));
            }
            catch
            {
                return null;
            }
            string[] parts = payload.Split(new[] { '|' }, 4);
            if (parts.Length == 0 || parts[0] != prefix)
            {
                return null;
            }
            return parts;
        }

        // 32 字节确定性伪密钥（FNV-1a + xorshift 展开，真 Base64，32 字节）。
        private static string DeriveFakeKey(string password, int keyVersion, int counter)
        {
            byte[] seed = Encoding.UTF8.GetBytes(password + "|" + keyVersion + "|" + counter);
            ulong hash = 1469598103934665603UL;
            foreach (byte b in seed)
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }
            var key = new byte[SyncConstants.KeyBytes];
            ulong state = hash | 1UL;
            for (int i = 0; i < key.Length; i++)
            {
                state ^= state << 13;
                state ^= state >> 7;
                state ^= state << 17;
                key[i] = (byte)(state & 0xFF);
            }
            return Convert.ToBase64String(key);
        }

        // 格式合法的假恢复密钥（SPM1-<43 base64url>-<12 HEX>，供格式校验链路使用）。
        private static string MakeRecoveryKey(int counter)
        {
            var raw = new byte[SyncConstants.KeyBytes];
            for (int i = 0; i < raw.Length; i++)
            {
                raw[i] = (byte)((counter * 31 + i * 7) & 0xFF);
            }
            string b64url = Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            ulong hash = 1469598103934665603UL;
            foreach (byte b in raw)
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }
            return "SPM1-" + b64url + "-" + hash.ToString("X12").Substring(0, 12);
        }
    }
}
