using SshTool.Core.Sftp;
using Xunit;

namespace SshTool.Core.Tests.Sftp
{
    public class PermissionBitsTests
    {
        [Theory]
        [InlineData(0x1FF, "777")]
        [InlineData(0x1ED, "755")]
        [InlineData(0x1A4, "644")]
        [InlineData(0x1C0, "700")]
        [InlineData(0, "000")]
        [InlineData(0x124, "444")]
        public void ToOctal_Cases(int mode, string expected)
        {
            Assert.Equal(expected, PermissionBits.ToOctal(mode));
        }

        [Fact]
        public void ToOctal_MasksHighBits()
        {
            // 含 S_IFMT 类型位时只取低 9 位（0100755 → "755"）。
            Assert.Equal("755", PermissionBits.ToOctal(0x81ED));
        }

        [Theory]
        [InlineData("755", 0x1ED)]
        [InlineData("0755", 0x1ED)]
        [InlineData("644", 0x1A4)]
        [InlineData("0644", 0x1A4)]
        [InlineData("000", 0)]
        [InlineData("777", 0x1FF)]
        [InlineData("400", 0x100)]
        public void TryParseOctal_Valid(string text, int expected)
        {
            int mode;
            Assert.True(PermissionBits.TryParseOctal(text, out mode));
            Assert.Equal(expected, mode);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("75")]
        [InlineData("7555")]
        [InlineData("1755")]   // 前导非零 4 位（setuid 位超出 9 位范围，拒绝）
        [InlineData("888")]
        [InlineData("78a")]
        [InlineData("rwx")]
        [InlineData(" 755")]
        [InlineData("755 ")]
        public void TryParseOctal_Invalid(string text)
        {
            int mode;
            Assert.False(PermissionBits.TryParseOctal(text, out mode));
            Assert.Equal(0, mode);
        }

        [Theory]
        [InlineData(0x1ED, "rwxr-xr-x")]
        [InlineData(0x1A4, "rw-r--r--")]
        [InlineData(0, "---------")]
        [InlineData(0x1FF, "rwxrwxrwx")]
        [InlineData(0x124, "r--r--r--")]
        public void ToRwx_Cases(int mode, string expected)
        {
            Assert.Equal(expected, PermissionBits.ToRwx(mode));
        }

        [Theory]
        [InlineData("rwxr-xr-x", 0x1ED)]
        [InlineData("rw-r--r--", 0x1A4)]
        [InlineData("---------", 0)]
        [InlineData("rwxrwxrwx", 0x1FF)]
        public void TryParseRwx_Valid(string text, int expected)
        {
            int mode;
            Assert.True(PermissionBits.TryParseRwx(text, out mode));
            Assert.Equal(expected, mode);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("rwxr-xr")]
        [InlineData("rwxr-xr-xx")]
        [InlineData("wwr-r--r--")]  // 首位只能 r/-
        [InlineData("rwxr-xr-X")]
        [InlineData("rwxrwxrwe")]
        public void TryParseRwx_Invalid(string text)
        {
            int mode;
            Assert.False(PermissionBits.TryParseRwx(text, out mode));
            Assert.Equal(0, mode);
        }

        [Theory]
        [InlineData(0x1ED)]
        [InlineData(0x1A4)]
        [InlineData(0)]
        [InlineData(0x1FF)]
        [InlineData(0x124)]
        [InlineData(0x092)]
        public void RoundTrip_OctalAndRwx(int mode)
        {
            int fromOctal;
            Assert.True(PermissionBits.TryParseOctal(PermissionBits.ToOctal(mode), out fromOctal));
            Assert.Equal(mode & PermissionBits.ModeMask, fromOctal);
            int fromRwx;
            Assert.True(PermissionBits.TryParseRwx(PermissionBits.ToRwx(mode), out fromRwx));
            Assert.Equal(mode & PermissionBits.ModeMask, fromRwx);
        }
    }
}
