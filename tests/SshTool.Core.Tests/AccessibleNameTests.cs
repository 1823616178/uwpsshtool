using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace SshTool.Core.Tests
{
    // ui/fix-pass：只有图标、没有文字的按钮必须有无障碍名，否则读屏只报「按钮」。
    // 认可的来源：Content/Label 属性、文字子元素、AutomationProperties.Name 属性、x:Uid 对应的
    // .Content / .Label / .[using:Windows.UI.Xaml.Automation]AutomationProperties.Name 键，
    // 或 code-behind 里给该 x:Name 赋了 Content / SetName。Themes/（模板部件）与 Views/Debug/ 豁免。
    public class AccessibleNameTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
        private static readonly string[] ButtonTypes =
        {
            "Button", "AppBarButton", "AppBarToggleButton", "ToggleButton", "HyperlinkButton", "RepeatButton"
        };
        private static readonly string[] TextualChildren = { "TextBlock", "StackPanel", "Grid", "Border" };

        [Fact]
        public void IconOnlyButtons_HaveAccessibleNames()
        {
            string app = AppDir();
            HashSet<string> keys = ReswKeys(Path.Combine(app, "Strings", "zh-cn", "Resources.resw"));
            var bad = new List<string>();
            foreach (string file in Directory.GetFiles(app, "*.xaml", SearchOption.AllDirectories))
            {
                string rel = file.Substring(app.Length + 1).Replace('\\', '/');
                if (rel.StartsWith("Themes/", StringComparison.Ordinal)
                    || rel.StartsWith("Views/Debug/", StringComparison.Ordinal)
                    || rel.Contains("/obj/") || rel.Contains("/bin/") || rel.StartsWith("obj/") || rel.StartsWith("bin/"))
                {
                    continue;
                }
                string codeBehind = File.Exists(file + ".cs") ? File.ReadAllText(file + ".cs") : string.Empty;
                XDocument doc = XDocument.Load(file);
                foreach (XElement el in doc.Descendants())
                {
                    string tag = el.Name.LocalName;
                    if (!ButtonTypes.Contains(tag) || tag.Contains("."))
                    {
                        continue;
                    }
                    if (HasName(el, tag, keys, codeBehind))
                    {
                        continue;
                    }
                    bad.Add(rel + ": <" + tag + " x:Name=" + (string)el.Attribute(X + "Name")
                        + " x:Uid=" + (string)el.Attribute(X + "Uid") + ">");
                }
            }
            Assert.True(bad.Count == 0, "缺少无障碍名的图标按钮：\n" + string.Join("\n", bad));
        }

        private static bool HasName(XElement el, string tag, HashSet<string> keys, string codeBehind)
        {
            if (el.Attributes().Any(a => a.Name.LocalName == "AutomationProperties.Name"
                || a.Name.LocalName == "Content" || a.Name.LocalName == "Label"))
            {
                return true;
            }
            bool appBar = tag.StartsWith("AppBar", StringComparison.Ordinal);
            if (!appBar && el.Elements().Any(c => TextualChildren.Contains(c.Name.LocalName)))
            {
                return true;
            }
            string uid = (string)el.Attribute(X + "Uid");
            if (!string.IsNullOrEmpty(uid)
                && (keys.Contains(uid + ".Content") || keys.Contains(uid + ".Label")
                    || keys.Contains(uid + ".[using:Windows.UI.Xaml.Automation]AutomationProperties.Name")))
            {
                return true;
            }
            string name = (string)el.Attribute(X + "Name");
            if (!string.IsNullOrEmpty(name)
                && (Regex.IsMatch(codeBehind, "\\b" + Regex.Escape(name) + "\\.(Content|Label)\\s*=")
                    || Regex.IsMatch(codeBehind, "SetName\\(\\s*" + Regex.Escape(name) + "\\b")))
            {
                return true;
            }
            return false;
        }

        private static HashSet<string> ReswKeys(string path)
        {
            return new HashSet<string>(
                XDocument.Load(path).Root.Elements("data").Select(d => (string)d.Attribute("name")),
                StringComparer.Ordinal);
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
            throw new DirectoryNotFoundException("src/SshTool.App");
        }
    }
}
