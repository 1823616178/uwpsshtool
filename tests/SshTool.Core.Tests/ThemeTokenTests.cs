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
            "PivotHeaderItemLockedTranslation"
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
