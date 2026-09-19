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
    }
}
