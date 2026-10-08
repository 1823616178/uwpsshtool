using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    // opt/full-pass：整帧状态快照（原生 TryReadState 槽位编解码 + 逐属性回退）。
    public class TerminalScreenStateTests
    {
        private sealed class BusySource : ITerminalScreen, ITerminalStateSource
        {
            public bool Busy;
            public TerminalScreenState Next;
            public long Revision { get { return -999; } }
            public int Cols { get { return -1; } }
            public int Rows { get { return -1; } }
            public int CursorRow { get { return 0; } }
            public int CursorCol { get { return 0; } }
            public bool CursorVisible { get { return false; } }
            public bool AltScreen { get { return false; } }
            public bool AppCursorKeys { get { return false; } }
            public bool BracketedPaste { get { return false; } }
            public int MouseMode { get { return 0; } }
            public bool MouseSgr { get { return false; } }
            public int ScrollbackCount { get { return 0; } }
            public bool CopyDirtyRows(byte[] rowsOut, byte[] dirtyOut) { return false; }
            public void CopyViewport(int offset, byte[] rowsOut) { }
            public string GetText(int startRow, int startCol, int endRow, int endCol, int offset) { return string.Empty; }

            public bool TryReadState(out TerminalScreenState state)
            {
                state = Next;
                return !Busy;
            }
        }

        [Fact]
        public void EncodeDecode_RoundTrip()
        {
            var original = new TerminalScreenState
            {
                Revision = 1234567890123L,
                Cols = 80,
                Rows = 24,
                CursorRow = 23,
                CursorCol = 79,
                CursorVisible = true,
                AltScreen = true,
                AppCursorKeys = false,
                BracketedPaste = true,
                MouseMode = 1002,
                MouseSgr = true,
                ScrollbackCount = 5000,
                HasBellCount = true,
                BellCount = 3
            };
            var slots = new long[TerminalScreenState.SlotCount];
            TerminalScreenState.Encode(original, slots);
            TerminalScreenState decoded;
            Assert.True(TerminalScreenState.TryDecode(slots, out decoded));
            Assert.Equal(original, decoded);
        }

        [Fact]
        public void Decode_RejectsShortArray()
        {
            TerminalScreenState state;
            Assert.False(TerminalScreenState.TryDecode(new long[TerminalScreenState.SlotCount - 1], out state));
            Assert.False(TerminalScreenState.TryDecode(null, out state));
        }

        [Fact]
        public void TryRead_UsesStateSource_AndReportsBusy()
        {
            var screen = new BusySource { Next = new TerminalScreenState { Revision = 5, Cols = 10, Rows = 3 } };
            TerminalScreenState state;
            Assert.True(TerminalScreenState.TryRead(screen, out state));
            Assert.Equal(5, state.Revision); // 没走逐属性（属性返回 -999）
            screen.Busy = true;
            Assert.False(TerminalScreenState.TryRead(screen, out state));
        }

        [Fact]
        public void TryRead_FallsBackToProperties_ForPlainScreen()
        {
            var screen = new StaticTerminalScreen();
            var cells = new byte[2 * 3 * TerminalCell.BytesPerCell];
            screen.SetScreen(cells, 3, 2, 1, 2);
            TerminalScreenState state;
            Assert.True(TerminalScreenState.TryRead(screen, out state));
            Assert.Equal(screen.Revision, state.Revision);
            Assert.Equal(3, state.Cols);
            Assert.Equal(2, state.Rows);
            Assert.Equal(1, state.CursorRow);
            Assert.Equal(2, state.CursorCol);
            Assert.False(state.HasBellCount); // 静态屏幕不实现 IBellSource
            Assert.False(TerminalScreenState.TryRead(null, out state));
        }
    }
}
