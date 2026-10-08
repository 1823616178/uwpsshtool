namespace SshTool.Core.Terminal
{
    // opt/full-pass：一帧所需的屏幕状态快照。原先 TerminalView 每帧分别读 Revision /
    // ScrollbackCount / Cols / Rows / BellCount / Cursor*，每次都是一次跨 WinRT 调用 +
    // 一次原生互斥锁；而且各值之间可能夹着一次 Feed，彼此不一致。
    // 实现 ITerminalStateSource 的屏幕（原生屏幕）一次加锁读完；其余屏幕走逐属性回退。
    public struct TerminalScreenState
    {
        // 原生 ReadState 的 int64 数组布局（与 Bridge/TerminalScreen.cpp 保持一致）
        public const int SlotRevision = 0;
        public const int SlotCols = 1;
        public const int SlotRows = 2;
        public const int SlotCursorRow = 3;
        public const int SlotCursorCol = 4;
        public const int SlotFlags = 5;
        public const int SlotMouseMode = 6;
        public const int SlotScrollbackCount = 7;
        public const int SlotBellCount = 8;
        public const int SlotCount = 9;

        public const long FlagCursorVisible = 1;
        public const long FlagAltScreen = 2;
        public const long FlagAppCursorKeys = 4;
        public const long FlagBracketedPaste = 8;
        public const long FlagMouseSgr = 16;
        public const long FlagHasBellCount = 32;

        public long Revision;
        public int Cols;
        public int Rows;
        public int CursorRow;
        public int CursorCol;
        public bool CursorVisible;
        public bool AltScreen;
        public bool AppCursorKeys;
        public bool BracketedPaste;
        public int MouseMode;
        public bool MouseSgr;
        public int ScrollbackCount;
        public bool HasBellCount;
        public long BellCount;

        // 逐属性回退 / 原生快照两条路径的统一入口。返回 false 表示原生锁正忙（Feed 进行中），
        // 调用方应跳过本帧、下一帧再试，而不是在 UI 线程上阻塞等锁。
        public static bool TryRead(ITerminalScreen screen, out TerminalScreenState state)
        {
            state = default(TerminalScreenState);
            if (screen == null)
            {
                return false;
            }
            var source = screen as ITerminalStateSource;
            if (source != null)
            {
                return source.TryReadState(out state);
            }
            state = ReadProperties(screen);
            return true;
        }

        public static TerminalScreenState ReadProperties(ITerminalScreen screen)
        {
            var state = new TerminalScreenState
            {
                Revision = screen.Revision,
                Cols = screen.Cols,
                Rows = screen.Rows,
                CursorRow = screen.CursorRow,
                CursorCol = screen.CursorCol,
                CursorVisible = screen.CursorVisible,
                AltScreen = screen.AltScreen,
                AppCursorKeys = screen.AppCursorKeys,
                BracketedPaste = screen.BracketedPaste,
                MouseMode = screen.MouseMode,
                MouseSgr = screen.MouseSgr,
                ScrollbackCount = screen.ScrollbackCount
            };
            var bell = screen as IBellSource;
            if (bell != null)
            {
                state.HasBellCount = true;
                state.BellCount = bell.BellCount;
            }
            return state;
        }

        public static bool TryDecode(long[] slots, out TerminalScreenState state)
        {
            state = default(TerminalScreenState);
            if (slots == null || slots.Length < SlotCount)
            {
                return false;
            }
            long flags = slots[SlotFlags];
            state.Revision = slots[SlotRevision];
            state.Cols = (int)slots[SlotCols];
            state.Rows = (int)slots[SlotRows];
            state.CursorRow = (int)slots[SlotCursorRow];
            state.CursorCol = (int)slots[SlotCursorCol];
            state.CursorVisible = (flags & FlagCursorVisible) != 0;
            state.AltScreen = (flags & FlagAltScreen) != 0;
            state.AppCursorKeys = (flags & FlagAppCursorKeys) != 0;
            state.BracketedPaste = (flags & FlagBracketedPaste) != 0;
            state.MouseSgr = (flags & FlagMouseSgr) != 0;
            state.HasBellCount = (flags & FlagHasBellCount) != 0;
            state.MouseMode = (int)slots[SlotMouseMode];
            state.ScrollbackCount = (int)slots[SlotScrollbackCount];
            state.BellCount = slots[SlotBellCount];
            return true;
        }

        public static void Encode(TerminalScreenState state, long[] slots)
        {
            long flags = 0;
            if (state.CursorVisible) { flags |= FlagCursorVisible; }
            if (state.AltScreen) { flags |= FlagAltScreen; }
            if (state.AppCursorKeys) { flags |= FlagAppCursorKeys; }
            if (state.BracketedPaste) { flags |= FlagBracketedPaste; }
            if (state.MouseSgr) { flags |= FlagMouseSgr; }
            if (state.HasBellCount) { flags |= FlagHasBellCount; }
            slots[SlotRevision] = state.Revision;
            slots[SlotCols] = state.Cols;
            slots[SlotRows] = state.Rows;
            slots[SlotCursorRow] = state.CursorRow;
            slots[SlotCursorCol] = state.CursorCol;
            slots[SlotFlags] = flags;
            slots[SlotMouseMode] = state.MouseMode;
            slots[SlotScrollbackCount] = state.ScrollbackCount;
            slots[SlotBellCount] = state.BellCount;
        }
    }
}
