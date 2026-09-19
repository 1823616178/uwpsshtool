using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SshTool.Core.Appearance;
using SshTool.Core.Models;
using Xunit;

namespace SshTool.Core.Tests.Appearance
{
    // A04 验收：两种格式各 ≥3 真实主题解析成功；非法 XML/JSON/缺键报错且不抛异常。
    // 夹具 tests/fixtures/themes/*：3 个 .itermcolors（Dracula/Solarized Dark/Nord
    // 官方配色）+ 2 个单 scheme JSON + 1 个含 3 scheme 的 settings.json。
    public class ThemeImportTests
    {
        private static readonly Regex ColorRegex = new Regex("^#[0-9A-F]{6}$");

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tests", "fixtures", "sync", "desktop-vectors.json")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            throw new FileNotFoundException("找不到仓库根（tests/fixtures/sync/desktop-vectors.json 缺失）");
        }

        private static string Fixture(string name)
        {
            string path = Path.Combine(RepoRoot(), "tests", "fixtures", "themes", name);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("找不到夹具 " + name);
            }
            return File.ReadAllText(path, new UTF8Encoding(false));
        }

        public static IEnumerable<object[]> ItermcolorsFiles()
        {
            yield return new object[]
            {
                "dracula.itermcolors",
                new string[]
                {
                    "#21222C", "#FF5555", "#50FA7B", "#F1FA8C",
                    "#BD93F9", "#FF79C6", "#8BE9FD", "#F8F8F2",
                    "#6272A4", "#FF6E6E", "#69FF94", "#FFFFA5",
                    "#D6ACFF", "#FF92DF", "#A4FFFF", "#FFFFFF"
                },
                "#F8F8F2", "#282A36", "#F8F8F2", "#44475A"
            };
            yield return new object[]
            {
                "solarized-dark.itermcolors",
                new string[]
                {
                    "#073642", "#DC322F", "#859900", "#B58900",
                    "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
                    "#002B36", "#CB4B16", "#586E75", "#657B83",
                    "#839496", "#6C71C4", "#93A1A1", "#FDF6E3"
                },
                "#839496", "#002B36", "#93A1A1", "#073642"
            };
            yield return new object[]
            {
                "nord.itermcolors",
                new string[]
                {
                    "#3B4252", "#BF616A", "#A3BE8C", "#EBCB8B",
                    "#81A1C1", "#B48EAD", "#88C0D0", "#E5E9F0",
                    "#4C566A", "#BF616A", "#A3BE8C", "#EBCB8B",
                    "#81A1C1", "#B48EAD", "#8FBCBB", "#ECEFF4"
                },
                "#D8DEE9", "#2E3440", "#D8DEE9", "#434C5E"
            };
        }

        [Theory]
        [MemberData(nameof(ItermcolorsFiles))]
        public void Itermcolors_RealTheme_ParsesAll20Slots(
            string file, string[] palette, string fg, string bg, string cursor, string selection)
        {
            ThemeImportResult result = ItermcolorsParser.Parse(Fixture(file), Path.GetFileNameWithoutExtension(file));
            Assert.True(result.Ok, result.Error);
            Assert.Single(result.Profiles);
            AppearanceProfile p = result.Profiles[0];
            Assert.Equal(16, p.Palette.Count);
            for (int i = 0; i < 16; i++)
            {
                Assert.Equal(palette[i], p.Palette[i]);
            }
            Assert.Equal(fg, p.Foreground);
            Assert.Equal(bg, p.Background);
            Assert.Equal(cursor, p.Cursor);
            Assert.Equal(selection, p.Selection);
            AssertImportedDefaults(p, Path.GetFileNameWithoutExtension(file));
        }

        public static IEnumerable<object[]> SingleSchemeFiles()
        {
            yield return new object[]
            {
                "dracula.json", "Dracula",
                new string[]
                {
                    "#21222C", "#FF5555", "#50FA7B", "#F1FA8C",
                    "#BD93F9", "#FF79C6", "#8BE9FD", "#F8F8F2",
                    "#6272A4", "#FF6E6E", "#69FF94", "#FFFFA5",
                    "#D6ACFF", "#FF92DF", "#A4FFFF", "#FFFFFF"
                },
                "#F8F8F2", "#282A36", "#F8F8F2", "#44475A"
            };
            yield return new object[]
            {
                "tokyo-night.json", "Tokyo Night",
                new string[]
                {
                    "#15161E", "#F7768E", "#9ECE6A", "#E0AF68",
                    "#7AA2F7", "#BB9AF7", "#7DCFFF", "#A9B1D6",
                    "#414868", "#F7768E", "#9ECE6A", "#E0AF68",
                    "#7AA2F7", "#BB9AF7", "#7DCFFF", "#C0CAF5"
                },
                "#C0CAF5", "#1A1B26", "#C0CAF5", "#283457"
            };
        }

        [Theory]
        [MemberData(nameof(SingleSchemeFiles))]
        public void WindowsTerminal_SingleScheme_ParsesAll20Slots(
            string file, string name, string[] palette, string fg, string bg, string cursor, string selection)
        {
            ThemeImportResult result = WindowsTerminalSchemeParser.Parse(Fixture(file), "fallback");
            Assert.True(result.Ok, result.Error);
            Assert.Single(result.Profiles);
            AppearanceProfile p = result.Profiles[0];
            Assert.Equal(name, p.Name);
            Assert.Equal(16, p.Palette.Count);
            for (int i = 0; i < 16; i++)
            {
                Assert.Equal(palette[i], p.Palette[i]);
            }
            Assert.Equal(fg, p.Foreground);
            Assert.Equal(bg, p.Background);
            Assert.Equal(cursor, p.Cursor);
            Assert.Equal(selection, p.Selection);
            AssertImportedDefaults(p, name);
        }

        [Fact]
        public void WindowsTerminal_SettingsJson_ReturnsAllThreeSchemes()
        {
            ThemeImportResult result = WindowsTerminalSchemeParser.Parse(
                Fixture("settings-with-schemes.json"), "fallback");
            Assert.True(result.Ok, result.Error);
            Assert.Equal(3, result.Profiles.Count);
            Assert.Equal("One Dark", result.Profiles[0].Name);
            Assert.Equal("Solarized Dark", result.Profiles[1].Name);
            Assert.Equal("Nord", result.Profiles[2].Name);
            Assert.Equal("#E06C75", result.Profiles[0].Palette[1]);
            Assert.Equal("#282C34", result.Profiles[0].Background);
            Assert.Equal("#859900", result.Profiles[1].Palette[2]);
            Assert.Equal("#839496", result.Profiles[1].Foreground);
            Assert.Equal("#88C0D0", result.Profiles[2].Palette[6]);
            Assert.Equal("#434C5E", result.Profiles[2].Selection);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (AppearanceProfile p in result.Profiles)
            {
                AssertImportedDefaults(p, p.Name);
                Assert.True(ids.Add(p.Id), "导入 id 重复");
            }
        }

        [Fact]
        public void WindowsTerminal_ShortHexAndLowerCase_NormalizedToUpper()
        {
            string json = "{ \"name\": \"t\", "
                + "\"black\": \"#000\", \"red\": \"#f00\", \"green\": \"#0f0\", \"yellow\": \"#ff0\", "
                + "\"blue\": \"#00f\", \"purple\": \"#f0f\", \"cyan\": \"#0ff\", \"white\": \"#fff\", "
                + "\"brightBlack\": \"#111\", \"brightRed\": \"#f00\", \"brightGreen\": \"#0f0\", "
                + "\"brightYellow\": \"#ff0\", \"brightBlue\": \"#00f\", \"brightPurple\": \"#f0f\", "
                + "\"brightCyan\": \"#0ff\", \"brightWhite\": \"#abc\", "
                + "\"foreground\": \"#def\", \"background\": \"#123456\", "
                + "\"cursorColor\": \"#123456\", \"selectionBackground\": \"#654321\" }";
            ThemeImportResult result = WindowsTerminalSchemeParser.Parse(json, "fallback");
            Assert.True(result.Ok, result.Error);
            AppearanceProfile p = Assert.Single(result.Profiles);
            Assert.Equal("#000000", p.Palette[0]);
            Assert.Equal("#FF0000", p.Palette[1]);
            Assert.Equal("#AABBCC", p.Palette[15]);
            Assert.Equal("#DDEEFF", p.Foreground);
        }

        [Fact]
        public void WindowsTerminal_DuplicateNames_GetNumericSuffix()
        {
            string one = Fixture("dracula.json");
            string json = "{ \"schemes\": [ " + one + ", " + one + " ] }";
            ThemeImportResult result = WindowsTerminalSchemeParser.Parse(json, "fallback");
            Assert.True(result.Ok, result.Error);
            Assert.Equal(2, result.Profiles.Count);
            Assert.Equal("Dracula", result.Profiles[0].Name);
            Assert.Equal("Dracula 2", result.Profiles[1].Name);
            Assert.NotEqual(result.Profiles[0].Id, result.Profiles[1].Id);
        }

        [Fact]
        public void WindowsTerminal_SchemeWithoutName_UsesSuggestedName()
        {
            string json = Fixture("dracula.json").Replace("\"name\": \"Dracula\",", string.Empty);
            ThemeImportResult named = WindowsTerminalSchemeParser.Parse(json, "我的主题");
            Assert.True(named.Ok, named.Error);
            Assert.Equal("我的主题", Assert.Single(named.Profiles).Name);

            ThemeImportResult unnamed = WindowsTerminalSchemeParser.Parse(json, null);
            Assert.True(unnamed.Ok, unnamed.Error);
            Assert.Equal("导入配色", Assert.Single(unnamed.Profiles).Name);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Itermcolors_NullOrEmpty_FailsWithoutThrowing(string text)
        {
            ThemeImportResult result = ItermcolorsParser.Parse(text, "x");
            Assert.False(result.Ok);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void WindowsTerminal_NullOrEmpty_FailsWithoutThrowing(string text)
        {
            ThemeImportResult result = WindowsTerminalSchemeParser.Parse(text, "x");
            Assert.False(result.Ok);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
        }

        [Theory]
        [InlineData("<not xml")]
        [InlineData("<plist><dict><key>Ansi 0 Color</key></dict></plist>")]
        [InlineData("\0\0{{{")]
        public void Itermcolors_MalformedXml_FailsWithoutThrowing(string text)
        {
            ThemeImportResult result = ItermcolorsParser.Parse(text, "x");
            Assert.False(result.Ok);
            Assert.Contains(".itermcolors", result.Error);
        }

        [Fact]
        public void Itermcolors_MissingAnsiKey_NamesTheKey()
        {
            string text = Fixture("dracula.itermcolors")
                .Replace("<key>Ansi 5 Color</key>", "<key>Ansi 5 Color MISSING</key>");
            ThemeImportResult result = ItermcolorsParser.Parse(text, "x");
            Assert.False(result.Ok);
            Assert.Contains("Ansi 5 Color", result.Error);
        }

        [Fact]
        public void Itermcolors_MissingForegroundKey_NamesTheKey()
        {
            string text = Fixture("dracula.itermcolors")
                .Replace("<key>Foreground Color</key>", "<key>Foreground Color MISSING</key>");
            ThemeImportResult result = ItermcolorsParser.Parse(text, "x");
            Assert.False(result.Ok);
            Assert.Contains("Foreground Color", result.Error);
        }

        [Fact]
        public void Itermcolors_ComponentOutOfRange_FailsWithoutThrowing()
        {
            string text = Fixture("dracula.itermcolors")
                .Replace("<real>0.12941176470588237</real>", "<real>1.5</real>");
            ThemeImportResult result = ItermcolorsParser.Parse(text, "x");
            Assert.False(result.Ok);
            Assert.Contains("0–1", result.Error);
        }

        [Theory]
        [InlineData("{oops")]
        [InlineData("[]")]
        [InlineData("\0\0{{{")]
        public void WindowsTerminal_MalformedJson_FailsWithoutThrowing(string text)
        {
            ThemeImportResult result = WindowsTerminalSchemeParser.Parse(text, "x");
            Assert.False(result.Ok);
            Assert.Contains("JSON", result.Error);
        }

        [Fact]
        public void WindowsTerminal_EmptyObject_NeitherSchemeNorSchemes()
        {
            ThemeImportResult result = WindowsTerminalSchemeParser.Parse("{}", "x");
            Assert.False(result.Ok);
            Assert.Contains("schemes", result.Error);
        }

        [Fact]
        public void WindowsTerminal_MissingPaletteKey_NamesTheKey()
        {
            string text = Fixture("dracula.json").Replace("\"green\": \"#50FA7B\",", string.Empty);
            ThemeImportResult result = WindowsTerminalSchemeParser.Parse(text, "x");
            Assert.False(result.Ok);
            Assert.Contains("green", result.Error);
        }

        [Fact]
        public void WindowsTerminal_BadColorValue_NamesTheKey()
        {
            string text = Fixture("dracula.json").Replace("\"red\": \"#FF5555\"", "\"red\": \"red\"");
            ThemeImportResult result = WindowsTerminalSchemeParser.Parse(text, "x");
            Assert.False(result.Ok);
            Assert.Contains("red", result.Error);
        }

        [Fact]
        public void WindowsTerminal_EmptySchemesArray_FailsWithoutThrowing()
        {
            ThemeImportResult result = WindowsTerminalSchemeParser.Parse("{ \"schemes\": [] }", "x");
            Assert.False(result.Ok);
            Assert.Contains("为空", result.Error);
        }

        [Fact]
        public void WindowsTerminal_NonObjectSchemeItem_FailsWithoutThrowing()
        {
            ThemeImportResult result = WindowsTerminalSchemeParser.Parse("{ \"schemes\": [ 1 ] }", "x");
            Assert.False(result.Ok);
            Assert.Contains("不是对象", result.Error);
        }

        [Fact]
        public void WindowsTerminal_BadSchemeInArray_NamesTheIndex()
        {
            string bad = "{ \"name\": \"坏的\", \"black\": \"#000000\" }";
            string json = "{ \"schemes\": [ " + Fixture("dracula.json") + ", " + bad + " ] }";
            ThemeImportResult result = WindowsTerminalSchemeParser.Parse(json, "x");
            Assert.False(result.Ok);
            Assert.Contains("第 2 项", result.Error);
        }

        private static void AssertImportedDefaults(AppearanceProfile p, string expectedName)
        {
            Assert.Equal(expectedName, p.Name);
            Assert.False(p.BuiltIn);
            Assert.False(string.IsNullOrWhiteSpace(p.Id));
            Assert.Equal("JetBrains Mono", p.FontFamily);
            Assert.Equal(12, p.FontSize);
            Assert.Equal(1.2, p.LineHeight);
            Assert.True(p.BoldAsBright);
            Assert.Equal(4, p.Padding);
            Assert.Equal(16, p.Palette.Count);
            var slots = new List<string>(p.Palette);
            slots.Add(p.Foreground);
            slots.Add(p.Background);
            slots.Add(p.Cursor);
            slots.Add(p.Selection);
            Assert.Equal(20, slots.Count);
            foreach (string c in slots)
            {
                Assert.Matches(ColorRegex, c);
            }
        }
    }
}
