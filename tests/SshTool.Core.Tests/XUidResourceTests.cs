using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace SshTool.Core.Tests
{
    // ui/fix-pass：x:Uid 只认「Uid.属性」形式的 resw 键（如 About_AppName.Text）。
    // 只有裸键（About_AppName）时运行时被静默忽略，界面空白；属性与元素类型不匹配
    // （Button 上写 .Text）则在真机上抛 XamlParseException。XAML 无法在 Linux CI 编译，
    // 这里静态兜住三类错误：
    //   1. 每个 x:Uid 在中英两份 resw 都至少有一个带属性后缀的键；
    //   2. 附加属性键必须写全限定形式 Uid.[using:命名空间]类.属性；
    //   3. 常见内置控件 / 本工程自定义控件上，后缀属性必须真实存在。
    // 另外兜住代码里 ResourceLoader.GetString("Uid/属性") 指向的键必须存在。
    public class XUidResourceTests
    {
        private static readonly Regex UidTag = new Regex(
            "<([A-Za-z_][\\w:.]*)\\b[^<>]*?x:Uid=\"([^\"]+)\"", RegexOptions.Compiled);

        private static readonly Regex SlashLookup = new Regex(
            "GetString\\(\"([A-Za-z0-9_]+)/([A-Za-z0-9_.\\[\\]:]+)\"\\)", RegexOptions.Compiled);

        // 内置控件可被 x:Uid 赋值的文案属性（只列本工程用到的类型；未列出的类型不校验）。
        private static readonly Dictionary<string, string[]> BuiltInProps =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                { "TextBlock", new[] { "Text" } },
                { "Run", new[] { "Text" } },
                { "Button", new[] { "Content" } },
                { "HyperlinkButton", new[] { "Content" } },
                { "RepeatButton", new[] { "Content" } },
                { "ToggleButton", new[] { "Content" } },
                { "CheckBox", new[] { "Content" } },
                { "RadioButton", new[] { "Content" } },
                { "ComboBoxItem", new[] { "Content" } },
                { "ListViewItem", new[] { "Content" } },
                { "AppBarButton", new[] { "Label" } },
                { "AppBarToggleButton", new[] { "Label" } },
                { "MenuFlyoutItem", new[] { "Text" } },
                { "ToggleMenuFlyoutItem", new[] { "Text" } },
                { "MenuFlyoutSubItem", new[] { "Text" } },
                { "PivotItem", new[] { "Header" } },
                { "TextBox", new[] { "Header", "PlaceholderText", "Text" } },
                { "PasswordBox", new[] { "Header", "PlaceholderText" } },
                { "AutoSuggestBox", new[] { "Header", "PlaceholderText" } },
                { "ComboBox", new[] { "Header", "PlaceholderText" } },
                { "ToggleSwitch", new[] { "Header", "OnContent", "OffContent" } },
                { "Slider", new[] { "Header" } },
                // CloseButtonText 需 16299，15063 真机上会炸，故意不列入。
                { "ContentDialog", new[] { "Title", "PrimaryButtonText", "SecondaryButtonText" } },
                { "Grid", new string[0] },
                { "StackPanel", new string[0] },
                { "Border", new string[0] }
            };

        [Theory]
        [InlineData("zh-cn")]
        [InlineData("en-us")]
        public void EveryXUid_HasAtLeastOneSuffixedKey(string lang)
        {
            var byUid = SuffixesByUid(LoadKeys(lang));
            var bad = new List<string>();
            foreach (var use in UidUses())
            {
                if (!byUid.ContainsKey(use.Uid))
                {
                    bad.Add(use.Where + " x:Uid=\"" + use.Uid + "\"");
                }
            }
            Assert.True(bad.Count == 0,
                lang + " 以下 x:Uid 没有任何「Uid.属性」键（界面会空白）：\n" + string.Join("\n", bad));
        }

        [Fact]
        public void AttachedPropertyKeys_AreFullyQualified()
        {
            var bad = LoadKeys("zh-cn")
                .Where(k => k.Contains('.'))
                .Select(k => k.Substring(k.IndexOf('.') + 1))
                .Where(p => p.Contains('.') && !p.StartsWith("[using:", StringComparison.Ordinal))
                .Distinct()
                .ToList();
            Assert.True(bad.Count == 0,
                "附加属性 resw 键必须写成 Uid.[using:Windows.UI.Xaml.Automation]AutomationProperties.Name：\n"
                + string.Join("\n", bad));
        }

        [Fact]
        public void SuffixedProperty_ExistsOnElementType()
        {
            var byUid = SuffixesByUid(LoadKeys("zh-cn"));
            var bad = new List<string>();
            foreach (var use in UidUses())
            {
                List<string> props;
                if (!byUid.TryGetValue(use.Uid, out props))
                {
                    continue;   // 由 EveryXUid_HasAtLeastOneSuffixedKey 报告
                }
                foreach (string prop in props)
                {
                    if (prop.StartsWith("[using:", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    bool? ok = PropertyExists(use.Tag, prop);
                    if (ok == false)
                    {
                        bad.Add(use.Where + " <" + use.Tag + "> 没有属性 " + prop + "（键 " + use.Uid + "." + prop + "）");
                    }
                }
            }
            Assert.True(bad.Count == 0, "resw 属性后缀与元素类型不匹配（真机 XamlParseException）：\n"
                + string.Join("\n", bad.Distinct()));
        }

        [Fact]
        public void SlashLookupsInCode_ResolveToExistingKeys()
        {
            var keys = new HashSet<string>(LoadKeys("zh-cn"), StringComparer.Ordinal);
            var bad = new List<string>();
            foreach (string file in Directory.GetFiles(AppDir(), "*.cs", SearchOption.AllDirectories))
            {
                if (IsExcluded(file))
                {
                    continue;
                }
                foreach (Match m in SlashLookup.Matches(File.ReadAllText(file)))
                {
                    string key = m.Groups[1].Value + "." + m.Groups[2].Value;
                    if (!keys.Contains(key))
                    {
                        bad.Add(Path.GetFileName(file) + ": GetString(\"" + m.Groups[1].Value + "/" + m.Groups[2].Value + "\")");
                    }
                }
            }
            Assert.True(bad.Count == 0, "代码按 Uid/属性 取的资源键不存在：\n" + string.Join("\n", bad));
        }

        private static bool? PropertyExists(string tag, string prop)
        {
            string[] allowed;
            if (BuiltInProps.TryGetValue(tag, out allowed))
            {
                return allowed.Contains(prop, StringComparer.Ordinal);
            }
            if (tag.StartsWith("controls:", StringComparison.Ordinal))
            {
                string cs = Path.Combine(AppDir(), "Controls", tag.Substring("controls:".Length) + ".xaml.cs");
                if (!File.Exists(cs))
                {
                    return null;
                }
                string text = File.ReadAllText(cs);
                return Regex.IsMatch(text, "\\bpublic\\s+[\\w.<>]+\\s+" + Regex.Escape(prop) + "\\b");
            }
            return null;
        }

        private sealed class UidUse
        {
            public string Tag;
            public string Uid;
            public string Where;
        }

        private static IEnumerable<UidUse> UidUses()
        {
            string app = AppDir();
            foreach (string file in Directory.GetFiles(app, "*.xaml", SearchOption.AllDirectories))
            {
                if (IsExcluded(file))
                {
                    continue;
                }
                string text = File.ReadAllText(file);
                foreach (Match m in UidTag.Matches(text))
                {
                    int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                    yield return new UidUse
                    {
                        Tag = m.Groups[1].Value,
                        Uid = m.Groups[2].Value,
                        Where = file.Substring(app.Length + 1).Replace('\\', '/') + ":" + line
                    };
                }
            }
        }

        private static bool IsExcluded(string path)
        {
            string p = path.Replace('\\', '/');
            return p.Contains("/obj/") || p.Contains("/bin/");
        }

        private static Dictionary<string, List<string>> SuffixesByUid(IEnumerable<string> keys)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string key in keys)
            {
                int dot = key.IndexOf('.');
                if (dot <= 0)
                {
                    continue;
                }
                string uid = key.Substring(0, dot);
                List<string> list;
                if (!map.TryGetValue(uid, out list))
                {
                    list = new List<string>();
                    map[uid] = list;
                }
                list.Add(key.Substring(dot + 1));
            }
            return map;
        }

        private static List<string> LoadKeys(string lang)
        {
            string path = Path.Combine(AppDir(), "Strings", lang, "Resources.resw");
            return XDocument.Load(path).Root.Elements("data")
                .Where(e => e.Attribute("name") != null)
                .Select(e => e.Attribute("name").Value)
                .ToList();
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
