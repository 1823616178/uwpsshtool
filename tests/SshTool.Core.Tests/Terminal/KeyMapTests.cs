using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    // 01-DESIGN.md §7.5 键位映射：逐行覆盖映射表
    // （方向/Home/End 两模式与修饰组合、~序列、F1–F12、Ctrl 特殊字符、Alt 前缀）。
    public class KeyMapTests
    {
        private static readonly TerminalModes Normal = new TerminalModes();
        private static readonly TerminalModes AppCursor =
            new TerminalModes { ApplicationCursorKeys = true };

        private static byte[] Map(TerminalKey key, TerminalModes modes = null)
        {
            return KeyMap.Map(new KeyChord(key), modes ?? Normal);
        }

        private static byte[] MapChar(char c, bool ctrl = false, bool alt = false, bool shift = false)
        {
            return KeyMap.Map(new KeyChord(TerminalKey.Char, c, ctrl, alt, shift), Normal);
        }

        private static byte[] B(params byte[] bytes)
        {
            return bytes;
        }

        // ---------- 方向键 ----------

        [Fact]
        public void Arrow_NormalMode()
        {
            Assert.Equal(B(0x1B, (byte)'[', (byte)'A'), Map(TerminalKey.Up));
            Assert.Equal(B(0x1B, (byte)'[', (byte)'B'), Map(TerminalKey.Down));
            Assert.Equal(B(0x1B, (byte)'[', (byte)'C'), Map(TerminalKey.Right));
            Assert.Equal(B(0x1B, (byte)'[', (byte)'D'), Map(TerminalKey.Left));
        }

        [Fact]
        public void Arrow_ApplicationCursorMode()
        {
            Assert.Equal(B(0x1B, (byte)'O', (byte)'A'), Map(TerminalKey.Up, AppCursor));
            Assert.Equal(B(0x1B, (byte)'O', (byte)'B'), Map(TerminalKey.Down, AppCursor));
            Assert.Equal(B(0x1B, (byte)'O', (byte)'C'), Map(TerminalKey.Right, AppCursor));
            Assert.Equal(B(0x1B, (byte)'O', (byte)'D'), Map(TerminalKey.Left, AppCursor));
        }

        [Theory]
        // m = 1 + shift + 2·alt + 4·ctrl
        [InlineData(false, false, true, "2")]   // shift
        [InlineData(false, true, false, "3")]   // alt
        [InlineData(true, false, false, "5")]   // ctrl
        [InlineData(false, true, true, "4")]    // shift+alt
        [InlineData(true, false, true, "6")]    // shift+ctrl
        [InlineData(true, true, false, "7")]    // ctrl+alt
        [InlineData(true, true, true, "8")]     // 全部
        public void Arrow_WithModifiers(bool ctrl, bool alt, bool shift, string m)
        {
            var bytes = KeyMap.Map(new KeyChord(TerminalKey.Up, '\0', ctrl, alt, shift), Normal);
            Assert.Equal(B(0x1B, (byte)'[', (byte)'1', (byte)';'), bytes.SubArray(0, 4));
            Assert.Equal(m, System.Text.Encoding.ASCII.GetString(bytes, 4, bytes.Length - 5));
            Assert.Equal((byte)'A', bytes[bytes.Length - 1]);
        }

        [Fact]
        public void Arrow_ModifiersIgnoreAppCursorMode()
        {
            // 带修饰时即使应用光标模式也用 CSI 1;mA（xterm 惯例）
            var bytes = KeyMap.Map(new KeyChord(TerminalKey.Up, '\0', true, false, false), AppCursor);
            Assert.Equal(B(0x1B, (byte)'[', (byte)'1', (byte)';', (byte)'5', (byte)'A'), bytes);
        }

        // ---------- Home/End ----------

        [Fact]
        public void HomeEnd_BothModes()
        {
            Assert.Equal(B(0x1B, (byte)'[', (byte)'H'), Map(TerminalKey.Home));
            Assert.Equal(B(0x1B, (byte)'[', (byte)'F'), Map(TerminalKey.End));
            Assert.Equal(B(0x1B, (byte)'O', (byte)'H'), Map(TerminalKey.Home, AppCursor));
            Assert.Equal(B(0x1B, (byte)'O', (byte)'F'), Map(TerminalKey.End, AppCursor));
        }

        [Fact]
        public void HomeEnd_WithModifiers()
        {
            Assert.Equal(B(0x1B, (byte)'[', (byte)'1', (byte)';', (byte)'5', (byte)'H'),
                KeyMap.Map(new KeyChord(TerminalKey.Home, '\0', true, false, false), Normal));
            Assert.Equal(B(0x1B, (byte)'[', (byte)'1', (byte)';', (byte)'3', (byte)'F'),
                KeyMap.Map(new KeyChord(TerminalKey.End, '\0', false, true, false), Normal));
        }

        // ---------- ~序列键 ----------

        [Fact]
        public void TildeKeys_Plain()
        {
            Assert.Equal(B(0x1B, (byte)'[', (byte)'5', (byte)'~'), Map(TerminalKey.PageUp));
            Assert.Equal(B(0x1B, (byte)'[', (byte)'6', (byte)'~'), Map(TerminalKey.PageDown));
            Assert.Equal(B(0x1B, (byte)'[', (byte)'2', (byte)'~'), Map(TerminalKey.Insert));
            Assert.Equal(B(0x1B, (byte)'[', (byte)'3', (byte)'~'), Map(TerminalKey.Delete));
        }

        [Fact]
        public void TildeKeys_WithModifiers()
        {
            Assert.Equal(B(0x1B, (byte)'[', (byte)'3', (byte)';', (byte)'5', (byte)'~'),
                KeyMap.Map(new KeyChord(TerminalKey.Delete, '\0', true, false, false), Normal));
        }

        // ---------- F1–F12 ----------

        [Fact]
        public void F1To4_Plain()
        {
            Assert.Equal(B(0x1B, (byte)'O', (byte)'P'), Map(TerminalKey.F1));
            Assert.Equal(B(0x1B, (byte)'O', (byte)'Q'), Map(TerminalKey.F2));
            Assert.Equal(B(0x1B, (byte)'O', (byte)'R'), Map(TerminalKey.F3));
            Assert.Equal(B(0x1B, (byte)'O', (byte)'S'), Map(TerminalKey.F4));
        }

        [Theory]
        [InlineData(TerminalKey.F5, 15)]
        [InlineData(TerminalKey.F6, 17)]
        [InlineData(TerminalKey.F7, 18)]
        [InlineData(TerminalKey.F8, 19)]
        [InlineData(TerminalKey.F9, 20)]
        [InlineData(TerminalKey.F10, 21)]
        [InlineData(TerminalKey.F11, 23)]
        [InlineData(TerminalKey.F12, 24)]
        public void F5ToF12_Plain(TerminalKey key, int n)
        {
            Assert.Equal("\x1b[" + n + "~", System.Text.Encoding.ASCII.GetString(Map(key)));
        }

        [Fact]
        public void FunctionKeys_WithModifiers()
        {
            Assert.Equal(B(0x1B, (byte)'[', (byte)'1', (byte)';', (byte)'2', (byte)'P'),
                KeyMap.Map(new KeyChord(TerminalKey.F1, '\0', false, false, true), Normal));
            Assert.Equal("\x1b[15;5~", System.Text.Encoding.ASCII.GetString(
                KeyMap.Map(new KeyChord(TerminalKey.F5, '\0', true, false, false), Normal)));
        }

        // ---------- Ctrl 特殊字符 ----------

        [Theory]
        [InlineData('a', 0x01)]
        [InlineData('A', 0x01)]   // 大小写不敏感
        [InlineData('c', 0x03)]
        [InlineData('z', 0x1A)]
        [InlineData(' ', 0x00)]   // Ctrl+Space
        [InlineData('@', 0x00)]   // Ctrl+@
        [InlineData('[', 0x1B)]
        [InlineData('\\', 0x1C)]
        [InlineData(']', 0x1D)]
        [InlineData('^', 0x1E)]
        [InlineData('_', 0x1F)]
        public void Ctrl_Combinations(char c, int expected)
        {
            Assert.Equal(B((byte)expected), MapChar(c, ctrl: true));
        }

        [Fact]
        public void Ctrl_Unmappable_ReturnsNull()
        {
            Assert.Null(MapChar('1', ctrl: true));
            Assert.Null(MapChar('中', ctrl: true));
            Assert.Null(KeyMap.Map(new KeyChord(TerminalKey.Char, '\0', true, false, false), Normal));
        }

        // ---------- Alt 前缀 ----------

        [Fact]
        public void Alt_PrintableChar_EscPrefix()
        {
            Assert.Equal(B(0x1B, (byte)'x'), MapChar('x', alt: true));
            // 非 ASCII 字符按 UTF-8 追加
            Assert.Equal(B(0x1B, 0xE4, 0xB8, 0xAD), MapChar('中', alt: true));
        }

        [Fact]
        public void AltCtrl_Char_EscPrefixCtrlByte()
        {
            Assert.Equal(B(0x1B, 0x03), MapChar('c', ctrl: true, alt: true));
        }

        [Fact]
        public void Alt_NamedKeys()
        {
            Assert.Equal(B(0x1B, 0x0D), KeyMap.Map(new KeyChord(TerminalKey.Enter, '\0', false, true, false), Normal));
            Assert.Equal(B(0x1B, 0x7F), KeyMap.Map(new KeyChord(TerminalKey.Backspace, '\0', false, true, false), Normal));
            Assert.Equal(B(0x1B, 0x1B), KeyMap.Map(new KeyChord(TerminalKey.Escape, '\0', false, true, false), Normal));
        }

        // ---------- 单键 ----------

        [Fact]
        public void Escape_Enter_Tab()
        {
            Assert.Equal(B(0x1B), Map(TerminalKey.Escape));
            Assert.Equal(B(0x0D), Map(TerminalKey.Enter));
            Assert.Equal(B(0x09), Map(TerminalKey.Tab));
        }

        [Fact]
        public void ShiftTab_Backtab()
        {
            Assert.Equal(B(0x1B, (byte)'[', (byte)'Z'),
                KeyMap.Map(new KeyChord(TerminalKey.Tab, '\0', false, false, true), Normal));
        }

        [Fact]
        public void Backspace_Configurable()
        {
            Assert.Equal(B(0x7F), Map(TerminalKey.Backspace)); // 默认 DEL
            Assert.Equal(B(0x08), KeyMap.Map(new KeyChord(TerminalKey.Backspace), Normal, backspaceAsBs: true));
        }

        // ---------- 不处理 ----------

        [Fact]
        public void PlainChar_NotMapped()
        {
            Assert.Null(MapChar('a'));                          // 纯字符走文本通路
            Assert.Null(MapChar('A', shift: true));             // 仅 Shift 也是文本通路
            Assert.Null(KeyMap.Map(new KeyChord(TerminalKey.None), Normal));
        }

        [Fact]
        public void NullChord_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() => KeyMap.Map(null, Normal));
        }
    }

    internal static class ByteArrayExtensions
    {
        public static byte[] SubArray(this byte[] data, int index, int length)
        {
            var result = new byte[length];
            System.Array.Copy(data, index, result, 0, length);
            return result;
        }
    }
}
