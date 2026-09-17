using System.Text;
using SshTool.Core.Sync.Protocol;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // 桌面端 sync-private-key.ts 的移植校验。
    public class CanonicalBase64AndPrivateKeyFormatTests
    {
        [Theory]
        [InlineData("AAAA")]                    // 规范
        [InlineData("AQ==")]                    // 规范填充
        [InlineData("AAA=")]                    // 规范填充
        public void CanonicalBase64_Accepts(string value)
        {
            Assert.True(CanonicalBase64.IsCanonical(value));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]                        // 空串（桌面端 !value 拒绝）
        [InlineData("Zh==")]                    // 非规范尾位（再编码为 Zg==）
        [InlineData("%%%%")]                    // 非法字母表
        [InlineData("AAA")]                     // 长度 %4 != 0
        [InlineData("A===")]                    // 填充形状非法
        [InlineData("AAAA\n")]                  // 空白字符
        public void CanonicalBase64_Rejects(string value)
        {
            Assert.False(CanonicalBase64.IsCanonical(value));
            Assert.Null(CanonicalBase64.TryDecode(value));
        }

        [Fact]
        public void CanonicalBase64_RoundTripsBytes()
        {
            var data = Encoding.UTF8.GetBytes("私钥内容🔑");
            string b64 = System.Convert.ToBase64String(data);
            Assert.Equal(data, CanonicalBase64.TryDecode(b64));
        }

        [Theory]
        [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nx", PrivateKeyFormat.OpenSsh)]
        [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\r\nx", PrivateKeyFormat.OpenSsh)]
        [InlineData("-----BEGIN PRIVATE KEY-----\nx", PrivateKeyFormat.Pem)]
        [InlineData("-----BEGIN RSA PRIVATE KEY-----\nx", PrivateKeyFormat.Pem)]
        [InlineData("-----BEGIN EC PRIVATE KEY-----\nx", PrivateKeyFormat.Pem)]
        [InlineData("-----BEGIN DSA PRIVATE KEY-----\nx", PrivateKeyFormat.Pem)]
        [InlineData("-----BEGIN ENCRYPTED PRIVATE KEY-----\nx", PrivateKeyFormat.Pem)]
        public void Detect_KnownHeaders(string text, string expected)
        {
            Assert.Equal(expected, PrivateKeyFormat.Detect(Encoding.UTF8.GetBytes(text)));
        }

        [Fact]
        public void Detect_RejectsUnknownAndNonUtf8()
        {
            Assert.Null(PrivateKeyFormat.Detect(Encoding.UTF8.GetBytes("PuTTY-User-Key-File-3: ssh-ed25519\n")));
            Assert.Null(PrivateKeyFormat.Detect(Encoding.UTF8.GetBytes("-----BEGIN OPENSSH PRIVATE KEY-----"))); // 无换行
            Assert.Null(PrivateKeyFormat.Detect(new byte[] { 0xFF, 0xFE, 0x00, 0x01 }));
            Assert.Null(PrivateKeyFormat.Detect(null));
        }

        [Theory]
        [InlineData("SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", true)]  // 43 位
        [InlineData("SHA256:AAA", false)]
        [InlineData("sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", false)] // 大小写敏感
        [InlineData("SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", false)] // 不允许填充
        [InlineData(null, false)]
        public void FingerprintPattern(string value, bool expected)
        {
            Assert.Equal(expected, PrivateKeyFormat.IsValidFingerprint(value));
        }
    }
}
