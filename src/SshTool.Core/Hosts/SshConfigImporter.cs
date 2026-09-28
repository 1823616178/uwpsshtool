using System;
using System.Collections.Generic;
using System.Globalization;
using SshTool.Core.Models;

namespace SshTool.Core.Hosts
{
    // W05（01-DESIGN §16.5）：~/.ssh/config 中的一个 Host 块（只取本应用用得上的键）。
    public sealed class SshConfigEntry
    {
        public string Alias { get; set; }
        public bool IsPattern { get; set; }
        public string HostName { get; set; }
        public string User { get; set; }
        public int Port { get; set; }
        public string ProxyJump { get; set; }
        public string IdentityFile { get; set; }
    }

    public sealed class SshConfigImportResult
    {
        public List<Host> Hosts { get; } = new List<Host>();
        // 与现有主机或本次前面的块同名，跳过。
        public List<string> SkippedDuplicates { get; } = new List<string>();
        // 缺 User（同步 schema 要求用户名必填），跳过。
        public List<string> SkippedIncomplete { get; } = new List<string>();
        // 含通配符的 Host 模式（Host * 之类），不是具体主机，跳过。
        public List<string> SkippedPatterns { get; } = new List<string>();
        // 配了 IdentityFile 的别名：私钥不随导入，需用户在「密钥」里自行导入并关联。
        public List<string> NeedsKey { get; } = new List<string>();
    }

    // 纯函数：解析 + 转主机草稿。不读文件、不落库。
    public static class SshConfigImporter
    {
        public static List<SshConfigEntry> Parse(string text)
        {
            var entries = new List<SshConfigEntry>();
            if (string.IsNullOrEmpty(text))
            {
                return entries;
            }
            SshConfigEntry current = null;
            bool inMatch = false;
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string raw in lines)
            {
                string keyword;
                List<string> args;
                if (!TrySplit(raw, out keyword, out args))
                {
                    continue;
                }
                if (keyword == "host")
                {
                    inMatch = false;
                    current = null;
                    if (args.Count == 0)
                    {
                        continue;
                    }
                    current = new SshConfigEntry { Alias = args[0], IsPattern = IsPattern(args[0]) };
                    entries.Add(current);
                    continue;
                }
                if (keyword == "match")
                {
                    inMatch = true;
                    current = null;
                    continue;
                }
                if (current == null || inMatch || args.Count == 0)
                {
                    continue; // 全局选项与 Match 块不导入
                }
                string value = args[0];
                // ssh 语义：同一块内第一次出现的值生效。
                switch (keyword)
                {
                    case "hostname":
                        if (current.HostName == null) { current.HostName = value; }
                        break;
                    case "user":
                        if (current.User == null) { current.User = value; }
                        break;
                    case "port":
                        int port;
                        if (current.Port == 0 && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                            && port >= 1 && port <= 65535)
                        {
                            current.Port = port;
                        }
                        break;
                    case "proxyjump":
                        if (current.ProxyJump == null) { current.ProxyJump = value; }
                        break;
                    case "identityfile":
                        if (current.IdentityFile == null) { current.IdentityFile = value; }
                        break;
                }
            }
            return entries;
        }

        public static SshConfigImportResult ToHosts(IList<SshConfigEntry> entries, IEnumerable<string> existingNames)
        {
            var result = new SshConfigImportResult();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (existingNames != null)
            {
                foreach (string name in existingNames)
                {
                    if (!string.IsNullOrEmpty(name))
                    {
                        names.Add(name);
                    }
                }
            }
            var byAlias = new Dictionary<string, Host>(StringComparer.OrdinalIgnoreCase);
            var jumps = new List<KeyValuePair<Host, string>>();
            if (entries == null)
            {
                return result;
            }
            foreach (SshConfigEntry entry in entries)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.Alias))
                {
                    continue;
                }
                if (entry.IsPattern)
                {
                    result.SkippedPatterns.Add(entry.Alias);
                    continue;
                }
                if (names.Contains(entry.Alias))
                {
                    result.SkippedDuplicates.Add(entry.Alias);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(entry.User))
                {
                    result.SkippedIncomplete.Add(entry.Alias);
                    continue;
                }
                Host host = Defaults.NewHost();
                host.Name = entry.Alias;
                host.HostName = string.IsNullOrWhiteSpace(entry.HostName) ? entry.Alias : entry.HostName;
                host.Username = entry.User;
                host.Port = entry.Port > 0 ? entry.Port : 22;
                names.Add(entry.Alias);
                byAlias[entry.Alias] = host;
                result.Hosts.Add(host);
                if (!string.IsNullOrWhiteSpace(entry.IdentityFile))
                {
                    result.NeedsKey.Add(entry.Alias);
                }
                string jump = FirstHop(entry.ProxyJump);
                if (jump != null)
                {
                    jumps.Add(new KeyValuePair<Host, string>(host, jump));
                }
            }
            // 跳板只解析到同一文件里导入的别名；指向外部地址的 ProxyJump 无法表达为主机引用，忽略。
            foreach (KeyValuePair<Host, string> pair in jumps)
            {
                Host target;
                if (byAlias.TryGetValue(pair.Value, out target) && !ReferenceEquals(target, pair.Key))
                {
                    pair.Key.JumpHostId = target.Id;
                }
            }
            return result;
        }

        private static string FirstHop(string proxyJump)
        {
            if (string.IsNullOrWhiteSpace(proxyJump)
                || string.Equals(proxyJump, "none", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            string hop = proxyJump.Split(',')[0].Trim();
            int at = hop.LastIndexOf('@');
            if (at >= 0)
            {
                hop = hop.Substring(at + 1);
            }
            int colon = hop.LastIndexOf(':');
            if (colon > 0 && hop.IndexOf(':') == colon)
            {
                hop = hop.Substring(0, colon);
            }
            return hop.Length == 0 ? null : hop;
        }

        private static bool IsPattern(string alias)
        {
            return alias.IndexOfAny(new[] { '*', '?', '!' }) >= 0;
        }

        // 「关键字 值…」或「关键字=值」；# 开头为注释；值可用双引号包含空格。
        private static bool TrySplit(string line, out string keyword, out List<string> args)
        {
            keyword = null;
            args = new List<string>();
            string text = line == null ? string.Empty : line.Trim();
            if (text.Length == 0 || text[0] == '#')
            {
                return false;
            }
            int i = 0;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '=')
            {
                i++;
            }
            keyword = text.Substring(0, i).ToLowerInvariant();
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == '='))
            {
                i++;
            }
            while (i < text.Length)
            {
                if (text[i] == '#')
                {
                    break; // 行尾注释
                }
                if (text[i] == '"')
                {
                    int close = text.IndexOf('"', i + 1);
                    if (close < 0)
                    {
                        close = text.Length;
                    }
                    args.Add(text.Substring(i + 1, close - i - 1));
                    i = close + 1;
                }
                else
                {
                    int start = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i]))
                    {
                        i++;
                    }
                    args.Add(text.Substring(start, i - start));
                }
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }
            }
            return keyword.Length > 0;
        }
    }
}
