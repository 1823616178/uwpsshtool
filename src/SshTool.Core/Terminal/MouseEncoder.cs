using System;
using System.Collections.Generic;
using System.Globalization;

namespace SshTool.Core.Terminal
{
    // T14：滚轮编码。T15 补 X10/普通/按钮/任意移动与 SGR 完整表。
    public static class MouseEncoder
    {
        public const int WheelUpButton = 64;
        public const int WheelDownButton = 65;
        public const int X10MaxCoordinate = 223;

        public static byte[] EncodeWheel(bool up, int col1, int row1, bool sgr)
        {
            int x = col1 < 1 ? 1 : col1;
            int y = row1 < 1 ? 1 : row1;
            int button = up ? WheelUpButton : WheelDownButton;
            if (sgr)
            {
                string seq = "\x1b[<" + button.ToString(CultureInfo.InvariantCulture)
                    + ";" + x.ToString(CultureInfo.InvariantCulture)
                    + ";" + y.ToString(CultureInfo.InvariantCulture) + "M";
                return Ascii(seq);
            }
            if (x > X10MaxCoordinate) { x = X10MaxCoordinate; }
            if (y > X10MaxCoordinate) { y = X10MaxCoordinate; }
            return new byte[]
            {
                0x1B, (byte)'[', (byte)'M',
                (byte)(button + 32),
                (byte)(x + 32),
                (byte)(y + 32)
            };
        }

        // lines > 0 = 手指下移 / 滚轮上 = 看旧内容 = ↑ / 64。
        public static IReadOnlyList<byte[]> EncodeAltScroll(int lines, bool wheelMode,
                                                            int mouseMode, bool sgr,
                                                            int col1, int row1,
                                                            TerminalModes modes)
        {
            if (lines == 0)
            {
                return new byte[0][];
            }
            bool up = lines > 0;
            int count = up ? lines : -lines;
            bool useMouse = wheelMode && mouseMode != 0;
            var result = new List<byte[]>(count);
            TerminalKey key = up ? TerminalKey.Up : TerminalKey.Down;
            for (int i = 0; i < count; i++)
            {
                if (useMouse)
                {
                    result.Add(EncodeWheel(up, col1, row1, sgr));
                }
                else
                {
                    result.Add(KeyMap.Map(new KeyChord(key), modes ?? new TerminalModes()));
                }
            }
            return result;
        }

        private static byte[] Ascii(string text)
        {
            var bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                bytes[i] = (byte)text[i];
            }
            return bytes;
        }
    }
}
