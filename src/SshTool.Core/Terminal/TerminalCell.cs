namespace SshTool.Core.Terminal
{
    // T05：16 字节单元格（01-DESIGN.md §7.1，小端，与 native Cell 布局一致）。
    public struct TerminalCell
    {
        public const int BytesPerCell = 16;
        public const uint WideContinuation = 0xFFFFFFFFu;
        public const uint DefaultFgMarker = 0x00000001u;
        public const uint DefaultBgMarker = 0x00000002u;

        public const ushort AttrBold = 1 << 0;
        public const ushort AttrItalic = 1 << 1;
        public const ushort AttrUnderline = 1 << 2;
        public const ushort AttrBlink = 1 << 3;
        public const ushort AttrReverse = 1 << 4;
        public const ushort AttrStrike = 1 << 5;
        public const ushort AttrDim = 1 << 6;
        public const ushort AttrWide = 1 << 7;
        public const ushort AttrInvisible = 1 << 8;
        public const ushort AttrSoftWrap = 1 << 9;

        public uint Codepoint;
        public uint FgArgb;
        public uint BgArgb;
        public ushort Attrs;
        public ushort Reserved;

        public bool IsEmpty
        {
            get { return Codepoint == 0; }
        }

        public bool IsWideContinuation
        {
            get { return Codepoint == WideContinuation; }
        }

        public bool IsWide
        {
            get { return (Attrs & AttrWide) != 0; }
        }

        public string Glyph()
        {
            if (Codepoint == 0 || Codepoint == WideContinuation || Codepoint > 0x10FFFFu)
            {
                return string.Empty;
            }
            return char.ConvertFromUtf32((int)Codepoint);
        }
    }
}
