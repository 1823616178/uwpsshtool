using System;

namespace SshTool.Core.Terminal
{
    // A03：ITerminalScreen 的静态实现——包一帧固定的 16 字节单元格缓冲，
    // 给外观预览用（TerminalView 的 Screen 属性直接可用，无需改绘制管线）。
    // SetScreen 后 Revision+1 并全行标脏；之后 CopyDirtyRows 返回 false，
    // 视图只在 Revision 变化/闪烁相位时重绘。
    public sealed class StaticTerminalScreen : ITerminalScreen
    {
        private byte[] _cells = new byte[0];
        private int _cols;
        private int _rows;
        private int _cursorRow;
        private int _cursorCol;
        private bool _stale;
        private long _revision;

        public long Revision
        {
            get { return _revision; }
        }

        public int Cols
        {
            get { return _cols; }
        }

        public int Rows
        {
            get { return _rows; }
        }

        public int CursorRow
        {
            get { return _cursorRow; }
        }

        public int CursorCol
        {
            get { return _cursorCol; }
        }

        public bool CursorVisible
        {
            get { return true; }
        }

        public bool AltScreen
        {
            get { return false; }
        }

        public bool AppCursorKeys
        {
            get { return false; }
        }

        public bool BracketedPaste
        {
        get { return false; }
        }

        public int MouseMode
        {
            get { return 0; }
        }

        public bool MouseSgr
        {
            get { return false; }
        }

        public int ScrollbackCount
        {
            get { return 0; }
        }

        public void SetScreen(byte[] cells, int cols, int rows, int cursorRow, int cursorCol)
        {
            if (cells == null)
            {
                throw new ArgumentNullException("cells");
            }
            if (cols <= 0 || rows <= 0)
            {
                throw new ArgumentOutOfRangeException("cols");
            }
            if (cells.Length != cols * rows * TerminalCell.BytesPerCell)
            {
                throw new ArgumentException("缓冲大小与行列不匹配", "cells");
            }
            _cells = cells;
            _cols = cols;
            _rows = rows;
            _cursorRow = cursorRow < 0 ? 0 : (cursorRow >= rows ? rows - 1 : cursorRow);
            _cursorCol = cursorCol < 0 ? 0 : (cursorCol >= cols ? cols - 1 : cursorCol);
            _stale = true;
            _revision++;
        }

        public bool CopyDirtyRows(byte[] rowsOut, byte[] dirtyOut)
        {
            if (rowsOut == null || dirtyOut == null)
            {
                return false;
            }
            if (!_stale || _cells.Length == 0)
            {
                return false;
            }
            if (rowsOut.Length != _cells.Length || dirtyOut.Length != (_rows + 7) / 8)
            {
                return false;
            }
            Array.Copy(_cells, rowsOut, _cells.Length);
            for (int i = 0; i < dirtyOut.Length; i++)
            {
                dirtyOut[i] = 0xFF;
            }
            _stale = false;
            return true;
        }

        public void CopyViewport(int offset, byte[] rowsOut)
        {
            if (rowsOut == null || _cells.Length == 0)
            {
                return;
            }
            int count = Math.Min(rowsOut.Length, _cells.Length);
            Array.Copy(_cells, rowsOut, count);
        }

        public string GetText(int startRow, int startCol, int endRow, int endCol, int offset)
        {
            if (_cells.Length == 0)
            {
                return string.Empty;
            }
            if (startRow < 0)
            {
                startRow = 0;
            }
            if (endRow >= _rows)
            {
                endRow = _rows - 1;
            }
            var sb = new System.Text.StringBuilder();
            for (int r = startRow; r <= endRow; r++)
            {
                int from = r == startRow ? startCol : 0;
                int to = r == endRow ? endCol : _cols - 1;
                for (int c = from; c <= to && c < _cols; c++)
                {
                    if (c < 0)
                    {
                        continue;
                    }
                    TerminalCell cell = CellBufferReader.Read(_cells, r * _cols + c);
                    if (cell.IsWideContinuation || cell.IsEmpty)
                    {
                        continue;
                    }
                    sb.Append(cell.Glyph());
                }
                if (r != endRow)
                {
                    sb.Append('\n');
                }
            }
            return sb.ToString();
        }
    }
}
