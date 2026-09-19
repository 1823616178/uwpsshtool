using System;
using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    public class CellBufferReaderTests
    {
        [Fact]
        public void Read_LittleEndianLayout()
        {
            byte[] buffer = new byte[TerminalCell.BytesPerCell];
            WriteCell(buffer, 0, 0x00004F60u, 0xFFCD0000u, 0xFF000000u, TerminalCell.AttrBold);

            TerminalCell cell = CellBufferReader.Read(buffer, 0);
            Assert.Equal(0x00004F60u, cell.Codepoint);
            Assert.Equal(0xFFCD0000u, cell.FgArgb);
            Assert.Equal(0xFF000000u, cell.BgArgb);
            Assert.Equal(TerminalCell.AttrBold, cell.Attrs);
            Assert.Equal("你", cell.Glyph());
            Assert.Equal(1, CellBufferReader.CellCount(buffer));
        }

        [Fact]
        public void Read_RowCol_IndexesRowMajor()
        {
            byte[] buffer = new byte[2 * 3 * TerminalCell.BytesPerCell];
            WriteCell(buffer, 1 * 3 + 2, (uint)'Z', 1, 2, 0);
            TerminalCell cell = CellBufferReader.Read(buffer, 1, 2, 3);
            Assert.Equal((uint)'Z', cell.Codepoint);
        }

        [Fact]
        public void Read_WideContinuationAndEmpty()
        {
            byte[] buffer = new byte[TerminalCell.BytesPerCell];
            WriteCell(buffer, 0, TerminalCell.WideContinuation, 0, 0, 0);
            TerminalCell cell = CellBufferReader.Read(buffer, 0);
            Assert.True(cell.IsWideContinuation);
            Assert.Equal(string.Empty, cell.Glyph());

            WriteCell(buffer, 0, 0, TerminalCell.DefaultFgMarker, TerminalCell.DefaultBgMarker, 0);
            cell = CellBufferReader.Read(buffer, 0);
            Assert.True(cell.IsEmpty);
        }

        [Fact]
        public void Read_OutOfRange_Throws()
        {
            byte[] buffer = new byte[TerminalCell.BytesPerCell];
            Assert.Throws<ArgumentOutOfRangeException>(() => CellBufferReader.Read(buffer, 1));
            Assert.Throws<ArgumentNullException>(() => CellBufferReader.Read(null, 0));
        }

        internal static void WriteCell(byte[] buffer, int index, uint cp, uint fg, uint bg, ushort attrs)
        {
            int o = index * TerminalCell.BytesPerCell;
            WriteU32(buffer, o, cp);
            WriteU32(buffer, o + 4, fg);
            WriteU32(buffer, o + 8, bg);
            WriteU16(buffer, o + 12, attrs);
            WriteU16(buffer, o + 14, 0);
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
