using System;
using System.Collections.Generic;
using System.Globalization;

namespace SshTool.Core.Terminal
{
    public enum MouseEventKind
    {
        Press = 0,
        Release = 1,
        Move = 2,
        Wheel = 3
    }

    // 01-DESIGN.md §7.6：X10(9)/1000/1002/1003 × 默认与 SGR(1006)。
    public static class MouseEncoder
    {
        public const int WheelUpButton = 64;
        public const int WheelDownButton = 65;
        public const int LeftButton = 0;
        public const int MiddleButton = 1;
        public const int RightButton = 2;
        public const int X10MaxCoordinate = 223;
        public const int ModeOff = 0;
        public const int ModeX10 = 9;
        public const int ModeNormal = 1000;
        public const int ModeButtonEvent = 1002;
        public const int ModeAnyEvent = 1003;

        public static bool ShouldReport(int mouseMode, MouseEventKind kind, bool buttonDown)
        {
            if (mouseMode == ModeOff)
            {
                return false;
            }
            switch (kind)
            {
                case MouseEventKind.Press:
                case MouseEventKind.Wheel:
                    return true;
                case MouseEventKind.Release:
                    return mouseMode != ModeX10;
                case MouseEventKind.Move:
                    if (mouseMode == ModeAnyEvent)
                    {
                        return true;
                    }
                    return mouseMode == ModeButtonEvent && buttonDown;
                default:
                    return false;
            }
        }

        public static byte[] Encode(int mouseMode, bool sgr, MouseEventKind kind,
                                    int button, int col1, int row1,
                                    bool shift, bool alt, bool ctrl)
        {
            int x = col1 < 1 ? 1 : col1;
            int y = row1 < 1 ? 1 : row1;
            int encodedButton = button;
            if (kind == MouseEventKind.Move)
            {
                encodedButton += 32;
            }
            if (shift) { encodedButton += 4; }
            if (alt) { encodedButton += 8; }
            if (ctrl) { encodedButton += 16; }

            if (sgr)
            {
                char final = kind == MouseEventKind.Release ? 'm' : 'M';
                string seq = "\x1b[<"
                    + encodedButton.ToString(CultureInfo.InvariantCulture)
                    + ";" + x.ToString(CultureInfo.InvariantCulture)
                    + ";" + y.ToString(CultureInfo.InvariantCulture)
                    + final;
                return Ascii(seq);
            }

            int x10Button = encodedButton;
            if (kind == MouseEventKind.Release)
            {
                x10Button = 3;
                if (shift) { x10Button += 4; }
                if (alt) { x10Button += 8; }
                if (ctrl) { x10Button += 16; }
            }
            if (x > X10MaxCoordinate) { x = X10MaxCoordinate; }
            if (y > X10MaxCoordinate) { y = X10MaxCoordinate; }
            return new byte[]
            {
                0x1B, (byte)'[', (byte)'M',
                (byte)(x10Button + 32),
                (byte)(x + 32),
                (byte)(y + 32)
            };
        }

        public static byte[] EncodeWheel(bool up, int col1, int row1, bool sgr)
        {
            return Encode(ModeNormal, sgr, MouseEventKind.Wheel,
                up ? WheelUpButton : WheelDownButton, col1, row1, false, false, false);
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
            bool useMouse = wheelMode && mouseMode != ModeOff;
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

    public sealed class CellHitEventArgs : EventArgs
    {
        public CellHitEventArgs(int row, int col)
        {
            Row = row;
            Col = col;
        }

        public int Row { get; private set; }
        public int Col { get; private set; }
    }
}
