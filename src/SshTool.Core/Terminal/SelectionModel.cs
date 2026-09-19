using System;
using System.Text;

namespace SshTool.Core.Terminal
{
    public struct CellPos : IEquatable<CellPos>
    {
        public CellPos(int row, int col)
        {
            Row = row;
            Col = col;
        }

        public int Row { get; }
        public int Col { get; }

        public bool Equals(CellPos other)
        {
            return Row == other.Row && Col == other.Col;
        }

        public override bool Equals(object obj)
        {
            return obj is CellPos && Equals((CellPos)obj);
        }

        public override int GetHashCode()
        {
            unchecked { return (Row * 397) ^ Col; }
        }
    }

    // 两端均含。Start 在字典序上不大于 End。
    public struct SelectionRange : IEquatable<SelectionRange>
    {
        public SelectionRange(int startRow, int startCol, int endRow, int endCol)
        {
            StartRow = startRow;
            StartCol = startCol;
            EndRow = endRow;
            EndCol = endCol;
        }

        public int StartRow { get; }
        public int StartCol { get; }
        public int EndRow { get; }
        public int EndCol { get; }

        public bool Equals(SelectionRange other)
        {
            return StartRow == other.StartRow && StartCol == other.StartCol
                && EndRow == other.EndRow && EndCol == other.EndCol;
        }

        public override bool Equals(object obj)
        {
            return obj is SelectionRange && Equals((SelectionRange)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StartRow;
                hash = (hash * 397) ^ StartCol;
                hash = (hash * 397) ^ EndRow;
                hash = (hash * 397) ^ EndCol;
                return hash;
            }
        }
    }

    public interface ISelectionGrid
    {
        int Rows { get; }
        int Cols { get; }
        TerminalCell CellAt(int row, int col);
    }

    // 01-DESIGN.md §7.6：绝对行坐标选择。词字符 = 字母数字与 -_./~
    public sealed class SelectionModel
    {
        public const string WordExtraChars = "-_./~";

        private bool _active;
        private CellPos _anchor;
        private CellPos _focus;

        public bool IsActive
        {
            get { return _active; }
        }

        public CellPos Anchor
        {
            get { return _anchor; }
        }

        public CellPos Focus
        {
            get { return _focus; }
        }

        public SelectionRange Normalized
        {
            get { return Normalize(_anchor, _focus); }
        }

        public void Cancel()
        {
            _active = false;
            _anchor = new CellPos();
            _focus = new CellPos();
        }

        public void BeginWord(int row, int col, ISelectionGrid grid)
        {
            if (grid == null)
            {
                throw new ArgumentNullException(nameof(grid));
            }
            CellPos pos = Clamp(grid, row, col);
            pos = SnapWideStart(grid, pos);
            SelectionRange word = ExpandWord(grid, pos);
            var start = new CellPos(word.StartRow, word.StartCol);
            var end = new CellPos(word.EndRow, word.EndCol);
            bool nearerStart = pos.Row == start.Row
                && (pos.Col - start.Col) <= (end.Col - pos.Col);
            _anchor = nearerStart ? start : end;
            _focus = nearerStart ? end : start;
            _active = true;
        }

        public void BeginLine(int row, ISelectionGrid grid)
        {
            if (grid == null)
            {
                throw new ArgumentNullException(nameof(grid));
            }
            int r = ClampRow(grid, row);
            _anchor = new CellPos(r, 0);
            _focus = new CellPos(r, Math.Max(0, grid.Cols - 1));
            _active = true;
        }

        public void SelectAll(ISelectionGrid grid)
        {
            if (grid == null)
            {
                throw new ArgumentNullException(nameof(grid));
            }
            _anchor = new CellPos(0, 0);
            _focus = new CellPos(Math.Max(0, grid.Rows - 1), Math.Max(0, grid.Cols - 1));
            _active = true;
        }

        public void Extend(int row, int col, ISelectionGrid grid)
        {
            if (!_active || grid == null)
            {
                return;
            }
            CellPos pos = SnapWideStart(grid, Clamp(grid, row, col));
            _focus = ExpandWideEnd(grid, pos);
        }

        public void MoveStart(int row, int col, ISelectionGrid grid)
        {
            if (!_active || grid == null)
            {
                return;
            }
            SelectionRange range = Normalized;
            CellPos start = SnapWideStart(grid, Clamp(grid, row, col));
            CellPos end = new CellPos(range.EndRow, range.EndCol);
            _anchor = start;
            _focus = end;
        }

        public void MoveEnd(int row, int col, ISelectionGrid grid)
        {
            if (!_active || grid == null)
            {
                return;
            }
            SelectionRange range = Normalized;
            CellPos start = new CellPos(range.StartRow, range.StartCol);
            CellPos end = ExpandWideEnd(grid, Clamp(grid, row, col));
            _anchor = start;
            _focus = end;
        }

