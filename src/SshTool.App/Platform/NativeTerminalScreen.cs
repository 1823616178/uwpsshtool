using SshTool.Core.Terminal;
using NativeBridge = SshTool.Native.Bridge;

namespace SshTool.App.Platform
{
    // T03：ITerminalScreen 的原生适配。
    public sealed class NativeTerminalScreen : ITerminalScreen, IBellSource
    {
        private readonly NativeBridge.TerminalScreen _native;

        public NativeTerminalScreen(NativeBridge.TerminalScreen native)
        {
            _native = native;
        }

        public long Revision { get { return _native.Revision; } }
        public int Cols { get { return _native.Cols; } }
        public int Rows { get { return _native.Rows; } }
        public int CursorRow { get { return _native.CursorRow; } }
        public int CursorCol { get { return _native.CursorCol; } }
        public bool CursorVisible { get { return _native.CursorVisible; } }
        public bool AltScreen { get { return _native.AltScreen; } }
        public bool AppCursorKeys { get { return _native.AppCursorKeys; } }
        public bool BracketedPaste { get { return _native.BracketedPaste; } }
        public int MouseMode { get { return _native.MouseMode; } }
        public bool MouseSgr { get { return _native.MouseSgr; } }
        public int ScrollbackCount { get { return _native.ScrollbackCount; } }
        public long BellCount { get { return _native.BellCount; } }

        public bool CopyDirtyRows(byte[] rowsOut, byte[] dirtyOut)
        {
            if (rowsOut == null || dirtyOut == null)
            {
                return false;
            }
            return _native.CopyDirtyRows(rowsOut, dirtyOut);
        }

        public void CopyViewport(int offset, byte[] rowsOut)
        {
            if (rowsOut == null)
            {
                return;
            }
            _native.CopyViewport(offset, rowsOut);
        }

        public string GetText(int startRow, int startCol, int endRow, int endCol, int offset)
        {
            return _native.GetText(startRow, startCol, endRow, endCol, offset) ?? string.Empty;
        }
    }
}
