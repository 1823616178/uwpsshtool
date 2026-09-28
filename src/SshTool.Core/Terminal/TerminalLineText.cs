using System;
using System.Collections.Generic;
using System.Text;

namespace SshTool.Core.Terminal
{
    // W02：一行终端格转成的文本 + 每个 UTF-16 字符所在的格列（宽字符的续格不产生字符，
    // 代理对两个 char 同属一格）。查找与链接识别都在文本上做，再用列表映射回格坐标。
    public sealed class TerminalLineText
    {
        private readonly int[] _columns;
        private readonly bool[] _wide;

        private TerminalLineText(string text, int[] columns, bool[] wide)
        {
            Text = text;
            _columns = columns;
            _wide = wide;
        }

        public string Text { get; }

        // 第 charIndex 个字符所在的格列。
        public int ColumnOf(int charIndex)
        {
            return _columns[charIndex];
        }

        // 字符区间 [start, start+length) 覆盖的格数（末字符是宽字符时多算一格）。
        public int CellSpan(int start, int length)
        {
            if (length <= 0)
            {
                return 0;
            }
            int last = start + length - 1;
            return _columns[last] - _columns[start] + (_wide[last] ? 2 : 1);
        }

        // 格列 col 对应的第一个字符下标；落在宽字符续格上时返回该宽字符；越界返回 -1。
        public int CharIndexAt(int col)
        {
            for (int i = 0; i < _columns.Length; i++)
            {
                int c = _columns[i];
                if (c == col || (_wide[i] && c + 1 == col))
                {
                    return i;
                }
                if (c > col)
                {
                    return -1;
                }
            }
            return -1;
        }

        public static TerminalLineText FromGrid(ISelectionGrid grid, int row)
        {
            if (grid == null || row < 0 || row >= grid.Rows)
            {
                return FromString(string.Empty);
            }
            var sb = new StringBuilder(grid.Cols);
            var columns = new List<int>(grid.Cols);
            var wide = new List<bool>(grid.Cols);
            for (int col = 0; col < grid.Cols; col++)
            {
                TerminalCell cell = grid.CellAt(row, col);
                if (cell.IsWideContinuation)
                {
                    continue;
                }
                string glyph = cell.Glyph();
                if (glyph.Length == 0)
                {
                    glyph = " ";
                }
                for (int k = 0; k < glyph.Length; k++)
                {
                    sb.Append(glyph[k]);
                    columns.Add(col);
                    wide.Add(cell.IsWide);
                }
            }
            return new TerminalLineText(sb.ToString(), columns.ToArray(), wide.ToArray());
        }

        // 测试与纯文本场景：每个字符占一格。
        public static TerminalLineText FromString(string text)
        {
            text = text ?? string.Empty;
            var columns = new int[text.Length];
            for (int i = 0; i < columns.Length; i++)
            {
                columns[i] = i;
            }
            return new TerminalLineText(text, columns, new bool[text.Length]);
        }
    }
}
