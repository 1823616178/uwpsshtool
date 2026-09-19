using System;
using SshTool.Core.Models;

namespace SshTool.Core.Appearance
{
    // A03：外观草稿脏检查（编辑页「未保存返回确认」用）。只比较用户可编辑的
    // 内容字段；Id/BuiltIn/Extra 不参与（复制/内置标记变化不算脏）。
    public static class AppearanceComparer
    {
        public static bool AreEqual(AppearanceProfile a, AppearanceProfile b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }
            if (a == null || b == null)
            {
                return false;
            }
            if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal))
            {
                return false;
            }
            if (!string.Equals(a.FontFamily, b.FontFamily, StringComparison.Ordinal))
            {
                return false;
            }
            if (a.FontSize != b.FontSize)
            {
                return false;
            }
            if (a.LineHeight != b.LineHeight)
            {
                return false;
            }
            if (a.FontWeightBold != b.FontWeightBold)
            {
                return false;
            }
            if (a.BoldAsBright != b.BoldAsBright)
            {
                return false;
            }
            if (a.CursorStyle != b.CursorStyle)
            {
                return false;
            }
            if (a.CursorBlink != b.CursorBlink)
            {
                return false;
            }
            if (a.Padding != b.Padding)
            {
                return false;
            }
            if (!string.Equals(a.Foreground, b.Foreground, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (!string.Equals(a.Background, b.Background, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (!string.Equals(a.Cursor, b.Cursor, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (!string.Equals(a.Selection, b.Selection, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (a.Palette == null || b.Palette == null)
            {
                return a.Palette == b.Palette;
            }
            if (a.Palette.Count != b.Palette.Count)
            {
                return false;
            }
            for (int i = 0; i < a.Palette.Count; i++)
            {
                if (!string.Equals(a.Palette[i], b.Palette[i], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
