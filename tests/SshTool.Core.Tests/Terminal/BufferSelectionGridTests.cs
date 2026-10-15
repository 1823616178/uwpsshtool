using System;
using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    // M-2：BufferSelectionGrid 是「长按选择→复制」与「查找高亮」的关键数据路径
    //（把 CopyViewport 的 16 字节格缓冲适配为 ISelectionGrid），此前零测试。
    // 本文件覆盖：行列透传、格解析（码点/颜色/属性）、宽字符续格、默认色标记、
    // 越界与截断缓冲的降级、空/负尺寸，以及与 SelectionModel 的端到端选区提取。
    public class BufferSelectionGridTests
    {
        [Fact]
        public void RowsCols_Passthrough()
        {
            var grid = new BufferSelectionGrid(new byte[3 * 5 * TerminalCell.BytesPerCell], 3, 5);
            Assert.Equal(3, grid.Rows);
            Assert.Equal(5, grid.Cols);
        }

        [Fact]
        public void NegativeSize_ClampedToZero()
        {
            var grid = new BufferSelectionGrid(new byte[16], -2, -4);
            Assert.Equal(0, grid.Rows);
            Assert.Equal(0, grid.Cols);
            Assert.True(grid.CellAt(0, 0).IsEmpty);
        }

        [Fact]
        public void NullBuffer_EmptyGrid()
        {
            var grid = new BufferSelectionGrid(null, 2, 2);
            Assert.True(grid.CellAt(0, 0).IsEmpty);
            Assert.True(grid.CellAt(1, 1).IsEmpty);
        }

        [Fact]
        public void CellAt_DecodesCodepointFgBgAttrs()
        {
            var cell = new TerminalCell
            {
                Codepoint = (uint)'A',
                FgArgb = 0xFFE8EEFBu,
                BgArgb = 0xFF12161Fu,
                Attrs = (ushort)(TerminalCell.AttrBold | TerminalCell.AttrUnderline)
            };
            var grid = new BufferSelectionGrid(Encode(new[] { cell }), 1, 1);
            TerminalCell got = grid.CellAt(0, 0);
            Assert.Equal((uint)'A', got.Codepoint);
            Assert.Equal(0xFFE8EEFBu, got.FgArgb);
            Assert.Equal(0xFF12161Fu, got.BgArgb);
            Assert.Equal((ushort)(TerminalCell.AttrBold | TerminalCell.AttrUnderline), got.Attrs);
        }

        [Fact]
        public void CellAt_RowMajorIndexing()
        {
            var cells = new[]
            {
                Ascii('a'), Ascii('b'),
                Ascii('c'), Ascii('d'),
            };
            var grid = new BufferSelectionGrid(Encode(cells), 2, 2);
            Assert.Equal((uint)'a', grid.CellAt(0, 0).Codepoint);
            Assert.Equal((uint)'b', grid.CellAt(0, 1).Codepoint);
            Assert.Equal((uint)'c', grid.CellAt(1, 0).Codepoint);
            Assert.Equal((uint)'d', grid.CellAt(1, 1).Codepoint);
        }

        [Fact]
        public void CellAt_WideCharAndContinuation()
        {
            var wide = new TerminalCell { Codepoint = 0x4F60, Attrs = TerminalCell.AttrWide };
            var cont = new TerminalCell { Codepoint = TerminalCell.WideContinuation };
            var grid = new BufferSelectionGrid(Encode(new[] { wide, cont }), 1, 2);
            Assert.True(grid.CellAt(0, 0).IsWide);
            Assert.Equal("你", grid.CellAt(0, 0).Glyph());
            Assert.True(grid.CellAt(0, 1).IsWideContinuation);
            Assert.Equal(string.Empty, grid.CellAt(0, 1).Glyph());
        }

        [Fact]
        public void CellAt_DefaultColorMarkers()
        {
            var cell = new TerminalCell
            {
                Codepoint = (uint)'x',
                FgArgb = TerminalCell.DefaultFgMarker,
                BgArgb = TerminalCell.DefaultBgMarker
            };
            var grid = new BufferSelectionGrid(Encode(new[] { cell }), 1, 1);
            Assert.Equal(TerminalCell.DefaultFgMarker, grid.CellAt(0, 0).FgArgb);
            Assert.Equal(TerminalCell.DefaultBgMarker, grid.CellAt(0, 0).BgArgb);
        }

        [Fact]
        public void CellAt_OutOfBounds_ReturnsEmpty()
        {
            var grid = new BufferSelectionGrid(Encode(new[] { Ascii('z') }), 1, 1);
            Assert.True(grid.CellAt(-1, 0).IsEmpty);
            Assert.True(grid.CellAt(0, -1).IsEmpty);
            Assert.True(grid.CellAt(1, 0).IsEmpty);
            Assert.True(grid.CellAt(0, 1).IsEmpty);
        }

        [Fact]
        public void CellAt_TruncatedBuffer_ReturnsEmpty()
        {
            // 缓冲只有半个格：CellAt 不得越界读，降级为空格。
            var grid = new BufferSelectionGrid(new byte[TerminalCell.BytesPerCell / 2], 1, 1);
            Assert.True(grid.CellAt(0, 0).IsEmpty);
        }

        [Fact]
        public void SelectionModel_EndToEnd_ExtractText()
        {
            // 关键集成路径：BufferSelectionGrid 直接喂给 SelectionModel 做选区提取，
            // 格解析错一位即导致选区错位/复制乱码。
            var grid = new BufferSelectionGrid(
                Encode(new[] { Ascii('h'), Ascii('i'), Ascii(' '), Ascii('!') }), 1, 4);
            var model = new SelectionModel();
            model.BeginLine(0, grid);
            Assert.Equal("hi !", model.ExtractText(grid));
        }

        [Fact]
        public void SelectionModel_EndToEnd_WideChar()
        {
            var wide = new TerminalCell { Codepoint = 0x4F60, Attrs = TerminalCell.AttrWide };
            var cont = new TerminalCell { Codepoint = TerminalCell.WideContinuation };
            var grid = new BufferSelectionGrid(Encode(new[] { wide, cont, Ascii(' ') }), 1, 3);
            var model = new SelectionModel();
            model.BeginWord(0, 1, grid);
            Assert.Equal("你", model.ExtractText(grid));
        }

        private static TerminalCell Ascii(char c)
        {
            return new TerminalCell { Codepoint = c };
        }

        // 按 16 字节小端布局编码（与 native Cell / CopyViewport 一致）。
        private static byte[] Encode(TerminalCell[] cells)
        {
            var buffer = new byte[cells.Length * TerminalCell.BytesPerCell];
            for (int i = 0; i < cells.Length; i++)
            {
                int o = i * TerminalCell.BytesPerCell;
                WriteU32(buffer, o, cells[i].Codepoint);
                WriteU32(buffer, o + 4, cells[i].FgArgb);
                WriteU32(buffer, o + 8, cells[i].BgArgb);
                WriteU16(buffer, o + 12, cells[i].Attrs);
                WriteU16(buffer, o + 14, cells[i].Reserved);
            }
            return buffer;
        }

        private static void WriteU32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static void WriteU16(byte[] buffer, int offset, ushort value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        }
    }
}
