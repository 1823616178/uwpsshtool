using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;

namespace SshTool.Core.Keys
{
    // K02 导入/生成编排（02-UI-DESIGN.md §5.10）。
    //
    // 职责：大小上限 → 解析（加密时需短语）→ 指纹去重 → 落库（元数据进
    // KeyRepository，私钥/短语进 SecretStore）→ 生成（经 IKeyTool）。
    // 本类只记相位与数量，不记私钥、短语、公钥内容（日志脱敏）。
    public enum KeyInspectStatus
    {
        Ready = 0,          // 可保存
        Duplicate = 1,      // 指纹已存在（Existing 非空，Inspected 为本次解析结果）
        NeedsPassphrase = 2,// 加密私钥，需短语后再 Inspect
        Invalid = 3,        // 解析失败（含短语错误、损坏文件）
        TooLarge = 4,       // 超过 MaxPrivateKeyBytes（未调用 IKeyTool）
        Empty = 5           // 空输入（未调用 IKeyTool）
    }

    public sealed class KeyInspectOutcome
    {
        public KeyInspectStatus Status { get; set; }
        public InspectedKeyInfo Inspected { get; set; }
        public KeyEntry Existing { get; set; }
    }

    public enum KeyGenerateKind
    {
        Ed25519 = 0,
        Rsa = 1
    }

    // fix/functional-pass（P2-2）：生成失败原因码（文案由 App 查 resw：KeyGenerate_Err_*）。
    public enum KeyGenerateError
    {
        None = 0,
        UnsupportedBits,
        GenerateFailed,
        VerifyFailed,
        DuplicateFingerprint,
        // App 侧校验（名称等），Error 为已本地化文案。
        Validation
    }

    public sealed class KeyGenerateOutcome
    {
        public bool Success { get; set; }
        public KeyEntry Entry { get; set; }
        public string PrivateKeyText { get; set; }
        public KeyGenerateError ErrorCode { get; set; }
        // 诊断文本（ErrorCode=Validation 时为 App 已本地化的文案）。
        public string Error { get; set; }
    }

    public sealed class KeyImportService
    {
        // 02-UI-DESIGN.md §5.10：导入上限 256 KiB（按 UTF-8 字节计）。
        public const int MaxPrivateKeyBytes = 262144;

        private const string Tag = "KeyImport";

        private readonly KeyRepository _keys;
        private readonly ISecretStore _secrets;
        private readonly IKeyTool _tool;
        private readonly ILogger _log;

        public KeyImportService(KeyRepository keys, ISecretStore secrets, IKeyTool tool, ILogger logger = null)
        {
            if (keys == null)
            {
                throw new ArgumentNullException("keys");
            }
            if (secrets == null)
            {
                throw new ArgumentNullException("secrets");
            }
            if (tool == null)
            {
                throw new ArgumentNullException("tool");
            }
            _keys = keys;
            _secrets = secrets;
            _tool = tool;
            _log = logger;
        }

        // 解析导入文本。passphrase 无则传 null/空串。
        // 返回 Duplicate 时调用方可提示「已存在」并让用户选择查看已有或取消。
        public async Task<KeyInspectOutcome> InspectAsync(string privateKeyText, string passphrase)
        {
            if (string.IsNullOrWhiteSpace(privateKeyText))
            {
                return new KeyInspectOutcome { Status = KeyInspectStatus.Empty };
            }
            int byteCount;
            try
            {
                byteCount = Encoding.UTF8.GetByteCount(privateKeyText);
            }
            catch (ArgumentException)
            {
                Log("Inspect 失败：非法字符");
                return new KeyInspectOutcome { Status = KeyInspectStatus.Invalid };
            }
            if (byteCount > MaxPrivateKeyBytes)
            {
                Log("Inspect 拒绝：超过大小上限 " + byteCount.ToString(CultureInfo.InvariantCulture) + " 字节");
                return new KeyInspectOutcome { Status = KeyInspectStatus.TooLarge };
            }

            string phrase = passphrase ?? string.Empty;
            InspectedKeyInfo info = await _tool.InspectAsync(privateKeyText, phrase).ConfigureAwait(false);
            if (info != null)
            {
                // 加密 PEM 无短语时的部分成功：无指纹，无法去重，必须先要短语。
                if (info.Encrypted && string.IsNullOrEmpty(info.FingerprintSha256))
                {
                    Log("Inspect 需短语：部分信息");
                    return new KeyInspectOutcome
                    {
                        Status = KeyInspectStatus.NeedsPassphrase,
                        Inspected = info
                    };
                }
                KeyEntry existing = await FindByFingerprintAsync(info.FingerprintSha256).ConfigureAwait(false);
                if (existing != null)
                {
                    Log("Inspect 重复指纹");
                    return new KeyInspectOutcome
                    {
                        Status = KeyInspectStatus.Duplicate,
                        Inspected = info,
                        Existing = existing
                    };
                }
                Log("Inspect 就绪 " + (info.KeyType ?? string.Empty));
                return new KeyInspectOutcome
                {
                    Status = KeyInspectStatus.Ready,
                    Inspected = info
                };
            }

            // 解析失败：无短语且文本带加密标记 → 提示要短语；
            // 带短语仍失败 → 短语错误或文件损坏（native 不区分，此处亦不区分）。
            if (phrase.Length == 0 && LooksEncrypted(privateKeyText))
            {
                Log("Inspect 需短语：解析失败");
                return new KeyInspectOutcome { Status = KeyInspectStatus.NeedsPassphrase };
            }
            Log("Inspect 失败：无法解析");
            return new KeyInspectOutcome { Status = KeyInspectStatus.Invalid };
        }

