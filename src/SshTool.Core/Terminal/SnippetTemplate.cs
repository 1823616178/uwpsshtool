using System.Collections.Generic;
using System.Text;

namespace SshTool.Core.Terminal
{
    // U13 命令片段模板（02-UI-DESIGN.md §5.9）。
    //
    // 语法：
    //   ${host} ${user} ${port} ${name}  内置变量（大小写敏感），由当前会话填充；
    //   ${xxx}                           其他一律视为待填变量，由 SnippetVariableDialog 收集；
    //   $$                               转义为单个 $（于是 $${host} 渲染为字面 ${host}）；
    //   ${} / 未闭合的 ${               按字面保留，不视为变量。
    //
    // 本文件只放纯函数：解析、变量收集、渲染、发送文本整形。
    // 分组、弹窗、ISshSession.Write 接线在 App 层（ViewModels/Snippets）。
    public sealed class SnippetBuiltin
    {
        public string Host { get; set; }
        public string User { get; set; }
        public string Port { get; set; }
        public string Name { get; set; }

        public SnippetBuiltin()
        {
            Host = string.Empty;
            User = string.Empty;
            Port = string.Empty;
            Name = string.Empty;
        }
    }

    public static class SnippetTemplate
    {
        public const string VarHost = "host";
        public const string VarUser = "user";
        public const string VarPort = "port";
        public const string VarName = "name";

        // 内置变量名是否命中（Ordinal 大小写敏感；${Host} 视为待填变量）。
        public static bool IsBuiltIn(string name)
        {
            return string.Equals(name, VarHost, System.StringComparison.Ordinal)
                || string.Equals(name, VarUser, System.StringComparison.Ordinal)
                || string.Equals(name, VarPort, System.StringComparison.Ordinal)
                || string.Equals(name, VarName, System.StringComparison.Ordinal);
        }

        // 收集待填变量：去重、保持首次出现顺序、排除内置变量与转义。
        // null/空模板返回空列表（不返回 null）。
        public static IReadOnlyList<string> CollectVariables(string template)
        {
            var names = new List<string>();
            if (string.IsNullOrEmpty(template))
            {
                return names;
            }
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            int i = 0;
            while (i < template.Length)
            {
                string name;
                int next;
                if (TryReadVariable(template, i, out name, out next))
                {
                    if (!IsBuiltIn(name) && seen.Add(name))
                    {
                        names.Add(name);
                    }
                    i = next;
                }
                else if (template[i] == '$'
                    && i + 1 < template.Length
                    && template[i + 1] == '$')
                {
                    i += 2;
                }
                else
                {
                    i++;
                }
            }
            return names;
        }

        // 通用渲染：values 缺 key 或 value 为 null 时填空字符串。
        // null 模板视为空字符串；values 为 null 视为全空。
        public static string Render(string template, IDictionary<string, string> values)
        {
            if (string.IsNullOrEmpty(template))
            {
                return string.Empty;
            }
            var sb = new StringBuilder(template.Length);
            int i = 0;
            while (i < template.Length)
            {
                if (template[i] == '$'
                    && i + 1 < template.Length
                    && template[i + 1] == '$')
                {
                    sb.Append('$');
                    i += 2;
                    continue;
                }
                string name;
                int next;
                if (TryReadVariable(template, i, out name, out next))
                {
                    string value = null;
                    if (values != null)
                    {
                        values.TryGetValue(name, out value);
                    }
                    sb.Append(value ?? string.Empty);
                    i = next;
                    continue;
                }
                sb.Append(template[i]);
                i++;
            }
            return sb.ToString();
        }

        // 内置 + 待填合并渲染：内置优先（extra 不得覆盖内置）。
        public static string RenderWithBuiltin(
            string template, SnippetBuiltin builtin, IDictionary<string, string> extra)
        {
            var merged = new Dictionary<string, string>(System.StringComparer.Ordinal);
            if (extra != null)
            {
                foreach (KeyValuePair<string, string> pair in extra)
                {
                    if (pair.Key != null && !merged.ContainsKey(pair.Key))
                    {
                        merged[pair.Key] = pair.Value;
                    }
                }
            }
            if (builtin != null)
            {
                merged[VarHost] = builtin.Host ?? string.Empty;
                merged[VarUser] = builtin.User ?? string.Empty;
                merged[VarPort] = builtin.Port ?? string.Empty;
                merged[VarName] = builtin.Name ?? string.Empty;
            }
            return Render(template, merged);
        }

        // 发送整形：换行归一为 \r（终端回车即 0x0D，见 KeyMap.Enter）；
        // sendEnter 且末尾不是 \r 时追加一个 \r。
        public static string PrepareSendText(string rendered, bool sendEnter)
        {
            if (string.IsNullOrEmpty(rendered))
            {
                return sendEnter ? "\r" : string.Empty;
            }
            var sb = new StringBuilder(rendered.Length + 1);
            for (int i = 0; i < rendered.Length; i++)
            {
                char c = rendered[i];
                if (c == '\r')
                {
                    sb.Append('\r');
                    if (i + 1 < rendered.Length && rendered[i + 1] == '\n')
                    {
                        i++;
                    }
                    continue;
                }
                if (c == '\n')
                {
                    sb.Append('\r');
                    continue;
                }
                sb.Append(c);
            }
            if (sendEnter && (sb.Length == 0 || sb[sb.Length - 1] != '\r'))
            {
                sb.Append('\r');
            }
            return sb.ToString();
        }

        // 在 pos 处尝试读一个 ${name}；成功返回 true 并给出变量名（已 Trim）与
        // 紧随 } 的位置。${}、${空白}、未闭合一律返回 false（调用方按字面处理）。
        private static bool TryReadVariable(string template, int pos, out string name, out int next)
        {
            name = null;
            next = pos;
            if (template == null || pos < 0 || pos + 2 >= template.Length)
            {
                return false;
            }
            if (template[pos] != '$' || template[pos + 1] != '{')
            {
                return false;
            }
            int end = template.IndexOf('}', pos + 2);
            if (end < 0)
            {
                return false;
            }
            string inner = template.Substring(pos + 2, end - (pos + 2));
            string trimmed = inner.Trim();
            if (trimmed.Length == 0)
            {
                return false;
            }
            name = trimmed;
            next = end + 1;
            return true;
        }
    }
}
