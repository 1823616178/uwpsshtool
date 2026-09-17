using System;
using System.Text;
using System.Text.RegularExpressions;

namespace SshTool.Core.Sync.Protocol
{
    // 桌面端 sync-private-key.ts 的移植：私钥格式由 header 判定；指纹格式常量。
    public static class PrivateKeyFormat
    {
        public const string OpenSsh = "openssh";
        public const string Pem = "pem";

        public const string EncodingBase64 = "base64";

        // PRIVATE_KEY_FINGERPRINT_PATTERN
        public const string FingerprintPattern = @"^SHA256:[A-Za-z0-9+/]{43}$";

        private static readonly Regex OpenSshHeader = new Regex(
            @"^-----BEGIN OPENSSH PRIVATE KEY-----\r?\n", RegexOptions.Compiled);
        private static readonly Regex PemHeader = new Regex(
            @"^-----BEGIN (?:RSA |EC |DSA |ENCRYPTED )?PRIVATE KEY-----\r?\n", RegexOptions.Compiled);

        // 与桌面端 privateKeyFormatOf 一致：UTF-8 解码失败或非受支持 header → null。
        public static string Detect(byte[] data)
        {
            if (data == null)
            {
                return null;
            }
            string text;
            try
            {
                text = new UTF8Encoding(false, true).GetString(data);
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
            if (OpenSshHeader.IsMatch(text))
            {
                return OpenSsh;
            }
            if (PemHeader.IsMatch(text))
            {
                return Pem;
            }
            return null;
        }

        public static bool IsValidFingerprint(string value)
        {
            return value != null && Regex.IsMatch(value, FingerprintPattern);
        }
    }
}
