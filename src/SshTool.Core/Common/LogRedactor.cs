using System.Text.RegularExpressions;

namespace SshTool.Core.Common
{
    // 01-DESIGN.md §12.2：键名（大小写不敏感）password|passphrase|token|accessToken|
    // refreshToken|privateKey|recoveryKey|ciphertext|authorization|syncPassword 的
    // key=value 与 "key":"value" 形式整值替换为 ***；PEM 块、SPM1 恢复密钥、Bearer 令牌同。
    public static class LogRedactor
    {
        private const string KeyNames =
            "password|passphrase|token|accessToken|refreshToken|privateKey|recoveryKey|ciphertext|authorization|syncPassword";

        private static readonly Regex PemBlock = new Regex(
            @"-----BEGIN [^-]+-----.*?-----END [^-]+-----",
            RegexOptions.Singleline);

        private static readonly Regex SpmRecoveryKey = new Regex(
            @"SPM1-[A-Za-z0-9_-]{43}-[A-Fa-f0-9]{12}");

        private static readonly Regex BearerToken = new Regex(
            @"\bBearer\s+\S+");

        private static readonly Regex JsonPair = new Regex(
            "\"" + "(?<key>" + KeyNames + ")" + "\"" + @"\s*:\s*""[^""]*""",
            RegexOptions.IgnoreCase);

        private static readonly Regex KeyValue = new Regex(
            @"\b(?<key>" + KeyNames + @")\s*=\s*(""[^""]*""|'[^']*'|[^\s,;&]+)",
            RegexOptions.IgnoreCase);

        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }
            var result = PemBlock.Replace(text, "***");
            result = SpmRecoveryKey.Replace(result, "***");
            result = BearerToken.Replace(result, "Bearer ***");
            result = JsonPair.Replace(result, "\"${key}\":\"***\"");
            result = KeyValue.Replace(result, "${key}=***");
            return result;
        }
    }
}
