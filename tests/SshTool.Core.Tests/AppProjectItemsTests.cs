using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SshTool.Core.Tests
{
    // ui/fix-pass：SshTool.App 是旧式 UWP csproj，源文件逐个登记；新加的 .cs / .xaml
    // 忘了登记，Linux CI 毫无察觉，到 Windows 上才编不过（或 XAML 资源字典静默缺失）。
    public class AppProjectItemsTests
    {
        private static readonly Regex Item = new Regex(
            "<(?:Compile|Page|ApplicationDefinition)\\s+Include=\"([^\"]+)\"", RegexOptions.Compiled);

        [Fact]
        public void EverySourceFile_IsListedInCsproj()
        {
            string app = AppDir();
            string csproj = File.ReadAllText(Path.Combine(app, "SshTool.App.csproj"));
            var listed = new HashSet<string>(
                Item.Matches(csproj).Cast<Match>().Select(m => m.Groups[1].Value.Replace('\\', '/')),
                StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            foreach (string ext in new[] { "*.cs", "*.xaml" })
            {
                foreach (string file in Directory.GetFiles(app, ext, SearchOption.AllDirectories))
                {
                    string rel = file.Substring(app.Length + 1).Replace('\\', '/');
                    if (rel.StartsWith("obj/", StringComparison.Ordinal) || rel.StartsWith("bin/", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (!listed.Contains(rel))
                    {
                        missing.Add(rel);
                    }
                }
            }
            Assert.True(missing.Count == 0, "以下文件未登记到 SshTool.App.csproj：\n" + string.Join("\n", missing));
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
