using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SshTool.Core.Sync.Protocol;

namespace SshTool.Core.Sync.Vault
{
    // U16：恢复密钥输入的规范化与校验（02-UI-DESIGN.md §5.13 VaultUnlockPage）。
    // 格式与桌面端 crypto-vault.ts decodeRecoveryKey 逐字对齐（改动任一字符都会破坏跨端互通）：
    //   SPM1-<43 位 base64url（32 字节 raw，无填充）>-<12 位 hex 校验段>
    //   校验段 = SHA256("SPM1" || raw) 前 6 字节的大写 hex。
    // 规范化规则（要点：去空白、前缀大写、格式通过才可提交）：
    //   - 去掉全部空白（用户可能分行/带空格粘贴）；
    //   - 前缀大小写不敏感，统一为大写 "SPM1-"；
    //   - 校验段大写（与桌面端一致按大写显示，比对时忽略大小写）；
    //   - 正文段是 base64url，大小写敏感，保持原样。
    // 返回值统一为 resw 文案键（Vault_*，两份 resw 由单测钉住存在），null 表示通过——
    // 与 LoginFormValidator 同风格。本类只做纯函数，不碰网络与缓存，不记录输入内容。
    public static class RecoveryKeyInput
    {
        // 正文段长度：32 字节 base64url 无填充恒为 43（4·⌈32/3⌉-1）。
        public const int BodyLength = 43;
        // 校验段长度：SHA256 前 6 字节 → 12 个 hex 字符。
        public const int ChecksumLength = 12;
        // 完整长度："SPM1-" (5) + 43 + "-" (1) + 12 = 61。
        public const int TotalLength = 5 + BodyLength + 1 + ChecksumLength;
        // 展示第一行的正文截断位（§5.8 分 3 行：前缀+正文前 16、正文余 27、校验段）。
        public const int DisplayBodyPrefixLength = 16;

        public const string FormatInvalidKey = "Vault_RecoveryFormatInvalid";
        public const string ChecksumFailedKey = "Vault_RecoveryChecksumFailed";

        public static readonly string[] AllErrorKeys = new string[]
        {
            FormatInvalidKey,
            ChecksumFailedKey,
        };

        // 与桌面端 decodeRecoveryKey 相同的正则（base64url 字符集，校验段 hex）。
        private static readonly Regex FormatPattern = new Regex(
            "^" + SyncConstants.RecoveryKeyPrefix
            + "-([A-Za-z0-9_-]{" + BodyLength + "})-([A-Fa-f0-9]{" + ChecksumLength + "})$",
            RegexOptions.Compiled);

        // 规范化：去空白 + 前缀与校验段大写。输入为空/全空白返回 null（不视为格式错误，
        // 由「是否已输入」的判空路径处理；Validate 对空输入同样返回格式无效键）。
        public static string Normalize(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return null;
            }
            var compact = new StringBuilder(input.Length);
            for (int i = 0; i < input.Length; i++)
            {
                if (!char.IsWhiteSpace(input[i]))
                {
                    compact.Append(input[i]);
                }
            }
            if (compact.Length == 0)
            {
                return null;
            }
            string text = compact.ToString();
            // 前缀大小写不敏感地归一为 "SPM1-"；正文段保持原样（base64url 大小写敏感）。
            if (text.Length >= 5
                && string.Equals(text.Substring(0, 5), "spm1-", StringComparison.OrdinalIgnoreCase))
            {
                text = SyncConstants.RecoveryKeyPrefix + "-" + text.Substring(5);
            }
            // 校验段（末 12 位）统一大写，便于显示与逐字比对。
            if (text.Length > ChecksumLength)
            {
                text = text.Substring(0, text.Length - ChecksumLength)
                    + text.Substring(text.Length - ChecksumLength).ToUpperInvariant();
            }
            return text;
        }

        // 只看格式（不验校验段）：解锁按钮的 CanSubmit 用它保证「格式通过才可提交」。
        public static bool HasValidFormat(string normalized)
        {
            return !string.IsNullOrEmpty(normalized) && FormatPattern.IsMatch(normalized);
        }

        // 校验段复算：SHA256("SPM1"||raw) 前 12 位 hex（大写）与输入段一致。
        // raw 必须恰好解码出 32 字节。比对忽略大小写（桌面端校验段同为大写 hex；常量时间
        // 比对在 native 解包侧完成，这里的复算只是把明显抄错的密钥拦在提交前）。
        public static bool VerifyChecksum(string normalized)
        {
            string body;
            string checksum;
            if (!Split(normalized, out body, out checksum))
            {
                return false;
            }
            byte[] raw = DecodeBase64Url(body);
            if (raw == null || raw.Length != SyncConstants.KeyBytes)
            {
                return false;
            }
            string expected = ComputeChecksum(raw);
            if (expected == null)
            {
                return false;
            }
            return string.Equals(expected, checksum, StringComparison.OrdinalIgnoreCase);
        }

        // 规范化 + 全量校验：通过返回 null 并输出规范化后的密钥（提交给 Coordinator 用它），
        // 否则返回 resw 文案键（格式无效 / 校验失败）。
        public static string Validate(string rawInput, out string normalized)
        {
            normalized = Normalize(rawInput);
            if (!HasValidFormat(normalized))
            {
                return FormatInvalidKey;
            }
            if (!VerifyChecksum(normalized))
            {
                return ChecksumFailedKey;
            }
            return null;
        }

        // §5.8 RecoveryKeyDialog：大号等宽分 3 行（前缀+正文前 16 / 正文余 27 / "-"校验段）。
        // 按语义切分，抄写不会切碎同一字段；格式非法时返回 null，调用方回退整行显示。
        public static string[] DisplayLines(string recoveryKey)
        {
            string normalized = Normalize(recoveryKey);
            string body;
            string checksum;
            if (!Split(normalized, out body, out checksum))
            {
                return null;
            }
            return new string[]
            {
                SyncConstants.RecoveryKeyPrefix + "-" + body.Substring(0, DisplayBodyPrefixLength),
                body.Substring(DisplayBodyPrefixLength),
                "-" + checksum,
            };
        }

        private static bool Split(string normalized, out string body, out string checksum)
        {
            body = null;
            checksum = null;
            if (!HasValidFormat(normalized))
            {
                return false;
            }
            body = normalized.Substring(5, BodyLength);
            checksum = normalized.Substring(normalized.Length - ChecksumLength);
            return true;
        }

        private static string ComputeChecksum(byte[] raw)
        {
            byte[] prefix = Encoding.UTF8.GetBytes(SyncConstants.RecoveryKeyPrefix);
            var input = new byte[prefix.Length + raw.Length];
            Array.Copy(prefix, input, prefix.Length);
            Array.Copy(raw, 0, input, prefix.Length, raw.Length);
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(input);
                if (hash == null || hash.Length < ChecksumLength / 2)
                {
                    return null;
                }
                var hex = new char[ChecksumLength];
                const string digits = "0123456789ABCDEF";
                for (int i = 0; i < ChecksumLength / 2; i++)
                {
                    hex[i * 2] = digits[hash[i] >> 4];
                    hex[i * 2 + 1] = digits[hash[i] & 0x0F];
                }
                return new string(hex);
            }
        }

        private static byte[] DecodeBase64Url(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }
            try
            {
                string standard = text.Replace('-', '+').Replace('_', '/');
                int pad = (4 - (standard.Length % 4)) % 4;
                if (pad != 0)
                {
                    standard = standard + new string('=', pad);
                }
                return Convert.FromBase64String(standard);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
