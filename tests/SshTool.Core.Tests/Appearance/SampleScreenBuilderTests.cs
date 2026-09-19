using System.Collections.Generic;
using SshTool.Core.Appearance;
using SshTool.Core.Models;
using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Appearance
{
    // A03 验收：SampleScreenBuilder 单测（16 色、粗体、下划线、反色）。
    public class SampleScreenBuilderTests
    {
        internal static AppearanceProfile Theme()
        {
            var palette = new List<string>
            {
                "#000000", "#CC0403", "#19CB00", "#CECB00",
                "#0D73CC", "#CB1ED1", "#0DCDCD", "#DDDDDD",
                "#767676", "#F2201F", "#23FD00", "#FAFD00",
                "#1A8FFF", "#FD28FF", "#14FFFF", "#FFFFFF"
            };
            return new AppearanceProfile
            {
                Id = "test",
                Name = "test",
                FontFamily = "JetBrains Mono",
                FontSize = 12,
                LineHeight = 1.2,
                BoldAsBright = true,
                CursorStyle = CursorStyle.Block,
                CursorBlink = true,
                Padding = 4,
                Palette = palette,
                Foreground = "#E8EEFB",
                Background = "#101010",
                Cursor = "#E8EEFB",
                Selection = "#3A4A66"
            };
        }

        [Fact]
        public void Build_CoversAllSixteenPaletteColors()
        {
            SampleScreen screen = SampleScreenBuilder.Build(Theme());
            Assert.Equal(SampleScreenBuilder.DefaultCols, screen.Cols);
            Assert.Equal(SampleScreenBuilder.DefaultRows, screen.Rows);
            Assert.Equal(screen.Cols * screen.Rows * TerminalCell.BytesPerCell, screen.Cells.Length);

            var seen = new HashSet<uint>();
            int count = CellBufferReader.CellCount(screen.Cells);
            for (int i = 0; i < count; i++)
            {
                seen.Add(CellBufferReader.Read(screen.Cells, i).FgArgb);
            }
            for (int i = 0; i < 16; i++)
            {
                uint argb = TerminalPalette.HexToArgb(Theme().Palette[i]);
                Assert.Contains(argb, seen);
            }
        }

        [Fact]
        public void Build_BoldRowHasBoldAttr()
        {
            SampleScreen screen = SampleScreenBuilder.Build(Theme());
            bool found = false;
            for (int c = 0; c < screen.Cols; c++)
            {
                TerminalCell cell = CellBufferReader.Read(screen.Cells, 4, c, screen.Cols);
                if (!cell.IsEmpty && !cell.IsWideContinuation)
                {
                    Assert.Equal(TerminalCell.AttrBold, (ushort)(cell.Attrs & TerminalCell.AttrBold));
                    found = true;
                }
            }
            Assert.True(found);
        }

        [Fact]
        public void Build_UnderlineRowHasUnderlineAttr()
        {
            SampleScreen screen = SampleScreenBuilder.Build(Theme());
            bool found = false;
            for (int c = 0; c < screen.Cols; c++)
            {
                TerminalCell cell = CellBufferReader.Read(screen.Cells, 5, c, screen.Cols);
                if (!cell.IsEmpty && !cell.IsWideContinuation)
                {
                    Assert.Equal(TerminalCell.AttrUnderline, (ushort)(cell.Attrs & TerminalCell.AttrUnderline));
                    found = true;
                }
            }
            Assert.True(found);
        }

        [Fact]
        public void Build_ReverseRowHasReverseAttr()
        {
            SampleScreen screen = SampleScreenBuilder.Build(Theme());
            bool found = false;
            for (int c = 0; c < screen.Cols; c++)
            {
                TerminalCell cell = CellBufferReader.Read(screen.Cells, 6, c, screen.Cols);
                if (!cell.IsEmpty && !cell.IsWideContinuation)
                {
                    Assert.Equal(TerminalCell.AttrReverse, (ushort)(cell.Attrs & TerminalCell.AttrReverse));
                    found = true;
                }
            }
            Assert.True(found);
        }

        [Fact]
        public void Build_WideCharsTakeTwoCells()
        {
            SampleScreen screen = SampleScreenBuilder.Build(Theme());
            bool foundWide = false;
            bool foundContinuation = false;
            for (int c = 0; c < screen.Cols; c++)
            {
                TerminalCell cell = CellBufferReader.Read(screen.Cells, 4, c, screen.Cols);
                if (cell.IsWide)
                {
                    foundWide = true;
                }
                if (cell.IsWideContinuation)
                {
                    foundContinuation = true;
                }
            }
            Assert.True(foundWide);
            Assert.True(foundContinuation);
        }

        [Fact]
        public void Build_CursorInsideBounds()
        {
            SampleScreen screen = SampleScreenBuilder.Build(Theme());
            Assert.InRange(screen.CursorRow, 0, screen.Rows - 1);
            Assert.InRange(screen.CursorCol, 0, screen.Cols - 1);
        }

        [Fact]
        public void Build_NullAppearanceThrows()
        {
            Assert.Throws<System.ArgumentNullException>(() => SampleScreenBuilder.Build(null));
        }

        [Fact]
        public void Build_CustomSizeRespected()
        {
            SampleScreen screen = SampleScreenBuilder.Build(Theme(), 20, 5);
            Assert.Equal(20, screen.Cols);
            Assert.Equal(5, screen.Rows);
            Assert.Equal(20 * 5 * TerminalCell.BytesPerCell, screen.Cells.Length);
        }
    }
}
