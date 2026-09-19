using System;

namespace SshTool.Core.Terminal
{
    // 把 CopyViewport 的 16 字节格缓冲适配成 ISelectionGrid。
    public sealed class BufferSelectionGrid : ISelectionGrid
    {
        private readonly byte[] _cells;

        public BufferSelectionGrid(byte[] cells, int rows, int cols)
        {
            _cells = cells ?? new byte[0];
            Rows = rows < 0 ? 0 : rows;
            Cols = cols < 0 ? 0 : cols;
        }

        public int Rows { get; }

        public int Cols { get; }

        public TerminalCell CellAt(int row, int col)
        {
            if (row < 0 || col < 0 || row >= Rows || col >= Cols)
            {
                return new TerminalCell();
            }
            int index = row * Cols + col;
            if ((index + 1) * TerminalCell.BytesPerCell > _cells.Length)
            {
                return new TerminalCell();
            }
            return CellBufferReader.Read(_cells, index);
        }
    }
}
