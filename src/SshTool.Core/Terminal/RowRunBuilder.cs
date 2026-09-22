using System.Collections.Generic;
using System.Text;

namespace SshTool.Core.Terminal
{
    // T05：一行单元格合并为绘制段（01-DESIGN.md §7.3）。
    // 宽字符单独成段（长度 2）；空格/空单元格打断文本；行尾默认色空白不产出段。
    public sealed class CellRun
    {
        public int StartCol;
        public int Length;
        public uint FgArgb;
        public uint BgArgb;
        public ushort Attrs;
        public string Text;
    }

    public static class RowRunBuilder
    {
        // 合并时忽略 wide/soft-wrap：wide 已单独成段；soft-wrap 只标在行末格。
        public const ushort StyleMask = unchecked((ushort)~(TerminalCell.AttrWide | TerminalCell.AttrSoftWrap));

        // 便利重载：每次新建表。测试与非热路径用。
        public static List<CellRun> Build(byte[] buffer, int row, int cols)
        {
            var runs = new List<CellRun>();
            Build(buffer, row, cols, runs);
            return runs;
        }

        // O11：渲染热路径用这个——调用方持有一张表反复填，省掉「每脏行每帧一个
        // List」的分配（48×30 满屏重绘时每帧 30 个）。
        //
        // 只复用表本身，不池化 CellRun 与文本串：那要把 DropTrailingDefaultBlanks
        // 的删尾改成游标回退、并让 CellRun 全程可变，出错面明显变大，而它省下的
        // 是 gen0 小对象——比起 DrawGlyphRun 里每段一个 Win2D COM 对象（本任务另一
        // 半修的），收益小得多。
        public static void Build(byte[] buffer, int row, int cols, List<CellRun> runs)
        {
            if (runs == null)
            {
                return;
            }
            runs.Clear();
            if (buffer == null || cols <= 0 || row < 0)
            {
                return;
            }
            int baseIndex = row * cols;
            if ((baseIndex + cols) * TerminalCell.BytesPerCell > buffer.Length)
            {
                return;
            }

            int col = 0;
            while (col < cols)
            {
                TerminalCell cell = CellBufferReader.Read(buffer, baseIndex + col);
                if (cell.IsWideContinuation)
                {
                    col++;
                    continue;
                }

                if (cell.IsWide)
                {
                    int span = (col + 1 < cols) ? 2 : 1;
                    runs.Add(MakeRun(col, span, cell, cell.Glyph()));
                    col += span;
                    continue;
                }

                if (cell.IsEmpty)
                {
                    col = EmitEmptyBgRun(buffer, baseIndex, cols, col, runs);
                    continue;
                }

                int start = col;
                uint fg = cell.FgArgb;
                uint bg = cell.BgArgb;
                ushort style = (ushort)(cell.Attrs & StyleMask);
                var text = new StringBuilder();
                text.Append(cell.Glyph());
                col++;
                while (col < cols)
                {
                    TerminalCell next = CellBufferReader.Read(buffer, baseIndex + col);
                    if (next.IsWide || next.IsWideContinuation || next.IsEmpty)
                    {
                        break;
                    }
                    if (next.FgArgb != fg || next.BgArgb != bg || (next.Attrs & StyleMask) != style)
                    {
                        break;
                    }
                    text.Append(next.Glyph());
                    col++;
                }
                runs.Add(new CellRun
                {
                    StartCol = start,
                    Length = col - start,
                    FgArgb = fg,
                    BgArgb = bg,
                    Attrs = (ushort)(cell.Attrs & ~TerminalCell.AttrSoftWrap),
                    Text = text.ToString()
                });
            }

            DropTrailingDefaultBlanks(runs);
        }

        private static int EmitEmptyBgRun(byte[] buffer, int baseIndex, int cols, int start, List<CellRun> runs)
        {
            TerminalCell cell = CellBufferReader.Read(buffer, baseIndex + start);
            uint bg = cell.BgArgb;
            uint fg = cell.FgArgb;
            ushort style = (ushort)(cell.Attrs & StyleMask);
            int col = start + 1;
            while (col < cols)
            {
                TerminalCell next = CellBufferReader.Read(buffer, baseIndex + col);
                if (!next.IsEmpty || next.BgArgb != bg || next.FgArgb != fg
                    || (next.Attrs & StyleMask) != style)
                {
                    break;
                }
                col++;
            }
            bool defaultBlank = bg == TerminalCell.DefaultBgMarker
                && fg == TerminalCell.DefaultFgMarker;
            if (!defaultBlank)
            {
                runs.Add(new CellRun
                {
                    StartCol = start,
                    Length = col - start,
                    FgArgb = fg,
                    BgArgb = bg,
                    Attrs = (ushort)(cell.Attrs & ~TerminalCell.AttrSoftWrap),
                    Text = string.Empty
                });
            }
            return col;
        }

        private static CellRun MakeRun(int start, int length, TerminalCell cell, string text)
        {
            return new CellRun
            {
                StartCol = start,
                Length = length,
                FgArgb = cell.FgArgb,
                BgArgb = cell.BgArgb,
                Attrs = (ushort)(cell.Attrs & ~TerminalCell.AttrSoftWrap),
                Text = text ?? string.Empty
            };
        }

        private static void DropTrailingDefaultBlanks(List<CellRun> runs)
        {
            while (runs.Count > 0)
            {
                CellRun last = runs[runs.Count - 1];
                if (last.Text.Length == 0
                    && last.BgArgb == TerminalCell.DefaultBgMarker
                    && last.FgArgb == TerminalCell.DefaultFgMarker)
                {
                    runs.RemoveAt(runs.Count - 1);
                    continue;
                }
                break;
            }
        }
    }
}
