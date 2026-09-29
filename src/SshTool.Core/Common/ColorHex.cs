using System;
using System.Globalization;

namespace SshTool.Core.Common
{
    // O14：`#RRGGBB` 解析。此前 ColorSwatchPicker / GroupHeader /
    // AppearanceListViewModel 三处逐字重复同一段 byte.Parse + 回退，改一处就得
    // 改三处。放在 Core 而不是 App：这里只产出 r/g/b 三个字节，不碰 UI 类型
    // （Color/Brush 是 App 层的事），Core 也就能单测。
    public static class ColorHex
    {
        // 只认 7 字符的 "#RRGGBB"。大小写不敏感；不接受 3 位简写与 8 位含 alpha
        // 的形式——本仓的外观配色一律是 6 位，放宽反而会悄悄接受脏数据。
        public static bool TryParse(string hex, out byte r, out byte g, out byte b)
        {
            r = 0;
            g = 0;
            b = 0;
            if (string.IsNullOrEmpty(hex) || hex.Length != 7 || hex[0] != '#')
            {
                return false;
            }
            byte pr;
            byte pg;
            byte pb;
            if (!TryByte(hex, 1, out pr) || !TryByte(hex, 3, out pg) || !TryByte(hex, 5, out pb))
            {
                return false;
            }
            r = pr;
            g = pg;
            b = pb;
            return true;
        }

        private static bool TryByte(string hex, int start, out byte value)
        {
            value = 0;
            // byte.TryParse 的 HexNumber 会接受空白与正负号之外的东西吗？不会，
            // 但它接受 "0x" 前缀以外的任意十六进制位；先自己挡掉非十六进制字符，
            // 免得 "#-1FFFF" 这类输入靠异常兜底。
            for (int i = start; i < start + 2; i++)
            {
                char c = hex[i];
                bool ok = (c >= '0' && c <= '9')
                          || (c >= 'a' && c <= 'f')
                          || (c >= 'A' && c <= 'F');
                if (!ok)
                {
                    return false;
                }
            }
            return byte.TryParse(hex.Substring(start, 2), NumberStyles.HexNumber,
                                 CultureInfo.InvariantCulture, out value);
        }

        // 计算感知亮度（ITU-R BT.601：0.299*R + 0.587*G + 0.114*B）。
        // 阈值 150 区分浅底（黑字）与深底（白字），保证足够的对比度。
        public static bool NeedsDarkText(byte r, byte g, byte b)
        {
            double luminance = 0.299 * r + 0.587 * g + 0.114 * b;
            return luminance > 150.0;
        }
    }
}
