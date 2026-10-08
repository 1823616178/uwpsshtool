using System;
using System.Globalization;

namespace SshTool.Core.Appearance
{
    // ui/fix-pass：应用界面配色的纯计算部分（WCAG 2.x 相对亮度 / 对比度），供 ThemeService
    // 在运行时为系统强调色挑前景色、调整强调色文字，也供测试校验 Tokens 调色板。
    // 不依赖 Windows.UI，Core 单测可直接覆盖。
    public struct Rgb : IEquatable<Rgb>
    {
        public readonly byte R;
        public readonly byte G;
        public readonly byte B;

        public Rgb(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }

        public static readonly Rgb White = new Rgb(255, 255, 255);
        public static readonly Rgb Black = new Rgb(0, 0, 0);

        // 接受 #RRGGBB 与 #AARRGGBB（忽略 alpha）。
        public static bool TryParse(string text, out Rgb value)
        {
            value = default(Rgb);
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            string hex = text.Trim().TrimStart('#');
            if (hex.Length == 8)
            {
                hex = hex.Substring(2);
            }
            if (hex.Length != 6)
            {
                return false;
            }
            int n;
            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n))
            {
                return false;
            }
            value = new Rgb((byte)((n >> 16) & 0xFF), (byte)((n >> 8) & 0xFF), (byte)(n & 0xFF));
            return true;
        }

        public static Rgb Parse(string text)
        {
            Rgb value;
            if (!TryParse(text, out value))
            {
                throw new FormatException("not a #RRGGBB color: " + text);
            }
            return value;
        }

        public string ToHex()
        {
            return "#" + R.ToString("X2", CultureInfo.InvariantCulture)
                + G.ToString("X2", CultureInfo.InvariantCulture)
                + B.ToString("X2", CultureInfo.InvariantCulture);
        }

        public bool Equals(Rgb other)
        {
            return R == other.R && G == other.G && B == other.B;
        }

        public override bool Equals(object obj)
        {
            return obj is Rgb && Equals((Rgb)obj);
        }

        public override int GetHashCode()
        {
            return (R << 16) | (G << 8) | B;
        }

        public override string ToString()
        {
            return ToHex();
        }
    }

    public static class ContrastMath
    {
        // WCAG 2.x：正文 4.5:1，大字号 / 图标 / 非文字图形 3:1。
        public const double TextMinimum = 4.5;
        public const double GraphicMinimum = 3.0;

        // 强调色上的深色前景（与 KeyBarKeyLockedForegroundBrush 同值），比纯黑柔和。
        public static readonly Rgb DarkForeground = new Rgb(0x1A, 0x1A, 0x1A);

        public static double Luminance(Rgb c)
        {
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }

        public static double Contrast(Rgb a, Rgb b)
        {
            double la = Luminance(a);
            double lb = Luminance(b);
            double hi = Math.Max(la, lb);
            double lo = Math.Min(la, lb);
            return (hi + 0.05) / (lo + 0.05);
        }

        // 强调色底上用深色还是白色前景：取对比度更高者（浅色系统强调色如黄、青、浅绿用深色字）。
        public static bool PreferDarkForeground(Rgb background)
        {
            return Contrast(background, DarkForeground) > Contrast(background, Rgb.White);
        }

        public static Rgb ForegroundFor(Rgb background)
        {
            return PreferDarkForeground(background) ? DarkForeground : Rgb.White;
        }

        // 线性插值：t=0 → a，t=1 → b。
        public static Rgb Mix(Rgb a, Rgb b, double t)
        {
            if (t <= 0)
            {
                return a;
            }
            if (t >= 1)
            {
                return b;
            }
            return new Rgb(Lerp(a.R, b.R, t), Lerp(a.G, b.G, t), Lerp(a.B, b.B, t));
        }

        // 半透明前景叠到不透明底色上的结果（alpha 0–255）。
        public static Rgb Composite(Rgb foreground, byte alpha, Rgb background)
        {
            return Mix(background, foreground, alpha / 255.0);
        }

        // 让 foreground 在 background 上至少达到 minimum 对比度：深底往白调、浅底往黑调，
        // 步进 5%，最多调到纯白/纯黑（此时对深/浅底一定 ≥ 4.5）。已达标原样返回。
        public static Rgb EnsureContrast(Rgb foreground, Rgb background, double minimum)
        {
            if (Contrast(foreground, background) >= minimum)
            {
                return foreground;
            }
            Rgb target = Luminance(background) < 0.18 ? Rgb.White : Rgb.Black;
            for (int step = 1; step <= 20; step++)
            {
                Rgb candidate = Mix(foreground, target, step * 0.05);
                if (Contrast(candidate, background) >= minimum)
                {
                    return candidate;
                }
            }
            return target;
        }

        private static double Channel(byte v)
        {
            double c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        private static byte Lerp(byte a, byte b, double t)
        {
            double v = a + (b - a) * t;
            return (byte)Math.Max(0, Math.Min(255, (int)Math.Round(v)));
        }
    }

    // 应用主题设置（与 App 侧 AppThemeMode 数值一致：0 跟随系统 / 1 浅色 / 2 深色）。
    public enum ThemeModeSetting
    {
        System = 0,
        Light = 1,
        Dark = 2
    }

    // 「当前实际生效的主题字典键」的唯一判定处。ThemeService.EffectiveThemeKey 与所有
    // 代码侧画刷解析都走这里，避免各控件各写一套（旧 Banner.ResolveThemedBrush 在
    // 「跟随系统 + 系统浅色」下误判为 Dark，就是各写一套的后果）。
    public static class ThemeKeyResolver
    {
        public const string Dark = "Dark";
        public const string Light = "Light";
        public const string HighContrast = "HighContrast";

        public static string Resolve(ThemeModeSetting mode, bool systemIsLight, bool highContrast)
        {
            // 系统高对比度优先于应用主题（XAML 也是这样选 ThemeDictionaries 的）。
            if (highContrast)
            {
                return HighContrast;
            }
            switch (mode)
            {
                case ThemeModeSetting.Light:
                    return Light;
                case ThemeModeSetting.Dark:
                    return Dark;
                default:
                    return systemIsLight ? Light : Dark;
            }
        }

        // UISettings.GetColorValue(UIColorType.Background)：浅色系统为白、深色系统为黑。
        public static bool IsLightSystemBackground(Rgb systemBackground)
        {
            return ContrastMath.Luminance(systemBackground) > 0.5;
        }
    }
}
