using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SshTool.Core.Common;
using Xunit;

namespace SshTool.Core.Tests.Common
{
    // X04 验收：每个 SshErrorCode 值在两个 resw 中都有 Error_<数值> 键且文案非空。
    public class ErrorCodeResourceTests
    {
        public static IEnumerable<object[]> ReswFiles()
        {
            yield return new object[] { "zh-CN" };
            yield return new object[] { "en-US" };
        }

        [Theory]
        [MemberData(nameof(ReswFiles))]
        public void EveryErrorCode_HasEntry(string lang)
        {
            string path = FindResw(lang);
            var doc = XDocument.Load(path);
            var names = doc.Root.Elements("data")
                .Where(e => e.Attribute("name") != null)
                .ToDictionary(e => e.Attribute("name").Value, e => (string)e.Element("value"));

            foreach (SshErrorCode code in Enum.GetValues(typeof(SshErrorCode)))
            {
                string key = "Error_" + (int)code;
                Assert.True(names.ContainsKey(key), $"{lang} 缺少 {key}（{path}）");
                Assert.False(string.IsNullOrWhiteSpace(names[key]), $"{lang} 的 {key} 文案为空");
            }
        }

        [Fact]
        public void BothLanguages_SameKeys()
        {
            var zh = KeySet(FindResw("zh-CN"));
            var en = KeySet(FindResw("en-US"));
            Assert.Equal(zh, en);
        }

        private static SortedSet<string> KeySet(string path)
        {
            var doc = XDocument.Load(path);
            return new SortedSet<string>(doc.Root.Elements("data")
                .Where(e => e.Attribute("name") != null)
                .Select(e => e.Attribute("name").Value), StringComparer.Ordinal);
        }

        // 相对仓库根：从测试输出目录向上找，直到出现 src/SshTool.App
        private static string FindResw(string lang)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "src", "SshTool.App", "Strings", lang, "Resources.resw");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
            throw new FileNotFoundException("找不到 " + lang + " Resources.resw（从 " + AppContext.BaseDirectory + " 向上搜索）");
        }
    }
}
