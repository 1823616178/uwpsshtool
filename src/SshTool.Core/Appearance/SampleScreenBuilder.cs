using System;
using System.Collections.Generic;
using SshTool.Core.Models;
using SshTool.Core.Terminal;

namespace SshTool.Core.Appearance
{
    // A03：外观预览用的固定样例屏（02-UI-DESIGN.md §5.12）。
    // 把样例文本编码为 16 字节单元格缓冲（01-DESIGN.md §7.1，小端），供
    // StaticTerminalScreen + TerminalView 做实时预览。颜色取被预览外观的
    // 真实 ARGB（调色板 16 色 + 前景/背景），因此改色后重建一次缓冲即可
    // 看到效果（36×8 格，重建 < 1 ms，满足 ≤100 ms 实时要求）。
    public struct SampleScreen
    {
        public byte[] Cells;
        public int Cols;
        public int Rows;
        public int CursorRow;
        public int CursorCol;
    }

    public static class SampleScreenBuilder
    {
        public const int DefaultCols = 36;
        public const int DefaultRows = 8;

        public static SampleScreen Build(AppearanceProfile appearance)
        {
            return Build(appearance, DefaultCols, DefaultRows);
        }

        public static SampleScreen Build(AppearanceProfile appearance, int cols, int rows)
        {
            if (appearance == null)
            {
                throw new ArgumentNullException("appearance");
            }
            if (cols <= 0)
            {
                throw new ArgumentOutOfRangeException("cols");
            }
            if (rows <= 0)
            {
                throw new ArgumentOutOfRangeException("rows");
            }
            uint[] palette = TerminalPalette.PaletteToArgb(appearance.Palette);
            uint fg = TerminalPalette.HexToArgb(appearance.Foreground);
            uint bg = TerminalPalette.HexToArgb(appearance.Background);

            var writer = new ScreenWriter(cols, rows, fg, bg);
            writer.WriteRow(0, new StyledSpan[] { Span("user@host:~$ ls --color", fg, bg, 0) });
            writer.WriteRow(1, new StyledSpan[]
            {
                Span("dir", palette[4], bg, 0),
                Span("  file.txt  ", fg, bg, 0),
                Span("run.sh", palette[2], bg, 0)
            });
            writer.WriteRow(2, PaletteRow(palette, 0, fg, bg));
            writer.WriteRow(3, PaletteRow(palette, 8, fg, bg));
            writer.WriteRow(4, new StyledSpan[] { Span("Bold 粗体 ABC", fg, bg, TerminalCell.AttrBold) });
            writer.WriteRow(5, new StyledSpan[] { Span("Underline 下划线 ABC", fg, bg, TerminalCell.AttrUnderline) });
            writer.WriteRow(6, new StyledSpan[] { Span("Reverse 反色 ABC", fg, bg, TerminalCell.AttrReverse) });
            string prompt = "user@host:~$ ";
            writer.WriteRow(7, new StyledSpan[] { Span(prompt, fg, bg, 0) });

            return new SampleScreen
            {
                Cells = writer.Buffer,
                Cols = cols,
                Rows = rows,
                CursorRow = rows > 7 ? 7 : 0,
                CursorCol = rows > 7 ? CellLength(prompt) : 0
            };
        }

        // 一行 8 个色块：数字 0–7 分别取 palette[base..base+7] 做前景。
        private static StyledSpan[] PaletteRow(uint[] palette, int baseIndex, uint fg, uint bg)
        {
            var spans = new List<StyledSpan>(8);
            for (int i = 0; i < 8; i++)
            {
                string digit = ((char)('0' + i)).ToString();
                spans.Add(Span(digit + " ", palette[baseIndex + i], bg, 0));
            }
            return spans.ToArray();
        }

        private static StyledSpan Span(string text, uint fg, uint bg, ushort attrs)
        {
            return new StyledSpan { Text = text, Fg = fg, Bg = bg, Attrs = attrs };
        }

