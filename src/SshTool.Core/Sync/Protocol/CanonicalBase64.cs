using System;
using System.Text.RegularExpressions;

namespace SshTool.Core.Sync.Protocol
{
    // 桌面端 sync-private-key.ts decodeCanonicalBase64 的移植：
    // 严格 RFC 4648 —— 标准字母表、必须有填充、长度 %4==0、解码再编码必须逐字节等于原文。
    public static class CanonicalBase64
    {
        // MAX_PRIVATE_KEY_BASE64_LENGTH = 4 * ceil(262144 / 3)
        public const int MaxPrivateKeyBase64Length = 349528;

        private static readonly Regex Shape = new Regex(
            @"^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$",
            RegexOptions.Compiled);

        public static byte[] TryDecode(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length % 4 != 0 || !Shape.IsMatch(value))
            {
                return null;
            }
            byte[] decoded;
            try
            {
                decoded = Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                return null;
            }
            if (Convert.ToBase64String(decoded) != value)
            {
                return null;
            }
            return decoded;
        }

        public static bool IsCanonical(string value)
        {
            return TryDecode(value) != null;
        }
    }
}
