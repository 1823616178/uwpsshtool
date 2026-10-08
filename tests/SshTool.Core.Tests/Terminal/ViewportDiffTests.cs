using System;
using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    // opt/full-pass：回滚浏览时的逐行差分（只重画变化行）。
    public class ViewportDiffTests
    {
        private const int Cols = 4;
        private const int Rows = 10;
        private const int RowBytes = Cols * TerminalCell.BytesPerCell;

        private static byte[] Grid(params byte[] rowFill)
        {
            var cells = new byte[Rows * RowBytes];
            for (int r = 0; r < rowFill.Length && r < Rows; r++)
            {
                for (int i = 0; i < RowBytes; i++)
                {
                    cells[r * RowBytes + i] = rowFill[r];
                }
            }
            return cells;
        }

        private static bool IsDirty(byte[] dirty, int row)
        {
            return (dirty[row / 8] & (1 << (row % 8))) != 0;
        }

        [Fact]
        public void IdenticalViewport_NoDirtyRows()
        {
            byte[] cells = Grid(1, 2, 3);
            byte[] fresh = Grid(1, 2, 3);
            var dirty = new byte[] { 0xFF, 0xFF };
            Assert.Equal(0, ViewportDiff.Apply(cells, fresh, dirty, Rows, Cols));
            Assert.Equal(new byte[] { 0, 0 }, dirty);
        }

        [Fact]
        public void ChangedRows_CopiedAndMarked_IncludingSecondDirtyByte()
        {
            byte[] cells = Grid(1, 2, 3, 4, 5, 6, 7, 8, 9, 10);
            byte[] fresh = Grid(1, 9, 3, 4, 5, 6, 7, 8, 42, 10);
            var dirty = new byte[2];
            Assert.Equal(2, ViewportDiff.Apply(cells, fresh, dirty, Rows, Cols));
            for (int r = 0; r < Rows; r++)
            {
                Assert.Equal(r == 1 || r == 8, IsDirty(dirty, r));
            }
            Assert.Equal(fresh, cells);
        }

        [Fact]
        public void SingleByteDifference_MarksRow()
        {
            byte[] cells = Grid();
            byte[] fresh = Grid();
            fresh[5 * RowBytes + RowBytes - 1] = 1; // 第 5 行最后一个字节
            var dirty = new byte[2];
            Assert.Equal(1, ViewportDiff.Apply(cells, fresh, dirty, Rows, Cols));
            Assert.True(IsDirty(dirty, 5));
        }

        [Fact]
        public void TooSmallBuffers_Throw_NullOrEmpty_ReturnZero()
        {
            Assert.Throws<ArgumentException>(() => ViewportDiff.Apply(new byte[1], Grid(), new byte[2], Rows, Cols));
            Assert.Throws<ArgumentException>(() => ViewportDiff.Apply(Grid(), Grid(), new byte[1], Rows, Cols));
            Assert.Equal(0, ViewportDiff.Apply(null, Grid(), new byte[2], Rows, Cols));
            Assert.Equal(0, ViewportDiff.Apply(Grid(), Grid(), new byte[2], 0, Cols));
        }
    }
}
