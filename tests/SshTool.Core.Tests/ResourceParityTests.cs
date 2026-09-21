using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace SshTool.Core.Tests
{
    // Q04：两份 resw 键集合完全一致；每个键的 en-US 值非空；en-US 值不含中文字符。
    public class ResourceParityTests
    {
        private static readonly Regex ChineseChar = new Regex(@"[\u4e00-\u9fff]", RegexOptions.Compiled);

        [Fact]
        public void ZhCnAndEnUs_KeySetsAreIdentical()
        {
            var zh = KeySet(FindResw("zh-CN"));
            var en = KeySet(FindResw("en-US"));
            var missingInEn = zh.Except(en, StringComparer.Ordinal).ToList();
            var missingInZh = en.Except(zh, StringComparer.Ordinal).ToList();
            Assert.True(missingInEn.Count == 0,
                "en-US 缺少以下键：" + string.Join(", ", missingInEn));
            Assert.True(missingInZh.Count == 0,
                "zh-CN 缺少以下键：" + string.Join(", ", missingInZh));
        }

        [Theory]
        [InlineData("en-US")]
        [InlineData("zh-CN")]
        public void EveryKey_ValueIsNonEmpty(string lang)
        {
            var values = ReadValues(FindResw(lang));
            var empty = values.Where(kvp => string.IsNullOrWhiteSpace(kvp.Value))
                              .Select(kvp => kvp.Key).ToList();
            Assert.True(empty.Count == 0,
                $"{lang} 以下键文案为空：" + string.Join(", ", empty));
        }

        [Fact]
        public void EnUsValues_ContainNoChineseCharacters()
        {
            var values = ReadValues(FindResw("en-US"));
            var bad = values.Where(kvp => ChineseChar.IsMatch(kvp.Value))
                            .Select(kvp => kvp.Key).ToList();
            Assert.True(bad.Count == 0,
                "en-US 以下键仍含中文：" + string.Join(", ", bad));
        }

        private static SortedSet<string> KeySet(string path)
        {
            var doc = XDocument.Load(path);
            return new SortedSet<string>(doc.Root.Elements("data")
                .Where(e => e.Attribute("name") != null)
                .Select(e => e.Attribute("name").Value), StringComparer.Ordinal);
        }

        private static Dictionary<string, string> ReadValues(string path)
        {
            var doc = XDocument.Load(path);
            return doc.Root.Elements("data")
                .Where(e => e.Attribute("name") != null)
                .ToDictionary(
                    e => e.Attribute("name").Value,
                    e => (string)e.Element("value") ?? string.Empty,
                    StringComparer.Ordinal);
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