        public string ExtractText(ISelectionGrid grid)
        {
            if (!_active || grid == null || grid.Rows <= 0 || grid.Cols <= 0)
            {
                return string.Empty;
            }
            SelectionRange range = Normalized;
            var sb = new StringBuilder();
            for (int row = range.StartRow; row <= range.EndRow; row++)
            {
                int from = row == range.StartRow ? range.StartCol : 0;
                int to = row == range.EndRow ? range.EndCol : grid.Cols - 1;
                string line = ExtractLine(grid, row, from, to);
                sb.Append(line);
                if (row < range.EndRow && !LineSoftWraps(grid, row))
                {
                    sb.Append('\n');
                }
            }
            return sb.ToString();
        }

        public static bool IsWordChar(uint codepoint)
        {
            if (codepoint == 0 || codepoint == TerminalCell.WideContinuation || codepoint > 0x10FFFFu)
            {
                return false;
            }
            if (codepoint <= 0xFFFFu)
            {
                char c = (char)codepoint;
                if (char.IsLetterOrDigit(c))
                {
                    return true;
                }
                return WordExtraChars.IndexOf(c) >= 0;
            }
            string glyph = char.ConvertFromUtf32((int)codepoint);
            return char.IsLetterOrDigit(glyph, 0);
        }

        public static SelectionRange Normalize(CellPos a, CellPos b)
        {
            bool aFirst = a.Row < b.Row || (a.Row == b.Row && a.Col <= b.Col);
            CellPos start = aFirst ? a : b;
            CellPos end = aFirst ? b : a;
            return new SelectionRange(start.Row, start.Col, end.Row, end.Col);
        }

        private static SelectionRange ExpandWord(ISelectionGrid grid, CellPos pos)
        {
            if (!IsWordChar(Codepoint(grid, pos.Row, pos.Col)))
            {
                CellPos wideEnd = ExpandWideEnd(grid, pos);
                return new SelectionRange(pos.Row, pos.Col, wideEnd.Row, wideEnd.Col);
            }
            int left = pos.Col;
            while (left > 0 && IsWordChar(Codepoint(grid, pos.Row, left - 1)))
            {
                left--;
            }
            int right = pos.Col;
            while (right + 1 < grid.Cols && IsWordChar(Codepoint(grid, pos.Row, right + 1)))
            {
                right++;
            }
            CellPos start = SnapWideStart(grid, new CellPos(pos.Row, left));
            CellPos end = ExpandWideEnd(grid, new CellPos(pos.Row, right));
            return new SelectionRange(start.Row, start.Col, end.Row, end.Col);
        }

        private static CellPos SnapWideStart(ISelectionGrid grid, CellPos pos)
        {
            if (pos.Col > 0 && grid.CellAt(pos.Row, pos.Col).IsWideContinuation)
            {
                return new CellPos(pos.Row, pos.Col - 1);
            }
            return pos;
        }

        private static CellPos ExpandWideEnd(ISelectionGrid grid, CellPos pos)
        {
            TerminalCell cell = grid.CellAt(pos.Row, pos.Col);
            if (cell.IsWide && pos.Col + 1 < grid.Cols)
            {
                return new CellPos(pos.Row, pos.Col + 1);
            }
            return pos;
        }

        private static string ExtractLine(ISelectionGrid grid, int row, int from, int to)
        {
            var sb = new StringBuilder();
            for (int col = from; col <= to; col++)
            {
                TerminalCell cell = grid.CellAt(row, col);
                if (cell.IsWideContinuation)
                {
                    continue;
                }
                sb.Append(cell.Glyph());
            }
            int end = sb.Length;
            while (end > 0 && sb[end - 1] == ' ')
            {
                end--;
            }
            return end == sb.Length ? sb.ToString() : sb.ToString(0, end);
        }

        private static bool LineSoftWraps(ISelectionGrid grid, int row)
        {
            if (grid.Cols <= 0)
            {
                return false;
            }
            TerminalCell last = grid.CellAt(row, grid.Cols - 1);
            return (last.Attrs & TerminalCell.AttrSoftWrap) != 0;
        }

        private static uint Codepoint(ISelectionGrid grid, int row, int col)
        {
            TerminalCell cell = grid.CellAt(row, col);
            if (cell.IsWideContinuation && col > 0)
            {
                return grid.CellAt(row, col - 1).Codepoint;
            }
            return cell.Codepoint;
        }

        private static CellPos Clamp(ISelectionGrid grid, int row, int col)
        {
            return new CellPos(ClampRow(grid, row), ClampCol(grid, col));
        }

        private static int ClampRow(ISelectionGrid grid, int row)
        {
            if (grid.Rows <= 0)
            {
                return 0;
            }
            if (row < 0) { return 0; }
            if (row >= grid.Rows) { return grid.Rows - 1; }
            return row;
        }

        private static int ClampCol(ISelectionGrid grid, int col)
        {
            if (grid.Cols <= 0)
            {
                return 0;
            }
            if (col < 0) { return 0; }
            if (col >= grid.Cols) { return grid.Cols - 1; }
            return col;
        }
    }
}