        // 终端列宽（宽字符占 2 列），与光标列计算共用。
        internal static int CellLength(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }
            int width = 0;
            for (int i = 0; i < text.Length; i++)
            {
                uint cp;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    cp = (uint)char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                }
                else
                {
                    cp = text[i];
                }
                width += IsWide(cp) ? 2 : 1;
            }
            return width;
        }

        // 宽字符判定（终端按 2 格定位，与 T06 渲染一致）。
        internal static bool IsWide(uint cp)
        {
            return (cp >= 0x1100 && cp <= 0x115F)
                || (cp >= 0x2E80 && cp <= 0x303E)
                || (cp >= 0x3041 && cp <= 0x33FF)
                || (cp >= 0x3400 && cp <= 0x4DBF)
                || (cp >= 0x4E00 && cp <= 0x9FFF)
                || (cp >= 0xA000 && cp <= 0xA4CF)
                || (cp >= 0xAC00 && cp <= 0xD7AF)
                || (cp >= 0xF900 && cp <= 0xFAFF)
                || (cp >= 0xFE30 && cp <= 0xFE4F)
                || (cp >= 0xFF00 && cp <= 0xFF60)
                || (cp >= 0xFFE0 && cp <= 0xFFE6)
                || (cp >= 0x20000 && cp <= 0x3FFFD)
                || (cp >= 0x1F300 && cp <= 0x1FAFF);
        }

        private struct StyledSpan
        {
            public string Text;
            public uint Fg;
            public uint Bg;
            public ushort Attrs;
        }

        private sealed class ScreenWriter
        {
            private readonly byte[] _buffer;
            private readonly int _cols;
            private readonly int _rows;
            private readonly uint _fg;
            private readonly uint _bg;

            public ScreenWriter(int cols, int rows, uint fg, uint bg)
            {
                _cols = cols;
                _rows = rows;
                _fg = fg;
                _bg = bg;
                _buffer = new byte[cols * rows * TerminalCell.BytesPerCell];
                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < cols; c++)
                    {
                        WriteCell(r, c, 0, TerminalCell.DefaultFgMarker, TerminalCell.DefaultBgMarker, 0);
                    }
                }
            }

            public byte[] Buffer
            {
                get { return _buffer; }
            }

            public void WriteRow(int row, StyledSpan[] spans)
            {
                if (row < 0 || row >= _rows || spans == null)
                {
                    return;
                }
                int col = 0;
                for (int s = 0; s < spans.Length && col < _cols; s++)
                {
                    col = WriteSpan(row, col, spans[s]);
                }
            }

            private int WriteSpan(int row, int col, StyledSpan span)
            {
                string text = span.Text ?? string.Empty;
                for (int i = 0; i < text.Length && col < _cols; i++)
                {
                    uint cp;
                    if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        cp = (uint)char.ConvertToUtf32(text[i], text[i + 1]);
                        i++;
                    }
                    else
                    {
                        cp = text[i];
                    }
                    ushort attrs = span.Attrs;
                    if (IsWide(cp))
                    {
                        attrs |= TerminalCell.AttrWide;
                    }
                    WriteCell(row, col, cp, span.Fg, span.Bg, attrs);
                    col++;
                    if ((attrs & TerminalCell.AttrWide) != 0 && col < _cols)
                    {
                        WriteCell(row, col, TerminalCell.WideContinuation, span.Fg, span.Bg, 0);
                        col++;
                    }
                }
                return col;
            }

            private void WriteCell(int row, int col, uint cp, uint fg, uint bg, ushort attrs)
            {
                int offset = (row * _cols + col) * TerminalCell.BytesPerCell;
                WriteU32(_buffer, offset, cp);
                WriteU32(_buffer, offset + 4, fg);
                WriteU32(_buffer, offset + 8, bg);
                WriteU16(_buffer, offset + 12, attrs);
                WriteU16(_buffer, offset + 14, 0);
            }

            private static void WriteU32(byte[] buffer, int offset, uint value)
            {
                buffer[offset] = (byte)value;
                buffer[offset + 1] = (byte)(value >> 8);
                buffer[offset + 2] = (byte)(value >> 16);
                buffer[offset + 3] = (byte)(value >> 24);
            }

            private static void WriteU16(byte[] buffer, int offset, ushort value)
            {
                buffer[offset] = (byte)value;
                buffer[offset + 1] = (byte)(value >> 8);
            }
        }
    }
}
