using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SshTool.Core.Terminal
{
    // T06：外观侧配色纯函数（01-DESIGN.md §7.3）。
    // 网格里的 fg/bg 已是 ARGB；本类处理标记值替换、bold-as-bright 反查、
    // reverse 互换、dim 50% 混合。管线顺序与鸿蒙端 TerminalDrawCommands 一致：
    // reverse → bold-as-bright → dim。
    public struct ResolvedCellStyle
    {
        public uint FgArgb;
        public uint BgArgb;
        public bool Bold;
        public bool Italic;
        public bool Underline;
        public bool Strike;
        public bool Invisible;
    }

    public static class TerminalPalette
    {
        public const int Size = 16;

        // 与 native vterm_screen.cpp kXtermPalette 逐项一致；native 未注入外观
        // 调色板时单元格 ARGB 来自这张表，渲染层 bold-as-bright 反查必须对得上。
        private static readonly uint[] Xterm =
        {
            0xFF000000u, 0xFFCD0000u, 0xFF00CD00u, 0xFFCDCD00u,
            0xFF0000EEu, 0xFFCD00CDu, 0xFF00CDCDu, 0xFFE5E5E5u,
            0xFF7F7F7Fu, 0xFFFF0000u, 0xFF00FF00u, 0xFFFFFF00u,
            0xFF5C5CFFu, 0xFFFF00FFu, 0xFF00FFFFu, 0xFFFFFFFFu
        };

        private static readonly Regex HexColor = new Regex("^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})$");

        public static uint[] CreateXtermPalette()
        {
            var copy = new uint[Size];
            Array.Copy(Xterm, copy, Size);
            return copy;
        }

        public static uint HexToArgb(string hex)
        {
            if (hex == null)
            {
                throw new ArgumentNullException(nameof(hex));
            }
            if (!HexColor.IsMatch(hex))
            {
                throw new ArgumentException("非法颜色值（期望 #RRGGBB 或 #AARRGGBB）: " + hex, nameof(hex));
            }
            string body = hex.Substring(1);
            uint value = uint.Parse(body, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (body.Length == 6)
            {
                return 0xFF000000u | value;
            }
            return value;
        }

        public static uint[] PaletteToArgb(IList<string> palette)
        {
            if (palette == null)
            {
                throw new ArgumentNullException(nameof(palette));
            }
            if (palette.Count != Size)
            {
                throw new ArgumentException("调色板必须为 " + Size + " 色，实际 " + palette.Count, nameof(palette));
            }
            var result = new uint[Size];
            for (int i = 0; i < Size; i++)
            {
                result[i] = HexToArgb(palette[i]);
            }
            return result;
        }

        public static uint ApplyBoldAsBright(uint fgArgb, bool bold, uint[] paletteArgb, bool enabled)
        {
            if (!enabled || !bold || paletteArgb == null)
            {
                return fgArgb;
            }
            int limit = paletteArgb.Length < 8 ? paletteArgb.Length : 8;
            for (int i = 0; i < limit; i++)
            {
                if (paletteArgb[i] == fgArgb && i + 8 < paletteArgb.Length)
                {
                    return paletteArgb[i + 8];
                }
            }
            return fgArgb;
        }

        // ATTR_DIM：前景 RGB 向背景混 50%，alpha 保留前景。
        public static uint ApplyDim(uint fgArgb, uint bgArgb)
        {
            uint a = (fgArgb >> 24) & 0xFFu;
            uint r = (((fgArgb >> 16) & 0xFFu) + ((bgArgb >> 16) & 0xFFu)) >> 1;
            uint g = (((fgArgb >> 8) & 0xFFu) + ((bgArgb >> 8) & 0xFFu)) >> 1;
            uint b = ((fgArgb & 0xFFu) + (bgArgb & 0xFFu)) >> 1;
            return (a << 24) | (r << 16) | (g << 8) | b;
        }

        public static uint WithOpacity(uint argb, double opacity)
        {
            if (opacity < 0)
            {
                opacity = 0;
            }
            else if (opacity > 1)
            {
                opacity = 1;
            }
            uint a = (uint)Math.Round(((argb >> 24) & 0xFFu) * opacity, MidpointRounding.AwayFromZero);
            return (a << 24) | (argb & 0x00FFFFFFu);
        }

        public static uint ResolveFgMarker(uint fgArgb, uint defaultFgArgb)
        {
            return fgArgb == TerminalCell.DefaultFgMarker ? defaultFgArgb : fgArgb;
        }

        public static uint ResolveBgMarker(uint bgArgb, uint defaultBgArgb)
        {
            return bgArgb == TerminalCell.DefaultBgMarker ? defaultBgArgb : bgArgb;
        }

        public static ResolvedCellStyle Resolve(
            uint fgArgb, uint bgArgb, ushort attrs,
            uint defaultFgArgb, uint defaultBgArgb,
            uint[] paletteArgb, bool boldAsBright)
        {
            uint fg = ResolveFgMarker(fgArgb, defaultFgArgb);
            uint bg = ResolveBgMarker(bgArgb, defaultBgArgb);
            bool bold = (attrs & TerminalCell.AttrBold) != 0;
            if ((attrs & TerminalCell.AttrReverse) != 0)
            {
                uint swap = fg;
                fg = bg;
                bg = swap;
            }
            fg = ApplyBoldAsBright(fg, bold, paletteArgb, boldAsBright);
            if ((attrs & TerminalCell.AttrDim) != 0)
            {
                fg = ApplyDim(fg, bg);
            }
            return new ResolvedCellStyle
            {
                FgArgb = fg,
                BgArgb = bg,
                Bold = bold,
                Italic = (attrs & TerminalCell.AttrItalic) != 0,
                Underline = (attrs & TerminalCell.AttrUnderline) != 0,
                Strike = (attrs & TerminalCell.AttrStrike) != 0,
                Invisible = (attrs & TerminalCell.AttrInvisible) != 0
            };
        }

        public static ResolvedCellStyle Resolve(
            CellRun run, uint defaultFgArgb, uint defaultBgArgb,
            uint[] paletteArgb, bool boldAsBright)
        {
            if (run == null)
            {
                throw new ArgumentNullException(nameof(run));
            }
            return Resolve(run.FgArgb, run.BgArgb, run.Attrs,
                defaultFgArgb, defaultBgArgb, paletteArgb, boldAsBright);
        }
    }
}
