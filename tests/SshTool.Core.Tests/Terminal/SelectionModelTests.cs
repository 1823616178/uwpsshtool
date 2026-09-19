using System;
using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    public class SelectionModelTests
    {
        [Fact]
        public void BeginWord_SelectsAlphanumericAndPunctuation()
        {
            var grid = Grid(1, 12, "foo-bar.baz");
            var model = new SelectionModel();
            model.BeginWord(0, 4, grid);
            Assert.Equal(new SelectionRange(0, 0, 0, 10), model.Normalized);
            Assert.Equal("foo-bar.baz", model.ExtractText(grid));
        }

        [Fact]
        public void BeginWord_OnSpace_SelectsSingleCell()
        {
            var grid = Grid(1, 5, "a b c");
            var model = new SelectionModel();
            model.BeginWord(0, 1, grid);
            Assert.Equal(new SelectionRange(0, 1, 0, 1), model.Normalized);
            Assert.Equal(string.Empty, model.ExtractText(grid));
        }

        [Fact]
        public void Extend_AcrossRows_IncludesNewline()
        {
            var grid = Grid(2, 4, "abcd", "efgh");
            var model = new SelectionModel();
            model.BeginWord(0, 0, grid);
            model.Extend(1, 1, grid);
            Assert.Equal(new SelectionRange(0, 0, 1, 1), model.Normalized);
            Assert.Equal("abcd\nef", model.ExtractText(grid));
        }

        [Fact]
        public void Extend_ReverseDrag_Normalizes()
        {
            var grid = Grid(1, 6, "abcdef");
            var model = new SelectionModel();
            model.BeginWord(0, 5, grid);
            model.Extend(0, 1, grid);
            Assert.Equal(new SelectionRange(0, 1, 0, 5), model.Normalized);
            Assert.Equal("bcdef", model.ExtractText(grid));
        }

        [Fact]
        public void WideChar_StartOnContinuation_ExpandsLeft()
        {
            var grid = WideThenSpace();
            var model = new SelectionModel();
            model.BeginWord(0, 1, grid);
            Assert.Equal(new SelectionRange(0, 0, 0, 1), model.Normalized);
            Assert.Equal("你", model.ExtractText(grid));
        }

        [Fact]
        public void WideChar_EndIncludesContinuation()
        {
            var grid = WideThenSpace();
            var model = new SelectionModel();
            model.BeginWord(0, 0, grid);
            Assert.Equal(new SelectionRange(0, 0, 0, 1), model.Normalized);
        }

        [Fact]
        public void BeginLine_SelectsWholeRow()
        {
            var grid = Grid(2, 4, "abcd", "efgh");
            var model = new SelectionModel();
            model.BeginLine(1, grid);
            Assert.Equal(new SelectionRange(1, 0, 1, 3), model.Normalized);
            Assert.Equal("efgh", model.ExtractText(grid));
        }

        [Fact]
        public void SelectAll_CoversGridIncludingScrollback()
        {
            var grid = Grid(3, 2, "ab", "cd", "ef");
            var model = new SelectionModel();
            model.SelectAll(grid);
            Assert.Equal(new SelectionRange(0, 0, 2, 1), model.Normalized);
            Assert.Equal("ab\ncd\nef", model.ExtractText(grid));
        }

        [Fact]
        public void ExtractText_TrimsTrailingSpaces()
        {
            var grid = Grid(1, 6, "hi    ");
            var model = new SelectionModel();
            model.SelectAll(grid);
            Assert.Equal("hi", model.ExtractText(grid));
        }

        [Fact]
        public void ExtractText_SoftWrap_DoesNotInsertNewline()
        {
            var grid = Grid(2, 4, "abcd", "efgh");
            TerminalCell last = grid.CellAt(0, 3);
            last.Attrs |= TerminalCell.AttrSoftWrap;
            grid.Set(0, 3, last);
            var model = new SelectionModel();
            model.SelectAll(grid);
            Assert.Equal("abcdefgh", model.ExtractText(grid));
        }

        [Fact]
        public void MoveStartAndEnd_AdjustsHandles()
        {
            var grid = Grid(1, 8, "abcdefgh");
            var model = new SelectionModel();
            model.BeginWord(0, 0, grid);
            model.MoveStart(0, 2, grid);
            model.MoveEnd(0, 5, grid);
            Assert.Equal(new SelectionRange(0, 2, 0, 5), model.Normalized);
            Assert.Equal("cdef", model.ExtractText(grid));
        }

        [Fact]
        public void Cancel_ClearsActive()
        {
            var grid = Grid(1, 3, "abc");
            var model = new SelectionModel();
            model.BeginWord(0, 0, grid);
            Assert.True(model.IsActive);
            model.Cancel();
            Assert.False(model.IsActive);
            Assert.Equal(string.Empty, model.ExtractText(grid));
        }

        private static MemoryGrid Grid(int rows, int cols, params string[] lines)
        {
            var grid = new MemoryGrid(rows, cols);
            for (int r = 0; r < lines.Length && r < rows; r++)
            {
                string line = lines[r] ?? string.Empty;
                for (int c = 0; c < cols; c++)
                {
                    char ch = c < line.Length ? line[c] : ' ';
                    grid.Set(r, c, Ascii(ch));
                }
            }
            return grid;
        }

        private static MemoryGrid WideThenSpace()
        {
            var grid = new MemoryGrid(1, 4);
            var ni = new TerminalCell { Codepoint = 0x4F60, Attrs = TerminalCell.AttrWide };
            var cont = new TerminalCell { Codepoint = TerminalCell.WideContinuation };
            grid.Set(0, 0, ni);
            grid.Set(0, 1, cont);
            grid.Set(0, 2, Ascii(' '));
            grid.Set(0, 3, Ascii(' '));
            return grid;
        }

        private static TerminalCell Ascii(char c)
        {
            return new TerminalCell { Codepoint = c };
        }

        private sealed class MemoryGrid : ISelectionGrid
        {
            private readonly TerminalCell[] _cells;

            public MemoryGrid(int rows, int cols)
            {
                Rows = rows;
                Cols = cols;
                _cells = new TerminalCell[rows * cols];
            }

            public int Rows { get; }
            public int Cols { get; }

            public TerminalCell CellAt(int row, int col)
            {
                if (row < 0 || col < 0 || row >= Rows || col >= Cols)
                {
                    return new TerminalCell();
                }
                return _cells[row * Cols + col];
            }

            public void Set(int row, int col, TerminalCell cell)
            {
                _cells[row * Cols + col] = cell;
            }
        }
    }
}
