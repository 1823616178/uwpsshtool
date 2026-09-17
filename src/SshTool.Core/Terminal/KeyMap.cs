using System;
using System.Text;

namespace SshTool.Core.Terminal
{
    // 01-DESIGN.md §7.5 键位映射：纯函数，返回要发给远端的字节；返回 null 表示
    // 「不由 KeyMap 处理」（纯可打印字符走文本输入通路、无法映射的 Ctrl 组合等）。
    public static class KeyMap
    {
        private const byte Esc = 0x1B;

        // backspaceAsBs：false=DEL(0x7F)（默认），true=BS(0x08)（设置项）
        public static byte[] Map(KeyChord chord, TerminalModes modes, bool backspaceAsBs = false)
        {
            if (chord == null)
            {
                throw new ArgumentNullException(nameof(chord));
            }
            modes = modes ?? new TerminalModes();
            switch (chord.Key)
            {
                case TerminalKey.Up: return Arrow("A", chord, modes);
                case TerminalKey.Down: return Arrow("B", chord, modes);
                case TerminalKey.Right: return Arrow("C", chord, modes);
                case TerminalKey.Left: return Arrow("D", chord, modes);
                case TerminalKey.Home: return HomeEnd("H", chord, modes);
                case TerminalKey.End: return HomeEnd("F", chord, modes);
                case TerminalKey.PageUp: return TildeKey(5, chord);
                case TerminalKey.PageDown: return TildeKey(6, chord);
                case TerminalKey.Insert: return TildeKey(2, chord);
                case TerminalKey.Delete: return TildeKey(3, chord);
                case TerminalKey.F1: return F1To4("P", chord);
                case TerminalKey.F2: return F1To4("Q", chord);
                case TerminalKey.F3: return F1To4("R", chord);
                case TerminalKey.F4: return F1To4("S", chord);
                case TerminalKey.F5: return TildeKey(15, chord);
                case TerminalKey.F6: return TildeKey(17, chord);
                case TerminalKey.F7: return TildeKey(18, chord);
                case TerminalKey.F8: return TildeKey(19, chord);
                case TerminalKey.F9: return TildeKey(20, chord);
                case TerminalKey.F10: return TildeKey(21, chord);
                case TerminalKey.F11: return TildeKey(23, chord);
                case TerminalKey.F12: return TildeKey(24, chord);
                case TerminalKey.Escape: return AltPrefix(chord, new byte[] { Esc });
                case TerminalKey.Enter: return AltPrefix(chord, new byte[] { 0x0D });
                case TerminalKey.Backspace:
                    return AltPrefix(chord, new byte[] { backspaceAsBs ? (byte)0x08 : (byte)0x7F });
                case TerminalKey.Tab:
                    // Shift+Tab 反向制表；Ctrl/Alt+Tab 无文档序列，Ctrl 按普通 Tab 处理
                    return chord.Shift
                        ? Bytes("\x1b[Z")
                        : AltPrefix(chord, new byte[] { 0x09 });
                case TerminalKey.Char: return MapChar(chord);
                default: return null;
            }
        }

        // m = 1 + shift + 2·alt + 4·ctrl（xterm 修饰参数）
        private static int ModifierParam(KeyChord chord)
        {
            return 1
                + (chord.Shift ? 1 : 0)
                + (chord.Alt ? 2 : 0)
                + (chord.Ctrl ? 4 : 0);
        }

        private static byte[] Arrow(string final, KeyChord chord, TerminalModes modes)
        {
            int m = ModifierParam(chord);
            if (m == 1)
            {
                return Bytes(modes.ApplicationCursorKeys ? "\x1bO" + final : "\x1b[" + final);
            }
            return Bytes("\x1b[1;" + m + final);
        }

        private static byte[] HomeEnd(string final, KeyChord chord, TerminalModes modes)
        {
            int m = ModifierParam(chord);
            if (m == 1)
            {
                return Bytes(modes.ApplicationCursorKeys ? "\x1bO" + final : "\x1b[" + final);
            }
            return Bytes("\x1b[1;" + m + final);
        }

        private static byte[] TildeKey(int n, KeyChord chord)
        {
            int m = ModifierParam(chord);
            if (m == 1)
            {
                return Bytes("\x1b[" + n + "~");
            }
            return Bytes("\x1b[" + n + ";" + m + "~");
        }

        private static byte[] F1To4(string final, KeyChord chord)
        {
            int m = ModifierParam(chord);
            if (m == 1)
            {
                return Bytes("\x1bO" + final);
            }
            return Bytes("\x1b[1;" + m + final);
        }

        private static byte[] MapChar(KeyChord chord)
        {
            char c = chord.Character;
            if (c == '\0')
            {
                return null;
            }
            if (chord.Ctrl)
            {
                var ctrl = CtrlByte(c);
                if (ctrl == null)
                {
                    return null; // 无法映射的 Ctrl 组合（如 Ctrl+1）：不发任何字节
                }
                return AltPrefix(chord, new byte[] { ctrl.Value });
            }
            if (chord.Alt)
            {
                // Alt+可打印字符 → ESC + 字符 UTF-8 字节
                return Prefix(Esc, Encoding.UTF8.GetBytes(new string(c, 1)));
            }
            return null; // 纯字符（含仅 Shift）走文本输入通路
        }

        // §7.5 Ctrl 特殊字符表（字母大小写不敏感）
        private static byte? CtrlByte(char c)
        {
            char upper = char.ToUpperInvariant(c);
            if (upper >= 'A' && upper <= 'Z')
            {
                return (byte)(upper & 0x1F);
            }
            switch (c)
            {
                case ' ':
                case '@': return 0x00;
                case '[': return 0x1B;
                case '\\': return 0x1C;
                case ']': return 0x1D;
                case '^': return 0x1E;
                case '_': return 0x1F;
                default: return null;
            }
        }

        // 字节序列键的 Alt 处理：ESC 前缀（CSI/SS3 键已在 m 里编码 alt，不走这里）
        private static byte[] AltPrefix(KeyChord chord, byte[] body)
        {
            return chord.Alt ? Prefix(Esc, body) : body;
        }

        private static byte[] Prefix(byte prefix, byte[] body)
        {
            var result = new byte[body.Length + 1];
            result[0] = prefix;
            Array.Copy(body, 0, result, 1, body.Length);
            return result;
        }

        private static byte[] Bytes(string ascii)
        {
            var bytes = new byte[ascii.Length];
            for (int i = 0; i < ascii.Length; i++)
            {
                bytes[i] = (byte)ascii[i];
            }
            return bytes;
        }
    }
}
