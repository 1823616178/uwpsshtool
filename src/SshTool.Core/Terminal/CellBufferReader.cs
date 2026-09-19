using System;

namespace SshTool.Core.Terminal
{
    // T05：从 CopyDirtyRows / CopyViewport 的连续缓冲按小端解析单元格。
    public static class CellBufferReader
    {
        public static int CellCount(byte[] buffer)
        {
            if (buffer == null)
            {
                return 0;
            }
            return buffer.Length / TerminalCell.BytesPerCell;
        }

        public static TerminalCell Read(byte[] buffer, int cellIndex)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }
            int offset = cellIndex * TerminalCell.BytesPerCell;
            if (cellIndex < 0 || offset + TerminalCell.BytesPerCell > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(cellIndex));
            }
            return new TerminalCell
            {
                Codepoint = ReadU32(buffer, offset),
                FgArgb = ReadU32(buffer, offset + 4),
                BgArgb = ReadU32(buffer, offset + 8),
                Attrs = ReadU16(buffer, offset + 12),
                Reserved = ReadU16(buffer, offset + 14)
            };
        }

        public static TerminalCell Read(byte[] buffer, int row, int col, int cols)
        {
            if (cols <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cols));
            }
            return Read(buffer, row * cols + col);
        }

        private static uint ReadU32(byte[] buffer, int offset)
        {
            return (uint)buffer[offset]
                | ((uint)buffer[offset + 1] << 8)
                | ((uint)buffer[offset + 2] << 16)
                | ((uint)buffer[offset + 3] << 24);
        }

        private static ushort ReadU16(byte[] buffer, int offset)
        {
            return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
        }
    }
}
