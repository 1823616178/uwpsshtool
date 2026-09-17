using SshTool.Core.Common;
using Xunit;

namespace SshTool.Core.Tests.Common
{
    // 01-DESIGN.md §12.2：每种脱敏模式都要有覆盖（X04 验收 ≥12 条）。
    public class LogRedactorTests
    {
        [Fact]
        public void KeyValue_Password_IsRedacted()
        {
            Assert.Equal("connect password=*** done", LogRedactor.Redact("connect password=hunter2 done"));
        }

        [Fact]
        public void KeyValue_KeyCaseInsensitive_CasePreserved()
        {
            Assert.Equal("PASSWORD=***", LogRedactor.Redact("PASSWORD=abc"));
        }

        [Fact]
        public void KeyValue_QuotedValue_IsRedacted()
        {
            Assert.Equal("syncPassword=***", LogRedactor.Redact("syncPassword=\"my secret pass\""));
        }

        [Fact]
        public void KeyValue_Token_AmpersandTerminated()
        {
            Assert.Equal("token=***&user=kim", LogRedactor.Redact("token=tok_123&user=kim"));
        }

        [Fact]
        public void KeyValue_RefreshToken_IsRedacted()
        {
            Assert.Equal("refreshToken=***", LogRedactor.Redact("refreshToken=rt-987"));
        }

        [Fact]
        public void Json_Password_IsRedacted()
        {
            Assert.Equal("{\"password\":\"***\",\"u\":\"kim\"}",
                LogRedactor.Redact("{\"password\":\"abc123\",\"u\":\"kim\"}"));
        }

        [Fact]
        public void Json_KeyCaseInsensitive_WithSpaces()
        {
            Assert.Equal("{\"PassPhrase\":\"***\"}",
                LogRedactor.Redact("{\"PassPhrase\"  :  \"two words\"}"));
        }

        [Fact]
        public void Json_AccessToken_IsRedacted()
        {
            Assert.Equal("{\"accessToken\":\"***\"}", LogRedactor.Redact("{\"accessToken\":\"at-1\"}"));
        }

        [Fact]
        public void KeyValue_PrivateKey_RecoveryKey_Ciphertext()
        {
            Assert.Equal("privateKey=*** recoveryKey=*** ciphertext=***",
                LogRedactor.Redact("privateKey=k1 recoveryKey=k2 ciphertext=k3"));
        }

        [Fact]
        public void Authorization_Header_Bearer_IsRedacted()
        {
            Assert.Equal("Authorization: Bearer ***",
                LogRedactor.Redact("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig"));
        }

        [Fact]
        public void PemBlock_IsRedacted()
        {
            string pem = "-----BEGIN OPENSSH PRIVATE KEY-----\nabc123\ndef456\n-----END OPENSSH PRIVATE KEY-----";
            Assert.Equal("key: *** end", LogRedactor.Redact("key: " + pem + " end"));
        }

        [Fact]
        public void SpmRecoveryKey_IsRedacted()
        {
            string spm = "SPM1-" + new string('a', 43) + "-0123456789ab";
            Assert.Equal("recovery: ***", LogRedactor.Redact("recovery: " + spm));
        }

        [Fact]
        public void NoSecrets_Unchanged()
        {
            const string text = "connect host=192.168.1.1 user=kim port=22";
            Assert.Equal(text, LogRedactor.Redact(text));
        }

        [Fact]
        public void PublicValues_NotTreatedAsKeys()
        {
            const string text = "tokenize=true passwords=3";
            Assert.Equal(text, LogRedactor.Redact(text));
        }

        [Fact]
        public void NullAndEmpty_Unchanged()
        {
            Assert.Null(LogRedactor.Redact(null));
            Assert.Equal("", LogRedactor.Redact(""));
        }
    }
}
