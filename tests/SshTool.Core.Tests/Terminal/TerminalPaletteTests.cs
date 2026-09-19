using System;
using System.Collections.Generic;
using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    public class TerminalPaletteTests
    {
        private static readonly uint[] Palette = TerminalPalette.CreateXtermPalette();

        [Fact]
        public void HexToArgb_ParsesRgbAndArgb()
        {
            Assert.Equal(0xFFE8EEFBu, TerminalPalette.HexToArgb("#E8EEFB"));
            Assert.Equal(0xFF000000u, TerminalPalette.HexToArgb("#000000"));
            Assert.Equal(0x80FF0000u, TerminalPalette.HexToArgb("#80FF0000"));
            Assert.Equal(0xFFAABBCCu, TerminalPalette.HexToArgb("#aabbcc"));
        }

        [Fact]
        public void HexToArgb_RejectsInvalid()
        {
            Assert.Throws<ArgumentNullException>(() => TerminalPalette.HexToArgb(null));
            string[] bad = { "FF0000", "#12345", "#1234567", "red", "", "#GGGGGG" };
            for (int i = 0; i < bad.Length; i++)
            {
                Assert.Throws<ArgumentException>(() => TerminalPalette.HexToArgb(bad[i]));
            }
        }

        [Fact]
        public void PaletteToArgb_Requires16()
        {
            var hex = new List<string>
            {
                "#000000", "#CC0403", "#19CB00", "#CECB00",
                "#0D73CC", "#CB1ED1", "#0DCDCD", "#DDDDDD",
                "#767676", "#F2201F", "#23FD00", "#FAFD00",
                "#1A8FFF", "#FD28FF", "#14FFFF", "#FFFFFF"
            };
            uint[] argb = TerminalPalette.PaletteToArgb(hex);
            Assert.Equal(16, argb.Length);
            Assert.Equal(0xFF000000u, argb[0]);
            Assert.Equal(0xFFF2201Fu, argb[9]);

            hex.RemoveAt(15);
            Assert.Throws<ArgumentException>(() => TerminalPalette.PaletteToArgb(hex));
            Assert.Throws<ArgumentNullException>(() => TerminalPalette.PaletteToArgb(null));
        }

        [Fact]
        public void ApplyBoldAsBright_MapsLowAnsiToBright()
        {
            Assert.Equal(Palette[9], TerminalPalette.ApplyBoldAsBright(Palette[1], true, Palette, true));
            Assert.Equal(Palette[1], TerminalPalette.ApplyBoldAsBright(Palette[1], false, Palette, true));
            Assert.Equal(Palette[1], TerminalPalette.ApplyBoldAsBright(Palette[1], true, Palette, false));
            Assert.Equal(0xFF123456u, TerminalPalette.ApplyBoldAsBright(0xFF123456u, true, Palette, true));
            Assert.Equal(Palette[9], TerminalPalette.ApplyBoldAsBright(Palette[9], true, Palette, true));
            Assert.Equal(Palette[1], TerminalPalette.ApplyBoldAsBright(Palette[1], true, null, true));
        }

        [Fact]
        public void ApplyDim_MixesHalfwayKeepingFgAlpha()
        {
            Assert.Equal(0xFF7F7F7Fu, TerminalPalette.ApplyDim(0xFFFFFFFFu, 0xFF000000u));
            // (0xE8+0x0E)>>1=0x7B，(0xEE+0x13)>>1=0x80，(0xFB+0x1D)>>1=0x8C
            Assert.Equal(0xFF7B808Cu, TerminalPalette.ApplyDim(0xFFE8EEFBu, 0xFF0E131Du));
            Assert.Equal(0x80u, TerminalPalette.ApplyDim(0x80FFFFFFu, 0xFF000000u) >> 24);
        }

        [Fact]
        public void WithOpacity_ScalesAlphaAndClamps()
        {
            Assert.Equal(0x80E8EEFBu, TerminalPalette.WithOpacity(0xFFE8EEFBu, 0.5));
            Assert.Equal(0xFFE8EEFBu, TerminalPalette.WithOpacity(0xFFE8EEFBu, 1));
            Assert.Equal(0xFFE8EEFBu, TerminalPalette.WithOpacity(0xFFE8EEFBu, 2));
            Assert.Equal(0x00E8EEFBu, TerminalPalette.WithOpacity(0xFFE8EEFBu, 0));
            Assert.Equal(0x00E8EEFBu, TerminalPalette.WithOpacity(0xFFE8EEFBu, -1));
        }

        [Fact]
        public void Resolve_ReplacesDefaultMarkers()
        {
            const uint fg = 0xFFE8EEFBu;
            const uint bg = 0xFF0E131Du;
            ResolvedCellStyle s = TerminalPalette.Resolve(
                TerminalCell.DefaultFgMarker, TerminalCell.DefaultBgMarker, 0,
                fg, bg, Palette, true);
            Assert.Equal(fg, s.FgArgb);
            Assert.Equal(bg, s.BgArgb);
            Assert.False(s.Bold);
            Assert.False(s.Invisible);
        }

        [Fact]
        public void Resolve_ReverseSwapsAfterMarkers()
        {
            ResolvedCellStyle s = TerminalPalette.Resolve(
                0xFFFF0000u, 0xFF0000FFu, TerminalCell.AttrReverse,
                0xFFE8EEFBu, 0xFF000000u, Palette, true);
            Assert.Equal(0xFFFF0000u, s.BgArgb);
            Assert.Equal(0xFF0000FFu, s.FgArgb);
        }

        [Fact]
        public void Resolve_BoldAsBrightAfterReverse()
        {
            // reverse 后前景变成 palette[1]，bold-as-bright 再映到 palette[9]
            ResolvedCellStyle s = TerminalPalette.Resolve(
                0xFF111111u, Palette[1],
                (ushort)(TerminalCell.AttrBold | TerminalCell.AttrReverse),
                0xFFE8EEFBu, 0xFF000000u, Palette, true);
            Assert.Equal(Palette[9], s.FgArgb);
            Assert.Equal(0xFF111111u, s.BgArgb);
            Assert.True(s.Bold);
        }

        [Fact]
        public void Resolve_DimMixesTowardBackground()
        {
            ResolvedCellStyle s = TerminalPalette.Resolve(
                0xFFFFFFFFu, 0xFF000000u, TerminalCell.AttrDim,
                0xFFE8EEFBu, 0xFF000000u, Palette, true);
            Assert.Equal(0xFF7F7F7Fu, s.FgArgb);
            Assert.Equal(0xFF000000u, s.BgArgb);
        }

        [Fact]
        public void Resolve_FlagsPassThrough()
        {
            ushort attrs = (ushort)(TerminalCell.AttrItalic | TerminalCell.AttrUnderline
                | TerminalCell.AttrStrike | TerminalCell.AttrInvisible);
            ResolvedCellStyle s = TerminalPalette.Resolve(
                1, 2, attrs, 0xFFu, 0xFFu, Palette, false);
            Assert.True(s.Italic);
            Assert.True(s.Underline);
            Assert.True(s.Strike);
            Assert.True(s.Invisible);
            Assert.False(s.Bold);
        }

        [Fact]
        public void CreateXtermPalette_MatchesNativeDefaults()
        {
            uint[] p = TerminalPalette.CreateXtermPalette();
            Assert.Equal(16, p.Length);
            Assert.Equal(0xFFCD0000u, p[1]);
            Assert.Equal(0xFFE5E5E5u, p[7]);
            Assert.Equal(0xFFFF0000u, p[9]);
            p[1] = 0;
            Assert.Equal(0xFFCD0000u, TerminalPalette.CreateXtermPalette()[1]);
        }
    }
}
