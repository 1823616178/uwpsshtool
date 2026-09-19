using SshTool.Core.Appearance;
using SshTool.Core.Terminal;
using SshTool.Core.Tests.Appearance;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    // A03：StaticTerminalScreen（预览静态源）。
    public class StaticTerminalScreenTests
    {
        private static StaticTerminalScreen WithSample()
        {
            var screen = new StaticTerminalScreen();
            SampleScreen sample = SampleScreenBuilder.Build(SampleScreenBuilderTests.Theme());
            screen.SetScreen(sample.Cells, sample.Cols, sample.Rows, sample.CursorRow, sample.CursorCol);
            return screen;
        }

        [Fact]
        public void FirstCopyDirtyRowsReturnsTrue_SecondReturnsFalse()
        {
            StaticTerminalScreen screen = WithSample();
            var rows = new byte[screen.Cols * screen.Rows * TerminalCell.BytesPerCell];
            var dirty = new byte[(screen.Rows + 7) / 8];
            Assert.True(screen.CopyDirtyRows(rows, dirty));
            Assert.False(screen.CopyDirtyRows(rows, dirty));
        }

        [Fact]
        public void SetScreen_BumpsRevision()
        {
            StaticTerminalScreen screen = WithSample();
            long before = screen.Revision;
            SampleScreen sample = SampleScreenBuilder.Build(SampleScreenBuilderTests.Theme());
            screen.SetScreen(sample.Cells, sample.Cols, sample.Rows, 0, 0);
            Assert.Equal(before + 1, screen.Revision);
        }

        [Fact]
        public void CopyViewport_CopiesWholeBuffer()
        {
            StaticTerminalScreen screen = WithSample();
            var rows = new byte[screen.Cols * screen.Rows * TerminalCell.BytesPerCell];
            screen.CopyViewport(0, rows);
            TerminalCell first = CellBufferReader.Read(rows, 0);
            Assert.Equal((uint)'u', first.Codepoint);
        }

        [Fact]
        public void GetText_ContainsPrompt()
        {
            StaticTerminalScreen screen = WithSample();
            string text = screen.GetText(0, 0, 0, screen.Cols - 1, 0);
            Assert.Contains("user@host", text);
        }

        [Fact]
        public void CursorVisible_NoScrollback()
        {
            StaticTerminalScreen screen = WithSample();
            Assert.True(screen.CursorVisible);
            Assert.Equal(0, screen.ScrollbackCount);
            Assert.Equal(0, screen.MouseMode);
        }

        [Fact]
        public void MismatchedBufferSize_Throws()
        {
            var screen = new StaticTerminalScreen();
            Assert.Throws<System.ArgumentException>(() => screen.SetScreen(new byte[10], 36, 8, 0, 0));
        }
    }
}
