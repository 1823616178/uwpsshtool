using System;
using SshTool.Core.Sync.Vault;
using Xunit;

namespace SshTool.Core.Tests.Sync.Vault
{
    // U16：恢复密钥输入规范化与校验（02-UI-DESIGN.md §5.13 VaultUnlockPage）。
    // 格式/校验段与桌面端 crypto-vault.ts decodeRecoveryKey 对拍：valid 基准用
    // tests/fixtures/sync/desktop-vectors.json 冻结的 recoveryKey（S04 由 node:crypto
    // 独立生成并经桌面端自检解开），本实现的 SHA256 复算必须与之逐字一致。
    public class RecoveryKeyInputTests
    {
        // 冻结的桌面端向量（raw = 60..7f，校验段 SHA256("SPM1"||raw)[0..12]）。
        private const string DesktopVectorKey = "SPM1-YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8-0F621092518E";

        [Fact]
        public void Normalize_DesktopVector_Unchanged()
        {
            Assert.Equal(DesktopVectorKey, RecoveryKeyInput.Normalize(DesktopVectorKey));
        }

        [Theory]
        [InlineData("spm1-YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8-0F621092518E")]
        [InlineData("Spm1-YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8-0F621092518E")]
        [InlineData("SPM1-YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8-0f621092518e")]
        public void Normalize_LowercasePrefixAndChecksum_Uppercased(string input)
        {
            // 前缀与校验段大小写不敏感；正文段保持原样。
            Assert.Equal(DesktopVectorKey, RecoveryKeyInput.Normalize(input));
        }

