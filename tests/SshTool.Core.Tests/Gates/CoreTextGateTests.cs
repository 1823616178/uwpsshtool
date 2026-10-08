using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace SshTool.Core.Tests.Gates
{
    // fix/functional-pass（P2-2）：Core 面向用户的文案改为返回码，由 App 查 resw。
    // 本门禁扫描 src/SshTool.Core 全部 .cs 的字符串字面量，统计「非诊断」CJK 字面量
    // （诊断 = 位于 throw / 异常构造 / ApiError( / 日志调用所在语句内，不直接展示给用户）。
    //  - 不在 Baseline 里的文件必须为 0（新代码不得把中文写进 Core 的 UI 路径）；
    //  - Baseline 里的文件只能减少（棘轮）：减少后请同步下调这里的数字。
    public class CoreTextGateTests
    {
        // 历史遗留（主题导入解析错误、配置警告等），后续逐步改为错误码。
        private static readonly Dictionary<string, int> Baseline = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "Appearance/ItermcolorsParser.cs", 4 },
            { "Appearance/SampleScreenBuilder.cs", 3 },
            { "Appearance/ThemeImportResult.cs", 2 },
            { "Appearance/WindowsTerminalSchemeParser.cs", 14 },
            { "Common/AppConfigParser.cs", 6 },
            { "Forwarding/Socks5Parser.cs", 5 },
            { "Hosts/HostEditState.cs", 1 },
            { "Hosts/HostListBuilder.cs", 3 },
            { "Keys/KeyImportService.cs", 1 },
            { "Lifecycle/BackgroundPolicy.cs", 3 },
            { "Models/Defaults.cs", 1 },
            { "Spikes/JsonSpike.cs", 6 },
            { "Storage/JsonStore.cs", 7 },
            { "Sync/Api/Dtos/DtoReader.cs", 1 },
        };

        private static readonly Regex Cjk = new Regex("[\u3000-\u303f\u4e00-\u9fff\uff00-\uffef]");

        private static readonly Regex Diagnostic = new Regex(
            @"\bthrow\b|Exception\s*\(|ApiError\s*\(|\b(Info|Warn|Warning|Debug|Trace|Log[A-Za-z]*)\s*\(|_log[A-Za-z]*\.|Logger\.|ResponseInvalid\s*\(");

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "src", "SshTool.Core", "SshTool.Core.csproj")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("repo root not found");
        }

        private static IEnumerable<string> SourceFiles(string root)
        {
            return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(p =>
                {
                    string rel = p.Substring(root.Length).Replace('\\', '/');
                    return !rel.Contains("/obj/") && !rel.Contains("/bin/");
                });
        }

        [Fact]
        public void CoreCjkLiterals_DoNotGrow()
        {
            string coreRoot = Path.Combine(RepoRoot(), "src", "SshTool.Core");
            var failures = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in SourceFiles(coreRoot))
            {
                string rel = path.Substring(coreRoot.Length + 1).Replace('\\', '/');
                List<string> found = NonDiagnosticCjkLiterals(File.ReadAllText(path, Encoding.UTF8));
                int allowed;
                Baseline.TryGetValue(rel, out allowed);
                if (found.Count > allowed)
                {
                    failures.Add(rel + " " + found.Count + " > " + allowed + ": " + string.Join(" | ", found));
                }
                else if (found.Count < allowed)
                {
                    // 棘轮：已经减少的文件要把基线一起调低，防止回退。
                    failures.Add(rel + " " + found.Count + " < baseline " + allowed + "（请下调 Baseline）");
                }
                seen.Add(rel);
            }
            foreach (string stale in Baseline.Keys.Where(k => !seen.Contains(k)))
            {
                failures.Add(stale + " 已不存在（请从 Baseline 删除）");
            }
            Assert.True(failures.Count == 0, "Core 新增了面向用户的中文字面量（请改为码 + App resw）：\n" + string.Join("\n", failures));
        }

        [Fact]
        public void Scanner_SkipsCommentsAndDiagnostics()
        {
            string src = "// 注释 \"中文\"\n/* \"中文\" */\nthrow new X(\n  \"中文\");\nLog(\"中文\");\nvar a = \"中文\";\nvar b = @\"多\"\"行\";\nvar c = \"en\";";
            List<string> found = NonDiagnosticCjkLiterals(src);
            Assert.Equal(new[] { "中文", "多\"\"行" }, found.ToArray());
        }

        // App 里 ResourceLoader.GetString 的键要用 '/' 分隔属性（"X/Text"），写成 "X.Text" 会取到空串。
        [Fact]
        public void AppResourceKeys_DoNotUseDottedPropertyNames()
        {
            string appRoot = Path.Combine(RepoRoot(), "src", "SshTool.App");
            var dotted = new Regex("GetString\\(\"[A-Za-z0-9_]+\\.[A-Za-z0-9_.]+\"\\)");
            var failures = new List<string>();
            foreach (string path in SourceFiles(appRoot))
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                foreach (Match m in dotted.Matches(text))
                {
                    failures.Add(Path.GetFileName(path) + ": " + m.Value);
                }
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        internal static List<string> NonDiagnosticCjkLiterals(string src)
        {
            var literals = new List<KeyValuePair<int, string>>();
            var code = new StringBuilder(src.Length);
            int i = 0;
            int n = src.Length;
            while (i < n)
            {
                char c = src[i];
                if (c == '/' && i + 1 < n && src[i + 1] == '/')
                {
                    int j = src.IndexOf('\n', i);
                    if (j < 0)
                    {
                        j = n;
                    }
                    code.Append(' ', j - i);
                    i = j;
                    continue;
                }
                if (c == '/' && i + 1 < n && src[i + 1] == '*')
                {
                    int j = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    j = j < 0 ? n : j + 2;
                    code.Append(' ', j - i);
                    i = j;
                    continue;
                }
                bool verbatim = (c == '@' && i + 1 < n && src[i + 1] == '"')
                    || (c == '$' && i + 2 < n && src[i + 1] == '@' && src[i + 2] == '"')
                    || (c == '@' && i + 2 < n && src[i + 1] == '$' && src[i + 2] == '"');
                bool regular = c == '"' || (c == '$' && i + 1 < n && src[i + 1] == '"');
                if (verbatim || regular)
                {
                    int k = src.IndexOf('"', i) + 1;
                    int j = k;
                    while (j < n)
                    {
                        if (verbatim)
                        {
                            if (src[j] == '"')
                            {
                                if (j + 1 < n && src[j + 1] == '"')
                                {
                                    j += 2;
                                    continue;
                                }
                                break;
                            }
                        }
                        else
                        {
                            if (src[j] == '\\')
                            {
                                j += 2;
                                continue;
                            }
                            if (src[j] == '"' || src[j] == '\n')
                            {
                                break;
                            }
                        }
                        j++;
                    }
                    if (j > n)
                    {
                        j = n;
                    }
                    literals.Add(new KeyValuePair<int, string>(i, src.Substring(k, j - k)));
                    code.Append('"');
                    code.Append(' ', Math.Min(n, j + 1) - i - 1);
                    i = j + 1;
                    continue;
                }
                if (c == '\'')
                {
                    int j = i + 1;
                    while (j < n && src[j] != '\'' && src[j] != '\n')
                    {
                        if (src[j] == '\\')
                        {
                            j++;
                        }
                        j++;
                    }
                    if (j > n)
                    {
                        j = n;
                    }
                    code.Append(' ', Math.Min(n, j + 1) - i);
                    i = j + 1;
                    continue;
                }
                code.Append(c);
                i++;
            }
            string stripped = code.ToString();
            var result = new List<string>();
            foreach (var lit in literals)
            {
                if (!Cjk.IsMatch(lit.Value))
                {
                    continue;
                }
                int k = Math.Min(lit.Key, stripped.Length) - 1;
                while (k >= 0 && stripped[k] != ';' && stripped[k] != '{' && stripped[k] != '}')
                {
                    k--;
                }
                string statement = stripped.Substring(k + 1, Math.Min(lit.Key, stripped.Length) - k - 1);
                if (!Diagnostic.IsMatch(statement))
                {
                    result.Add(lit.Value);
                }
            }
            return result;
        }
    }
}
