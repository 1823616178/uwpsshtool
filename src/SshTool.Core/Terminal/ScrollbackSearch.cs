using System;
using System.Collections.Generic;

namespace SshTool.Core.Terminal
{
    // W02：一个命中。Line 是逻辑行号（回滚最旧行 = 0，屏幕末行 = ScrollbackCount + Rows - 1），
    // Col/CellLength 以终端格计。
    public struct TextMatch : IEquatable<TextMatch>
    {
        public TextMatch(int line, int col, int cellLength)
        {
            Line = line;
            Col = col;
            CellLength = cellLength;
        }

        public int Line { get; }
        public int Col { get; }
        public int CellLength { get; }

        public bool Equals(TextMatch other)
        {
            return Line == other.Line && Col == other.Col && CellLength == other.CellLength;
        }

        public override bool Equals(object obj)
        {
            return obj is TextMatch && Equals((TextMatch)obj);
        }

        public override int GetHashCode()
        {
            return (Line * 397) ^ (Col * 31) ^ CellLength;
        }
    }

    // W02（01-DESIGN §16.2）：回滚 + 屏幕全文查找。纯函数，不跨行匹配（软换行拆开的
    // 长词查不到，与大多数移动终端一致）。
    public static class ScrollbackSearch
    {
        // 病态查询（如单个空格）在 5 万行回滚里会产生海量命中，截断保护 UI。
        public const int MaxMatches = 1000;

        public static List<TextMatch> Find(IList<TerminalLineText> lines, string query)
        {
            var matches = new List<TextMatch>();
            if (lines == null || string.IsNullOrWhiteSpace(query))
            {
                return matches;
            }
            for (int line = 0; line < lines.Count; line++)
            {
                TerminalLineText text = lines[line];
                if (text == null)
                {
                    continue;
                }
                int from = 0;
                while (from <= text.Text.Length - query.Length)
                {
                    int hit = text.Text.IndexOf(query, from, StringComparison.OrdinalIgnoreCase);
                    if (hit < 0)
                    {
                        break;
                    }
                    matches.Add(new TextMatch(line, text.ColumnOf(hit), text.CellSpan(hit, query.Length)));
                    if (matches.Count >= MaxMatches)
                    {
                        return matches;
                    }
                    from = hit + query.Length;
                }
            }
            return matches;
        }

        // 读出全部逻辑行（回滚 + 屏幕）。按视口整窗拷贝，每次 Rows 行。
        public static List<TerminalLineText> ReadAllLines(ITerminalScreen screen)
        {
            var lines = new List<TerminalLineText>();
            if (screen == null || screen.Rows <= 0 || screen.Cols <= 0)
            {
                return lines;
            }
            int rows = screen.Rows;
            int scrollback = Math.Max(0, screen.ScrollbackCount);
            int total = scrollback + rows;
            var buffer = new byte[rows * screen.Cols * TerminalCell.BytesPerCell];
            while (lines.Count < total)
            {
                int offset = Math.Max(0, scrollback - lines.Count);
                int firstLine = scrollback - offset;
                screen.CopyViewport(offset, buffer);
                var grid = new BufferSelectionGrid(buffer, rows, screen.Cols);
                for (int r = 0; r < rows; r++)
                {
                    int line = firstLine + r;
                    if (line == lines.Count && line < total)
                    {
                        lines.Add(TerminalLineText.FromGrid(grid, r));
                    }
                }
            }
            return lines;
        }

        // 让逻辑行 line 出现在视口中部所需的回滚偏移。
        public static int OffsetToReveal(int line, int scrollbackCount, int rows)
        {
            int scrollback = Math.Max(0, scrollbackCount);
            int offset = scrollback - line + rows / 2;
            return ScrollController.ClampOffset(offset, scrollback);
        }

        // 逻辑行在给定偏移下的视口行；不在视口内返回 -1。
        public static int ViewportRow(int line, int scrollbackCount, int rows, int offset)
        {
            int row = line - (Math.Max(0, scrollbackCount) - offset);
            return row >= 0 && row < rows ? row : -1;
        }
    }
}