        [Theory]
        [InlineData("SPM1-YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8-0F621092518E ")]
        [InlineData("  SPM1-YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8-0F621092518E")]
        [InlineData("SPM1-\nYGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8-\n0F621092518E")]
        [InlineData("SPM1- YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8 -0F621092518E")]
        public void Normalize_WhitespaceStripped(string input)
        {
            // 去掉全部空白（含换行）：分行粘贴不拼段也能过。
            Assert.Equal(DesktopVectorKey, RecoveryKeyInput.Normalize(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   \r\n ")]
        public void Normalize_Empty_ReturnsNull(string input)
        {
            Assert.Null(RecoveryKeyInput.Normalize(input));
        }

        [Theory]
        [InlineData("SPM1-YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8-0F621092518")]   // 校验段 11 位
        [InlineData("SPM1-YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn-0F621092518E")]    // 正文 42 位
        [InlineData("SPM1-YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8x-0F621092518E")]  // 正文 44 位
        [InlineData("SPM1-YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8+0F621092518E")]   // '+' 不是 base64url
        [InlineData("SPM1-YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8-0F621092518G")]   // 校验段含非 hex
        [InlineData("SPM1-YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8")]                // 缺校验段
        [InlineData("SPM1-YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8-0F621092518EX")]  // 校验段 13 位
        [InlineData("SPM2-YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8-0F621092518E")]   // 前缀错
        [InlineData("XYZ1-YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8-0F621092518E")]   // 前缀错
        [InlineData("SPM1-YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8-0F621092518E-X")] // 多余尾段
        public void HasValidFormat_InvalidShape_ReturnsFalse(string normalized)
        {
            Assert.False(RecoveryKeyInput.HasValidFormat(RecoveryKeyInput.Normalize(normalized) ?? normalized));
        }

        [Fact]
        public void HasValidFormat_Empty_ReturnsFalse()
        {
            Assert.False(RecoveryKeyInput.HasValidFormat(null));
            Assert.False(RecoveryKeyInput.HasValidFormat(string.Empty));
        }

        [Fact]
        public void VerifyChecksum_DesktopVector_Passes()
        {
            // 跨端对拍关键用例：复算结果与桌面端冻结校验段一致。
            Assert.True(RecoveryKeyInput.VerifyChecksum(DesktopVectorKey));
        }

        [Fact]
        public void VerifyChecksum_TamperedChecksum_Fails()
        {
            string tampered = "SPM1-YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8-0F621092519E";
            Assert.False(RecoveryKeyInput.VerifyChecksum(tampered));
        }

        [Fact]
        public void VerifyChecksum_TamperedBody_Fails()
        {
            // 正文是 base64url 大小写敏感：改动一个字符就改变 raw → 校验段不再匹配
            //（仍能过格式，因此失败来自校验段复算而非格式）。
            string tampered = "SPM1-yGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8-0F621092518E";
            Assert.True(RecoveryKeyInput.HasValidFormat(tampered));
            Assert.False(RecoveryKeyInput.VerifyChecksum(tampered));
        }

        [Fact]
        public void VerifyChecksum_Empty_ReturnsFalse()
        {
            Assert.False(RecoveryKeyInput.VerifyChecksum(null));
            Assert.False(RecoveryKeyInput.VerifyChecksum(string.Empty));
        }

        [Theory]
        [InlineData(null, "Vault_RecoveryFormatInvalid")]
        [InlineData("", "Vault_RecoveryFormatInvalid")]
        [InlineData("SPM1-short-0F621092518E", "Vault_RecoveryFormatInvalid")]
        public void Validate_BadShape_ReturnsFormatInvalid(string input, string expected)
        {
            string normalized;
            Assert.Equal(expected, RecoveryKeyInput.Validate(input, out normalized));
            Assert.NotEqual(DesktopVectorKey, normalized);
        }

        [Fact]
        public void Validate_DesktopVector_ReturnsNullAndNormalized()
        {
            string normalized;
            // 正文段（大小写敏感）原样保留，仅前缀与校验段被归一。
            Assert.Null(RecoveryKeyInput.Validate("  spm1-YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8-0f621092518e\n", out normalized));
            Assert.Equal(DesktopVectorKey, normalized);
        }

        [Fact]
        public void Validate_WrongChecksum_ReturnsChecksumFailed()
        {
            string normalized;
            Assert.Equal(
                RecoveryKeyInput.ChecksumFailedKey,
                RecoveryKeyInput.Validate("SPM1-YGFiY2RlZmdoaWprbG1ub3BxcnN0dXZ3eHl6e3x9fn8-0F621092519E", out normalized));
            Assert.NotNull(normalized);
        }

        [Fact]
        public void AllErrorKeys_AreExactlyTheTwoDocumentedKeys()
        {
            Assert.Equal(
                new string[] { "Vault_RecoveryFormatInvalid", "Vault_RecoveryChecksumFailed" },
                RecoveryKeyInput.AllErrorKeys);
        }

        [Fact]
        public void DisplayLines_DesktopVector_ThreeSemanticLines()
        {
            string[] lines = RecoveryKeyInput.DisplayLines(DesktopVectorKey);
            Assert.NotNull(lines);
            Assert.Equal(3, lines.Length);
            Assert.Equal("SPM1-YGFiY2RlZmdoaWpr", lines[0]);   // 前缀 + 正文前 16
            Assert.Equal("bG1ub3BxcnN0dXZ3eHl6e3x9fn8", lines[1]); // 正文余 27
            Assert.Equal("-0F621092518E", lines[2]);           // "-" + 校验段
            Assert.Equal(DesktopVectorKey, lines[0] + lines[1] + lines[2]);
        }

        [Fact]
        public void DisplayLines_InvalidInput_ReturnsNull()
        {
            Assert.Null(RecoveryKeyInput.DisplayLines("not-a-key"));
            Assert.Null(RecoveryKeyInput.DisplayLines(null));
        }

        [Fact]
        public void LengthConstants_MatchDesktopRegex()
        {
            Assert.Equal(43, RecoveryKeyInput.BodyLength);
            Assert.Equal(12, RecoveryKeyInput.ChecksumLength);
            Assert.Equal(61, RecoveryKeyInput.TotalLength);
        }
    }
}
