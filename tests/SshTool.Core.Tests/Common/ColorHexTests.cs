using SshTool.Core.Common;
using Xunit;

namespace SshTool.Core.Tests.Common
{
    // O14：ColorHex.TryParse 单测。
    // 三个调用方（ColorSwatchPicker / GroupHeader / AppearanceListViewModel）
    // 原来各自写同一段解析逻辑，收口到此处后只需测一次。
    public class ColorHexTests
    {
        // --- 合法格式 ---

        [Theory]
        [InlineData("#000000", 0, 0, 0)]
        [InlineData("#FFFFFF", 255, 255, 255)]
        [InlineData("#4F8CFF", 0x4F, 0x8C, 0xFF)]
        [InlineData("#F4605F", 0xF4, 0x60, 0x5F)]
        [InlineData("#34C77B", 0x34, 0xC7, 0x7B)]
        public void TryParse_ValidUppercase_ReturnsTrue(string hex, byte er, byte eg, byte eb)
        {
            byte r, g, b;
            bool ok = ColorHex.TryParse(hex, out r, out g, out b);
            Assert.True(ok);
            Assert.Equal(er, r);
            Assert.Equal(eg, g);
            Assert.Equal(eb, b);
        }

        [Theory]
        [InlineData("#4f8cff", 0x4F, 0x8C, 0xFF)]
        [InlineData("#abcdef", 0xAB, 0xCD, 0xEF)]
        public void TryParse_ValidLowercase_ReturnsTrue(string hex, byte er, byte eg, byte eb)
        {
            byte r, g, b;
            bool ok = ColorHex.TryParse(hex, out r, out g, out b);
            Assert.True(ok);
            Assert.Equal(er, r);
            Assert.Equal(eg, g);
            Assert.Equal(eb, b);
        }

        // 大小写混合
        [Fact]
        public void TryParse_MixedCase_ReturnsTrue()
        {
            byte r, g, b;
            bool ok = ColorHex.TryParse("#aAbBcC", out r, out g, out b);
            Assert.True(ok);
            Assert.Equal(0xAA, r);
            Assert.Equal(0xBB, g);
            Assert.Equal(0xCC, b);
        }

        // --- 非法格式 ---

        [Theory]
        [InlineData(null)]           // null
        [InlineData("")]             // 空串
        [InlineData("123456")]       // 缺 '#' 前缀
        [InlineData("#12345")]       // 5 字符（不足）
        [InlineData("#1234567")]     // 7 字符但多了一位（8 位含 alpha 不支持）
        [InlineData("#GGGGGG")]      // 非十六进制字符
        [InlineData("#-1FFFF")]      // 含负号
        [InlineData("# 00000")]      // 含空格
        [InlineData("#FFF")]         // 3 位简写不支持
        [InlineData("#AARRGGBB")]    // 8 位 ARGB 不支持
        public void TryParse_Invalid_ReturnsFalse(string hex)
        {
            byte r, g, b;
            bool ok = ColorHex.TryParse(hex, out r, out g, out b);
            Assert.False(ok);
            // 失败时输出参数须置零
            Assert.Equal(0, r);
            Assert.Equal(0, g);
            Assert.Equal(0, b);
        }

        // 失败时输出参数置零（额外验证边界值）
        [Fact]
        public void TryParse_Invalid_OutputsAreZero()
        {
            byte r = 99;
            byte g = 99;
            byte b = 99;
            ColorHex.TryParse("invalid", out r, out g, out b);
            Assert.Equal(0, r);
            Assert.Equal(0, g);
            Assert.Equal(0, b);
        }
    }
}
