using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SshTool.Core.Appearance;
using Xunit;

namespace SshTool.Core.Tests.Appearance
{
    // ui/fix-pass：把 Tokens.Dark/Light.xaml 的配色按 WCAG 2.x 校验，防止调色时回退。
    // 正文/小字 ≥ 4.5:1；非文字图形（聚焦描边、状态点）≥ 3:1。
    // 渐变画刷按每个 GradientStop 分别校验；半透明叠层先合成到底色再算。
    public class PaletteContrastTests
    {
        private static readonly string[] Surfaces = { "AppBgBrush", "AppSurfaceBrush", "AppSurfaceAltBrush", "AppCardBrush" };

        private static readonly string[] TextOnSurfaces =
        {
            "AppTextBrush", "AppTextDimBrush", "AppTextFaintBrush", "AppAccentTextBrush",
            "AppDangerBrush", "AppWarningBrush", "AppSuccessBrush", "AppInfoBrush"
        };

        [Theory]
        [InlineData("Dark")]
        [InlineData("Light")]
        public void TextTokens_OnSurfaces_MeetTextMinimum(string theme)
        {
            var t = Load(theme);
            var bad = new List<string>();
            foreach (string fg in TextOnSurfaces)
            {
                foreach (string bg in Surfaces)
                {
                    foreach (Rgb b in t[bg])
                    {
                        Check(bad, fg, t[fg][0], bg, b, ContrastMath.TextMinimum);
                    }
                }
            }
            Assert.True(bad.Count == 0, theme + "：\n" + string.Join("\n", bad));
        }

        [Theory]
        [InlineData("Dark")]
        [InlineData("Light")]
        public void ForegroundsOnFills_MeetTextMinimum(string theme)
        {
            var t = Load(theme);
            var bad = new List<string>();
            Check(bad, "AppOnAccentBrush", t["AppOnAccentBrush"][0], "AppAccentBrush", t["AppAccentBrush"][0], ContrastMath.TextMinimum);
            Check(bad, "AppOnAccentBrush", t["AppOnAccentBrush"][0], "AppDangerFillBrush", t["AppDangerFillBrush"][0], ContrastMath.TextMinimum);
            Check(bad, "AppOnAccentBrush", t["AppOnAccentBrush"][0], "KeyBarKeyActiveBrush", t["KeyBarKeyActiveBrush"][0], ContrastMath.TextMinimum);
            Check(bad, "KeyBarKeyLockedForegroundBrush", t["KeyBarKeyLockedForegroundBrush"][0],
                "KeyBarKeyLockedBrush", t["KeyBarKeyLockedBrush"][0], ContrastMath.TextMinimum);
            foreach (Rgb key in t["KeyBarKeyIdleBrush"])
            {
                Check(bad, "AppTextBrush", t["AppTextBrush"][0], "KeyBarKeyIdleBrush", key, ContrastMath.TextMinimum);
            }
            Assert.True(bad.Count == 0, theme + "：\n" + string.Join("\n", bad));
        }

        // 强调色叠层（快速连接条、徽章底）上的强调色文字/图标。
        [Theory]
        [InlineData("Dark")]
        [InlineData("Light")]
        public void AccentText_OnAccentSoftOverlay_MeetsTextMinimum(string theme)
        {
            var t = Load(theme);
            byte alpha = Alpha[theme + "/AppAccentSoftBrush"];
            var bad = new List<string>();
            foreach (string bg in Surfaces)
            {
                foreach (Rgb b in t[bg])
                {
                    Rgb overlay = ContrastMath.Composite(t["AppAccentSoftBrush"][0], alpha, b);
                    Check(bad, "AppAccentTextBrush", t["AppAccentTextBrush"][0], "AccentSoft/" + bg, overlay, ContrastMath.TextMinimum);
                }
            }
            Assert.True(bad.Count == 0, theme + "：\n" + string.Join("\n", bad));
        }

        // 危险提示块：AppDangerSoftBrush 叠层上的正文（AppTextBrush）。
        [Theory]
        [InlineData("Dark")]
        [InlineData("Light")]
        public void Text_OnDangerSoftOverlay_MeetsTextMinimum(string theme)
        {
            var t = Load(theme);
            byte alpha = Alpha[theme + "/AppDangerSoftBrush"];
            var bad = new List<string>();
            foreach (string bg in Surfaces)
            {
                foreach (Rgb b in t[bg])
                {
                    Rgb overlay = ContrastMath.Composite(t["AppDangerSoftBrush"][0], alpha, b);
                    Check(bad, "AppTextBrush", t["AppTextBrush"][0], "DangerSoft/" + bg, overlay, ContrastMath.TextMinimum);
                }
            }
            Assert.True(bad.Count == 0, theme + "：\n" + string.Join("\n", bad));
        }

        // 聚焦描边、开关填充等非文字图形 ≥ 3:1。
        [Theory]
        [InlineData("Dark")]
        [InlineData("Light")]
        public void AccentFill_OnSurfaces_MeetsGraphicMinimum(string theme)
        {
            var t = Load(theme);
            var bad = new List<string>();
            foreach (string bg in new[] { "AppSurfaceBrush", "AppSurfaceAltBrush" })
            {
                Check(bad, "AppAccentBrush", t["AppAccentBrush"][0], bg, t[bg][0], ContrastMath.GraphicMinimum);
            }
            Assert.True(bad.Count == 0, theme + "：\n" + string.Join("\n", bad));
        }

        private static void Check(List<string> bad, string fgName, Rgb fg, string bgName, Rgb bg, double min)
        {
            double ratio = ContrastMath.Contrast(fg, bg);
            if (ratio < min)
            {
                bad.Add(string.Format("{0} {1} on {2} {3} = {4:0.00} < {5}", fgName, fg.ToHex(), bgName, bg.ToHex(), ratio, min));
            }
        }

        private static readonly Dictionary<string, byte> Alpha = new Dictionary<string, byte>(StringComparer.Ordinal);

        // 键 → 颜色列表（纯色 1 个；渐变为各 stop）。同时记录纯色画刷的 alpha。
        private static Dictionary<string, List<Rgb>> Load(string theme)
        {
            string path = Path.Combine(AppDir(), "Themes", "Tokens." + theme + ".xaml");
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var map = new Dictionary<string, List<Rgb>>(StringComparer.Ordinal);
            foreach (XElement e in XDocument.Load(path).Root.Elements())
            {
                XAttribute key = e.Attribute(x + "Key");
                if (key == null)
                {
                    continue;
                }
                var colors = new List<string>();
                if (e.Name.LocalName == "SolidColorBrush")
                {
                    colors.Add((string)e.Attribute("Color"));
                }
                else if (e.Name.LocalName == "LinearGradientBrush")
                {
                    colors.AddRange(e.Elements().Where(s => s.Name.LocalName == "GradientStop")
                        .Select(s => (string)s.Attribute("Color")));
                }
                var parsed = new List<Rgb>();
                foreach (string c in colors)
                {
                    Rgb rgb;
                    if (Rgb.TryParse(c, out rgb))
                    {
                        parsed.Add(rgb);
                    }
                }
                if (parsed.Count > 0)
                {
                    map[key.Value] = parsed;
                    string raw = colors[0].TrimStart('#');
                    Alpha[theme + "/" + key.Value] = raw.Length == 8
                        ? Convert.ToByte(raw.Substring(0, 2), 16)
                        : (byte)255;
                }
            }
            return map;
        }

        private static string AppDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "src", "SshTool.App");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("找不到 src/SshTool.App");
        }
    }
}
