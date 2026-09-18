namespace SshTool.Core.Terminal
{
    // N09b 先定义、T03 实现：终端屏幕快照（镜像 01-DESIGN §6.2
    // TerminalScreen，只用 Core 自有类型）。
    //
    // 读取纪律：UI 帧线程在 ContentDirty 后按 Revision 变化拷贝脏行；
    // rowsOut 大小 = Rows*Cols*16；dirtyOut 大小 = ceil(Rows/8)。
    public interface ITerminalScreen
    {
        long Revision { get; }
        int Cols { get; }
        int Rows { get; }
        int CursorRow { get; }
        int CursorCol { get; }
        bool CursorVisible { get; }
        bool AltScreen { get; }
        bool AppCursorKeys { get; }
        bool BracketedPaste { get; }
        int MouseMode { get; }  // 0 关 / 1000 / 1002 / 1003
        bool MouseSgr { get; }  // 1006
        int ScrollbackCount { get; }

        // 返回是否有变化；缓冲区大小约定见上。
        bool CopyDirtyRows(byte[] rowsOut, byte[] dirtyOut);
        // 从底部向上偏移 offset 行的视口整窗拷贝（回滚浏览）。
        void CopyViewport(int offset, byte[] rowsOut);
        // 选择复制。
        string GetText(int startRow, int startCol, int endRow, int endCol, int offset);
    }
}
