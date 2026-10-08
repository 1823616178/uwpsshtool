using SshTool.Core.Appearance;
using Xunit;

namespace SshTool.Core.Tests.Appearance
{
    public class ContrastMathTests
    {
        [Fact]
        public void BlackOnWhite_Is21()
        {
            Assert.Equal(21.0, ContrastMath.Contrast(Rgb.Black, Rgb.White), 2);
        }

        [Fact]
        public void KnownPair_MatchesWcagReference()
        {
            // #777777 on white ≈ 4.48:1（WCAG 常用临界示例）
            Assert.Equal(4.48, ContrastMath.Contrast(Rgb.Parse("#777777"), Rgb.White), 2);
        }

        [Theory]
        [InlineData("#FFB900", true)]   // 系统强调色：金黄 → 深色字
        [InlineData("#00CC6A", true)]   // 浅绿 → 深色字
        [InlineData("#2F6FE0", false)]  // 本应用深色强调色 → 白字
        [InlineData("#0078D7", false)]  // 系统默认蓝 → 白字
        [InlineData("#E81123", false)]  // 红 → 白字
        public void PreferDarkForeground_PicksHigherContrast(string accent, bool dark)
        {
            Rgb bg = Rgb.Parse(accent);
            Assert.Equal(dark, ContrastMath.PreferDarkForeground(bg));
            Rgb fg = ContrastMath.ForegroundFor(bg);
            Rgb other = dark ? Rgb.White : ContrastMath.DarkForeground;
            Assert.True(ContrastMath.Contrast(fg, bg) >= ContrastMath.Contrast(other, bg));
        }

        [Theory]
        [InlineData("#0078D7", "#1A2030")]
        [InlineData("#FFB900", "#EEF1F7")]
        [InlineData("#00CC6A", "#FFFFFF")]
        [InlineData("#744DA9", "#12161F")]
        public void EnsureContrast_ReachesTextMinimum(string fg, string bg)
        {
            Rgb result = ContrastMath.EnsureContrast(Rgb.Parse(fg), Rgb.Parse(bg), ContrastMath.TextMinimum);
            Assert.True(ContrastMath.Contrast(result, Rgb.Parse(bg)) >= ContrastMath.TextMinimum,
                result.ToHex() + " on " + bg);
        }

        [Fact]
        public void EnsureContrast_KeepsAlreadyPassingColor()
        {
            Rgb fg = Rgb.Parse("#6EA2FF");
            Assert.Equal(fg, ContrastMath.EnsureContrast(fg, Rgb.Parse("#12161F"), ContrastMath.TextMinimum));
        }

        [Fact]
        public void Parse_AcceptsArgbAndIgnoresAlpha()
        {
            Assert.Equal(Rgb.Parse("#2F6FE0"), Rgb.Parse("#292F6FE0"));
            Rgb unused;
            Assert.False(Rgb.TryParse("#12345", out unused));
            Assert.False(Rgb.TryParse(null, out unused));
        }

        [Fact]
        public void Composite_BlendsByAlpha()
        {
            Assert.Equal(Rgb.Parse("#808080"), ContrastMath.Composite(Rgb.White, 128, Rgb.Black));
        }
    }

    // 主题判定：旧 Banner.ResolveThemedBrush 在「跟随系统 + 系统浅色」时误判为 Dark。
    public class ThemeKeyResolverTests
    {
        [Theory]
        [InlineData(ThemeModeSetting.System, true, false, "Light")]   // 回归：旧实现返回 Dark
        [InlineData(ThemeModeSetting.System, false, false, "Dark")]
        [InlineData(ThemeModeSetting.Light, false, false, "Light")]
        [InlineData(ThemeModeSetting.Dark, true, false, "Dark")]
        [InlineData(ThemeModeSetting.Dark, true, true, "HighContrast")]
        [InlineData(ThemeModeSetting.System, true, true, "HighContrast")]
        public void Resolve_FollowsModeSystemAndHighContrast(ThemeModeSetting mode, bool systemLight, bool hc, string expected)
        {
            Assert.Equal(expected, ThemeKeyResolver.Resolve(mode, systemLight, hc));
        }

        [Fact]
        public void SystemBackground_WhiteIsLight_BlackIsDark()
        {
            Assert.True(ThemeKeyResolver.IsLightSystemBackground(Rgb.White));
            Assert.False(ThemeKeyResolver.IsLightSystemBackground(Rgb.Black));
        }
    }
}
