using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SshTool.Core.Tests
{
    // opt/full-pass：XAML 无法在 Linux CI 编译，用静态扫描兜两类「真机才炸」的错误：
    //   1. 深浅主题字典键集不一致（切主题时 ThemeResource 解析失败）；
    //   2. XAML 引用了未定义的 StaticResource/ThemeResource 键（运行时 XamlParseException）。
    public class ThemeTokenTests
    {
        private static readonly Regex KeyAttr = new Regex("x:Key=\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex KeyOrName = new Regex("x:(?:Key|Name)=\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex ResourceRef =
            new Regex(@"\{(?:StaticResource|ThemeResource)\s+([A-Za-z0-9_]+)\}", RegexOptions.Compiled);

        // 系统（generic.xaml）提供的键：Controls.xaml 里重写系统模板时引用
        private static readonly HashSet<string> SystemKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "ContentControlThemeFontFamily",
            "PivotHeaderItemBackgroundDisabled",
            "PivotHeaderItemBackgroundSelectedPointerOver",
            "PivotHeaderItemBackgroundSelectedPressed",
            "PivotHeaderItemBackgroundUnselectedPointerOver",
            "PivotHeaderItemBackgroundUnselectedPressed",
            "PivotHeaderItemCharacterSpacing",
            "PivotHeaderItemLockedTranslation",
            // ui/fix-pass：Tokens.HighContrast.xaml 引用的系统高对比度色（generic.xaml，10240 起即有）
            "SystemColorWindowColor",
            "SystemColorWindowTextColor",
            "SystemColorHighlightColor",
            "SystemColorHighlightTextColor",
            "SystemColorHotlightColor",
            "SystemColorGrayTextColor"
        };

        [Fact]
        public void DarkAndLightThemeDictionaries_HaveIdenticalKeys()
        {
            string themes = Path.Combine(AppDir(), "Themes");
            var dark = Keys(Path.Combine(themes, "Tokens.Dark.xaml"));
            var light = Keys(Path.Combine(themes, "Tokens.Light.xaml"));
            var missingInLight = dark.Except(light, StringComparer.Ordinal).ToList();
            var missingInDark = light.Except(dark, StringComparer.Ordinal).ToList();
            Assert.True(missingInLight.Count == 0, "Light 缺少：" + string.Join(", ", missingInLight));
            Assert.True(missingInDark.Count == 0, "Dark 缺少：" + string.Join(", ", missingInDark));
        }

        // ui/fix-pass：高对比度字典与深色字典键集一致（缺键时高对比度下 ThemeResource 解析失败）。
        [Fact]
        public void HighContrastDictionary_HasIdenticalKeys()
        {
            string themes = Path.Combine(AppDir(), "Themes");
            var dark = Keys(Path.Combine(themes, "Tokens.Dark.xaml"));
            var hc = Keys(Path.Combine(themes, "Tokens.HighContrast.xaml"));
            var missing = dark.Except(hc, StringComparer.Ordinal).ToList();
            var extra = hc.Except(dark, StringComparer.Ordinal).ToList();
            Assert.True(missing.Count == 0, "HighContrast 缺少：" + string.Join(", ", missing));
            Assert.True(extra.Count == 0, "HighContrast 多出：" + string.Join(", ", extra));
            string tokens = File.ReadAllText(Path.Combine(themes, "Tokens.xaml"));
            Assert.Contains("x:Key=\"HighContrast\"", tokens);
        }

        // 代码里按名字取的主题画刷（ThemeService.ResolveBrush / Banner.ResolveThemedBrush("…")）
        // 必须在主题字典里存在：取不到时返回 null，界面上就是透明/不可见。
        [Fact]
        public void CodeResolvedBrushKeys_AreDefined()
        {
            string app = AppDir();
            var dark = Keys(Path.Combine(app, "Themes", "Tokens.Dark.xaml"));
            var re = new Regex("(?:ResolveBrush|ResolveThemedBrush|Brush)\\(\"([A-Za-z0-9]+Brush)\"\\)", RegexOptions.Compiled);
            var missing = new List<string>();
            foreach (string f in Directory.GetFiles(app, "*.cs", SearchOption.AllDirectories))
            {
                string rel = f.Substring(app.Length).Replace('\\', '/');
                if (rel.Contains("/Views/Debug/") || rel.Contains("/obj/") || rel.Contains("/bin/"))
                {
                    continue;
                }
                foreach (Match m in re.Matches(File.ReadAllText(f)))
                {
                    if (!dark.Contains(m.Groups[1].Value))
                    {
                        missing.Add(rel + " → " + m.Groups[1].Value);
                    }
                }
            }
            Assert.True(missing.Count == 0, "代码引用的主题画刷未定义：\n" + string.Join("\n", missing.Distinct()));
        }

        [Fact]
        public void EveryResourceReference_IsDefined()
        {
            string app = AppDir();
            var defined = new HashSet<string>(StringComparer.Ordinal);
            foreach (string f in Directory.GetFiles(Path.Combine(app, "Themes"), "*.xaml"))
            {
                defined.UnionWith(Keys(f));
            }
            string appXaml = Path.Combine(app, "App.xaml");
            if (File.Exists(appXaml))
            {
                defined.UnionWith(Keys(appXaml));
            }

            var missing = new List<string>();
            foreach (string f in Directory.GetFiles(app, "*.xaml", SearchOption.AllDirectories))
            {
                string rel = f.Substring(app.Length).Replace('\\', '/');
                if (rel.Contains("/Views/Debug/") || rel.Contains("/obj/") || rel.Contains("/bin/"))
                {
                    continue;
                }
                string text = File.ReadAllText(f);
                var local = new HashSet<string>(KeyOrName.Matches(text).Cast<Match>().Select(m => m.Groups[1].Value),
                    StringComparer.Ordinal);
                foreach (Match m in ResourceRef.Matches(text))
                {
                    string key = m.Groups[1].Value;
                    if (!defined.Contains(key) && !local.Contains(key) && !SystemKeys.Contains(key))
                    {
                        missing.Add(rel + " → " + key);
                    }
                }
            }
            Assert.True(missing.Count == 0, "未定义的资源键：\n" + string.Join("\n", missing.Distinct()));
        }

        private static HashSet<string> Keys(string path)
        {
            return new HashSet<string>(KeyAttr.Matches(File.ReadAllText(path)).Cast<Match>().Select(m => m.Groups[1].Value),
                StringComparer.Ordinal);
        }

        private static string AppDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "src", "SshTool.App");
                if (Directory.Exists(Path.Combine(candidate, "Themes")))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("找不到 src/SshTool.App（从 " + AppContext.BaseDirectory + " 向上搜索）");
        }
    }
}