        // 落库：元数据进仓库，私钥原文进 SecretStore，短语非空才存。
        // name 为空时用注释或类型兜底；调用方（对话框）应已做非空校验。
        // passphraseToStore：要保存的短语（加密钥且用户勾选保存时）；null/空串不存。
        public async Task<KeyEntry> SaveAsync(
            string privateKeyText, string name, InspectedKeyInfo inspected, string passphraseToStore)
        {
            if (string.IsNullOrEmpty(privateKeyText))
            {
                throw new ArgumentNullException("privateKeyText");
            }
            if (inspected == null)
            {
                throw new ArgumentNullException("inspected");
            }
            if (string.IsNullOrEmpty(inspected.FingerprintSha256))
            {
                throw new InvalidOperationException("无指纹的解析结果不能保存");
            }
            string cleanName = (name ?? string.Empty).Trim();
            if (cleanName.Length == 0)
            {
                cleanName = string.IsNullOrEmpty(inspected.Comment)
                    ? (inspected.KeyType ?? "密钥")
                    : inspected.Comment;
            }
            if (cleanName.Length > 255)
            {
                cleanName = cleanName.Substring(0, 255);
            }
            var entry = new KeyEntry
            {
                Id = IdGenerator.NewId(),
                Name = cleanName,
                KeyType = inspected.KeyType ?? string.Empty,
                Bits = inspected.Bits,
                Format = inspected.Format ?? string.Empty,
                Encrypted = inspected.Encrypted,
                PublicKeyOpenSsh = inspected.PublicKeyOpenSsh ?? string.Empty,
                FingerprintSha256 = inspected.FingerprintSha256,
                CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Comment = inspected.Comment ?? string.Empty,
                Extra = null
            };
            await _keys.AddAsync(entry, ChangeOrigin.User).ConfigureAwait(false);
            await _secrets.SetAsync(SecretKeys.KeyPrivate(entry.Id), privateKeyText).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(passphraseToStore))
            {
                await _secrets.SetAsync(SecretKeys.KeyPassphrase(entry.Id), passphraseToStore).ConfigureAwait(false);
            }
            Log("保存成功 " + entry.KeyType + " " + CredentialTail(entry.FingerprintSha256));
            return entry;
        }

        // 生成并落库：生成的私钥一律未加密，短语不存。
        public async Task<KeyGenerateOutcome> GenerateAsync(
            KeyGenerateKind kind, int bits, string comment, string name)
        {
            string cleanComment = comment ?? string.Empty;
            string text = null;
            if (kind == KeyGenerateKind.Ed25519)
            {
                text = await _tool.GenerateEd25519Async(cleanComment).ConfigureAwait(false);
            }
            else
            {
                if (bits != 3072 && bits != 4096)
                {
                    return new KeyGenerateOutcome { Success = false, ErrorCode = KeyGenerateError.UnsupportedBits, Error = "rsa bits must be 3072 or 4096" };
                }
                text = await _tool.GenerateRsaAsync(bits, cleanComment).ConfigureAwait(false);
            }
            if (string.IsNullOrEmpty(text))
            {
                Log("生成失败");
                return new KeyGenerateOutcome { Success = false, ErrorCode = KeyGenerateError.GenerateFailed, Error = "generate failed" };
            }
            InspectedKeyInfo info = await _tool.InspectAsync(text, string.Empty).ConfigureAwait(false);
            if (info == null || string.IsNullOrEmpty(info.FingerprintSha256))
            {
                Log("生成后解析失败");
                return new KeyGenerateOutcome { Success = false, ErrorCode = KeyGenerateError.VerifyFailed, Error = "verify after generate failed" };
            }
            KeyEntry existing = await FindByFingerprintAsync(info.FingerprintSha256).ConfigureAwait(false);
            if (existing != null)
            {
                // 生成碰撞（指纹重复）概率可忽略；真发生时拒绝，避免两条元数据指向同一密钥。
                Log("生成碰撞：指纹重复");
                return new KeyGenerateOutcome { Success = false, ErrorCode = KeyGenerateError.DuplicateFingerprint, Error = "duplicate fingerprint" };
            }
            KeyEntry entry = await SaveAsync(text, name, info, null).ConfigureAwait(false);
            return new KeyGenerateOutcome { Success = true, Entry = entry, PrivateKeyText = text };
        }

        private async Task<KeyEntry> FindByFingerprintAsync(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint))
            {
                return null;
            }
            IReadOnlyList<KeyEntry> all = await _keys.GetAllAsync().ConfigureAwait(false);
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null
                    && string.Equals(all[i].FingerprintSha256, fingerprint, StringComparison.Ordinal))
                {
                    return all[i];
                }
            }
            return null;
        }

        // PEM 加密私钥头（"ENCRYPTED PRIVATE KEY" / "ENCRYPTED OPENSSH PRIVATE KEY"）
        // 与传统 PEM 的 Proc-Type/DEK-Info 标记；openssh-key-v1 加密时短语被 native
        // 忽略（公钥可直接取），走不到这里，此处只做启发式提示。
        private static bool LooksEncrypted(string text)
        {
            return text.IndexOf("ENCRYPTED", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Proc-Type: 4,ENCRYPTED", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("DEK-Info:", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // 日志只记指纹尾部（公钥指纹非敏感，但全指纹无必要）。
        private static string CredentialTail(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint))
            {
                return string.Empty;
            }
            string s = fingerprint;
            int colon = s.LastIndexOf(':');
            if (colon >= 0 && colon < s.Length - 1)
            {
                s = s.Substring(colon + 1);
            }
            if (s.Length <= 8)
            {
                return s;
            }
            return s.Substring(s.Length - 8);
        }

        private void Log(string message)
        {
            ILogger log = _log;
            if (log != null)
            {
                log.Log(LogLevel.Info, Tag, message);
            }
        }
    }
}
