using System;
using System.Collections.Generic;
using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    // W02：回滚查找、格列映射、视口定位与链接识别。
    public class ScrollbackSearchTests
    {
        // 逻辑行 = 回滚 + 屏幕；CopyViewport 按「从底部向上 offset 行」的约定取窗口。
        private sealed class LinesScreen : ITerminalScreen
        {
            private readonly List<string> _lines;

            public LinesScreen(List<string> lines, int rows, int cols)
            {
                _lines = lines;
                Rows = rows;
                Cols = cols;
            }

            public long Revision { get { return 0; } }
            public int Cols { get; }
            public int Rows { get; }
            public int CursorRow { get { return 0; } }
            public int CursorCol { get { return 0; } }
            public bool CursorVisible { get { return false; } }
            public bool AltScreen { get { return false; } }
            public bool AppCursorKeys { get { return false; } }
            public bool BracketedPaste { get { return false; } }
            public int MouseMode { get { return 0; } }
            public bool MouseSgr { get { return false; } }
            public int ScrollbackCount { get { return _lines.Count - Rows; } }
            public int Viewports;

            public bool CopyDirtyRows(byte[] rowsOut, byte[] dirtyOut) { return false; }

            public void CopyViewport(int offset, byte[] rowsOut)
            {
                Viewports++;
                Array.Clear(rowsOut, 0, rowsOut.Length);
                int first = ScrollbackCount - offset;
                for (int r = 0; r < Rows; r++)
                {
                    string text = _lines[first + r];
                    for (int c = 0; c < Cols && c < text.Length; c++)
                    {
                        int at = (r * Cols + c) * TerminalCell.BytesPerCell;
                        rowsOut[at] = (byte)text[c];
                    }
                }
            }

            public string GetText(int startRow, int startCol, int endRow, int endCol, int offset) { return string.Empty; }
        }

        private static List<TerminalLineText> Lines(params string[] text)
        {
            var list = new List<TerminalLineText>();
            foreach (string t in text)
            {
                list.Add(TerminalLineText.FromString(t));
            }
            return list;
        }

        [Fact]
        public void Find_ReportsEveryHitInOrder_CaseInsensitive()
        {
            List<TextMatch> hits = ScrollbackSearch.Find(Lines("Error one", "ok", "error ERROR"), "error");
            Assert.Equal(new[]
            {
                new TextMatch(0, 0, 5),
                new TextMatch(2, 0, 5),
                new TextMatch(2, 6, 5)
            }, hits);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Find_BlankQuery_ReturnsNothing(string query)
        {
            Assert.Empty(ScrollbackSearch.Find(Lines("anything"), query));
        }

        [Fact]
        public void Find_StopsAtMaxMatches()
        {
            var many = new List<TerminalLineText>();
            for (int i = 0; i < ScrollbackSearch.MaxMatches + 50; i++)
            {
                many.Add(TerminalLineText.FromString("x"));
            }
            Assert.Equal(ScrollbackSearch.MaxMatches, ScrollbackSearch.Find(many, "x").Count);
        }

        [Fact]
        public void Find_NonOverlapping()
        {
            Assert.Equal(2, ScrollbackSearch.Find(Lines("aaaa"), "aa").Count);
        }

        [Fact]
        public void ReadAllLines_CoversScrollbackAndScreenInOrder()
        {
            var text = new List<string>();
            for (int i = 0; i < 11; i++)
            {
                text.Add("line" + i);
            }
            var screen = new LinesScreen(text, 4, 8);
            List<TerminalLineText> lines = ScrollbackSearch.ReadAllLines(screen);
            Assert.Equal(11, lines.Count);
            for (int i = 0; i < 11; i++)
            {
                Assert.Equal("line" + i, lines[i].Text.TrimEnd());
            }
            Assert.True(screen.Viewports <= 4);
        }

        [Fact]
        public void ReadAllLines_ThenFind_ReturnsLogicalLineNumbers()
        {
            var text = new List<string> { "alpha", "needle", "beta", "gamma", "needle" };
            var screen = new LinesScreen(text, 2, 8);
            List<TextMatch> hits = ScrollbackSearch.Find(ScrollbackSearch.ReadAllLines(screen), "needle");
            Assert.Equal(new[] { new TextMatch(1, 0, 6), new TextMatch(4, 0, 6) }, hits);
        }

        [Fact]
        public void OffsetToReveal_CentersAndClamps()
        {
            // 回滚 100、屏幕 20：第 50 行居中需偏移 100-50+10=60。
            Assert.Equal(60, ScrollbackSearch.OffsetToReveal(50, 100, 20));
            Assert.Equal(0, ScrollbackSearch.OffsetToReveal(119, 100, 20));
            Assert.Equal(100, ScrollbackSearch.OffsetToReveal(0, 100, 20));
        }

        [Fact]
        public void ViewportRow_MapsVisibleLines_AndRejectsOthers()
        {
            int offset = ScrollbackSearch.OffsetToReveal(50, 100, 20);
            Assert.Equal(10, ScrollbackSearch.ViewportRow(50, 100, 20, offset));
            Assert.Equal(-1, ScrollbackSearch.ViewportRow(10, 100, 20, offset));
        }

        [Fact]
        public void ScrollTo_ClampsToRange()
        {
            var scroll = new ScrollController();
            scroll.SetScrollbackCount(30);
            scroll.ScrollTo(12);
            Assert.Equal(12, scroll.Offset);
            scroll.ScrollTo(99);
            Assert.Equal(30, scroll.Offset);
            scroll.ScrollTo(-5);
            Assert.Equal(0, scroll.Offset);
        }

        [Fact]
        public void LineText_WideCharacters_MapToCellColumns()
        {
            // 「你好 ok」：你(0-1) 好(2-3) 空格(4) o(5) k(6)
            var cells = new List<TerminalCell>
            {
                Wide('你'), Continuation(), Wide('好'), Continuation(), Narrow(' '), Narrow('o'), Narrow('k')
            };
            var grid = new ListGrid(cells);
            TerminalLineText line = TerminalLineText.FromGrid(grid, 0);
            Assert.Equal("你好 ok", line.Text);
            List<TextMatch> hits = ScrollbackSearch.Find(new List<TerminalLineText> { line }, "好 o");
            Assert.Equal(new TextMatch(0, 2, 4), Assert.Single(hits));
            Assert.Equal(1, line.CharIndexAt(3)); // 续格落回「好」
            Assert.Equal(-1, line.CharIndexAt(40));
        }

        private static TerminalCell Wide(char c)
        {
            return new TerminalCell { Codepoint = c, Attrs = TerminalCell.AttrWide };
        }

        private static TerminalCell Narrow(char c)
        {
            return new TerminalCell { Codepoint = c };
        }

        private static TerminalCell Continuation()
        {
            return new TerminalCell { Codepoint = TerminalCell.WideContinuation };
        }

        private sealed class ListGrid : ISelectionGrid
        {
            private readonly List<TerminalCell> _cells;

            public ListGrid(List<TerminalCell> cells)
            {
                _cells = cells;
            }

            public int Rows { get { return 1; } }
            public int Cols { get { return _cells.Count; } }

            public TerminalCell CellAt(int row, int col)
            {
                return _cells[col];
            }
        }
    }

    public class LinkDetectorTests
    {
        [Theory]
        [InlineData("see https://example.com/a?b=1 now", "https://example.com/a?b=1")]
        [InlineData("(docs: http://x.org/p(1)).", "http://x.org/p(1)")]
        [InlineData("visit www.lumia.net, thanks", "http://www.lumia.net")]
        [InlineData("url=\"https://q.io/x\"", "https://q.io/x")]
        [InlineData("HTTPS://UPPER.COM/", "HTTPS://UPPER.COM/")]
        public void Find_ExtractsUrlWithoutTrailingPunctuation(string text, string expected)
        {
            DetectedLink link = Assert.Single(LinkDetector.Find(text));
            Assert.Equal(expected, link.Url);
        }

        [Theory]
        [InlineData("no links here")]
        [InlineData("awww.not")]
        [InlineData("https://")]
        [InlineData("www.")]
        public void Find_IgnoresNonLinks(string text)
        {
            Assert.Empty(LinkDetector.Find(text));
        }

        [Fact]
        public void Find_MultipleLinks_InOrder()
        {
            List<DetectedLink> links = LinkDetector.Find("a http://one.com b https://two.com");
            Assert.Equal(2, links.Count);
            Assert.Equal("http://one.com", links[0].Url);
            Assert.Equal(2, links[0].Start);
            Assert.Equal("https://two.com", links[1].Url);
        }

        [Fact]
        public void UrlAt_HitsOnlyInsideLink()
        {
            const string text = "go http://h.io/x ok";
            Assert.Equal("http://h.io/x", LinkDetector.UrlAt(text, 3));
            Assert.Equal("http://h.io/x", LinkDetector.UrlAt(text, 15));
            Assert.Null(LinkDetector.UrlAt(text, 16));
            Assert.Null(LinkDetector.UrlAt(text, 0));
        }
    }
}
