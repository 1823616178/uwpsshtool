using System.Collections.Generic;
using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    public class RowRunBuilderTests
    {
        private const uint Fg = 0xFFE5E5E5u;
        private const uint Bg = 0xFF000000u;
        private const uint Red = 0xFFCD0000u;
        private const uint Green = 0xFF00CD00u;

        [Fact]
        public void SameStyleCells_MergeIntoOneRun()
        {
            byte[] row = Row(4);
            Put(row, 0, 'A', Fg, Bg, 0);
            Put(row, 1, 'B', Fg, Bg, 0);
            Put(row, 2, 'C', Fg, Bg, 0);

            List<CellRun> runs = RowRunBuilder.Build(row, 0, 4);
            Assert.Single(runs);
            Assert.Equal(0, runs[0].StartCol);
            Assert.Equal(3, runs[0].Length);
            Assert.Equal("ABC", runs[0].Text);
            Assert.Equal(Fg, runs[0].FgArgb);
            Assert.Equal(Bg, runs[0].BgArgb);
        }

        [Fact]
        public void AttributeChange_SplitsRun()
        {
            byte[] row = Row(4);
            Put(row, 0, 'A', Fg, Bg, 0);
            Put(row, 1, 'B', Fg, Bg, TerminalCell.AttrBold);
            Put(row, 2, 'C', Red, Bg, 0);

            List<CellRun> runs = RowRunBuilder.Build(row, 0, 4);
            Assert.Equal(3, runs.Count);
            Assert.Equal("A", runs[0].Text);
            Assert.Equal("B", runs[1].Text);
            Assert.Equal(TerminalCell.AttrBold, runs[1].Attrs);
            Assert.Equal("C", runs[2].Text);
            Assert.Equal(Red, runs[2].FgArgb);
        }

        [Fact]
        public void WideChar_IsOwnRunOfLengthTwo()
        {
            byte[] row = Row(4);
            Put(row, 0, 'A', Fg, Bg, 0);
            Put(row, 1, 0x4F60u, Fg, Bg, TerminalCell.AttrWide);
            Put(row, 2, TerminalCell.WideContinuation, Fg, Bg, 0);
            Put(row, 3, 'B', Fg, Bg, 0);

            List<CellRun> runs = RowRunBuilder.Build(row, 0, 4);
            Assert.Equal(3, runs.Count);
            Assert.Equal("A", runs[0].Text);
            Assert.Equal(1, runs[0].Length);
            Assert.Equal("你", runs[1].Text);
            Assert.Equal(2, runs[1].Length);
            Assert.Equal(1, runs[1].StartCol);
            Assert.Equal(TerminalCell.AttrWide, runs[1].Attrs & TerminalCell.AttrWide);
            Assert.Equal("B", runs[2].Text);
            Assert.Equal(3, runs[2].StartCol);
        }

        [Fact]
        public void TrailingDefaultBlanks_AreDropped()
        {
            byte[] row = Row(5);
            Put(row, 0, 'H', Fg, Bg, 0);
            Put(row, 1, 'i', Fg, Bg, 0);
            // cols 2-4 remain default empty markers

            List<CellRun> runs = RowRunBuilder.Build(row, 0, 5);
            Assert.Single(runs);
            Assert.Equal("Hi", runs[0].Text);
            Assert.Equal(2, runs[0].Length);
        }

        [Fact]
        public void EmptyCellsWithCustomBackground_KeptAsBlankRun()
        {
            byte[] row = Row(3);
            Put(row, 0, 'X', Fg, Bg, 0);
            Put(row, 1, 0, Fg, Green, 0);
            Put(row, 2, 0, Fg, Green, 0);

            List<CellRun> runs = RowRunBuilder.Build(row, 0, 3);
            Assert.Equal(2, runs.Count);
            Assert.Equal("X", runs[0].Text);
            Assert.Equal(string.Empty, runs[1].Text);
            Assert.Equal(1, runs[1].StartCol);
            Assert.Equal(2, runs[1].Length);
            Assert.Equal(Green, runs[1].BgArgb);
        }

        [Fact]
        public void EmptyCellInMiddle_BreaksTextRun()
        {
            byte[] row = Row(3);
            Put(row, 0, 'A', Fg, Bg, 0);
            Put(row, 1, 0, TerminalCell.DefaultFgMarker, TerminalCell.DefaultBgMarker, 0);
            Put(row, 2, 'B', Fg, Bg, 0);

            List<CellRun> runs = RowRunBuilder.Build(row, 0, 3);
            Assert.Equal(2, runs.Count);
            Assert.Equal("A", runs[0].Text);
            Assert.Equal("B", runs[1].Text);
            Assert.Equal(2, runs[1].StartCol);
        }

        // O11：两行缓冲（复用表的跨行测试用）。
        private static byte[] TwoRows(int cols)
        {
            var buffer = new byte[cols * 2 * TerminalCell.BytesPerCell];
            for (int c = 0; c < cols * 2; c++)
            {
                CellBufferReaderTests.WriteCell(buffer, c, 0,
                    TerminalCell.DefaultFgMarker, TerminalCell.DefaultBgMarker, 0);
            }
            return buffer;
        }

        private static byte[] Row(int cols)
        {
            var buffer = new byte[cols * TerminalCell.BytesPerCell];
            for (int c = 0; c < cols; c++)
            {
                CellBufferReaderTests.WriteCell(buffer, c, 0,
                    TerminalCell.DefaultFgMarker, TerminalCell.DefaultBgMarker, 0);
            }
            return buffer;
        }

        private static void Put(byte[] row, int col, uint cp, uint fg, uint bg, ushort attrs)
        {
            CellBufferReaderTests.WriteCell(row, col, cp, fg, bg, attrs);
        }

        private static void Put(byte[] row, int col, char ch, uint fg, uint bg, ushort attrs)
        {
            Put(row, col, (uint)ch, fg, bg, attrs);
        }

        // ---------- O11：复用表的重载 ----------

        // 复用重载与新建重载必须逐字段一致，且连续复用不得残留上一行的段。
        [Fact]
        public void Build_IntoReusedList_MatchesAllocatingOverload()
        {
            byte[] cells = TwoRows(4);
            Put(cells, 0, 'a', 0xFFFFFFFFu, TerminalCell.DefaultBgMarker, 0);
            Put(cells, 1, 'b', 0xFFFFFFFFu, TerminalCell.DefaultBgMarker, 0);
            Put(cells, 4, 'x', 0xFFFFFFFFu, TerminalCell.DefaultBgMarker, 0); // 第 1 行第 0 列

            var pool = new List<CellRun>();
            RowRunBuilder.Build(cells, 0, 4, pool);
            List<CellRun> fresh0 = RowRunBuilder.Build(cells, 0, 4);
            AssertSameRuns(fresh0, pool);

            // 同一张表接着填第 1 行：不得残留第 0 行的段
            RowRunBuilder.Build(cells, 1, 4, pool);
            List<CellRun> fresh1 = RowRunBuilder.Build(cells, 1, 4);
            AssertSameRuns(fresh1, pool);
        }

        [Fact]
        public void Build_IntoNullList_DoesNotThrow()
        {
            byte[] cells = Row(4);
            RowRunBuilder.Build(cells, 0, 4, null);
        }

        [Fact]
        public void Build_IntoList_ClearsOnInvalidInput()
        {
            byte[] cells = Row(4);
            Put(cells, 0, 'a', 0xFFFFFFFFu, TerminalCell.DefaultBgMarker, 0);
            var pool = new List<CellRun>();
            RowRunBuilder.Build(cells, 0, 4, pool);
            Assert.NotEmpty(pool);

            RowRunBuilder.Build(cells, 99, 4, pool); // 越界行
            Assert.Empty(pool);
        }

        private static void AssertSameRuns(List<CellRun> expected, List<CellRun> actual)
        {
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].StartCol, actual[i].StartCol);
                Assert.Equal(expected[i].Length, actual[i].Length);
                Assert.Equal(expected[i].FgArgb, actual[i].FgArgb);
                Assert.Equal(expected[i].BgArgb, actual[i].BgArgb);
                Assert.Equal(expected[i].Attrs, actual[i].Attrs);
                Assert.Equal(expected[i].Text, actual[i].Text);
            }
        }
    }
}
