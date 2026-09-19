using System;
using System.Collections.Generic;
using SshTool.Core.Models;

namespace SshTool.App.ViewModels.Snippets
{
    // U13：片段行 / 分组行（SnippetsPage 与 SnippetPickerFlyout 共用）。
    public sealed class SnippetRowVm
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Preview { get; set; }
        public string GroupName { get; set; }
        public bool SendEnter { get; set; }
        // 管理页行内发送按钮是否可用（有会话时 true，无会话时禁用）。
        public bool SendEnabled { get; set; }
        public Snippet Source { get; set; }
    }

    public sealed class SnippetGroupVm
    {
        public SnippetGroupVm()
        {
            Rows = new List<SnippetRowVm>();
        }

        public string GroupName { get; set; }
        public string Title { get; set; }
        public List<SnippetRowVm> Rows { get; private set; }
    }

    // 分组 + 搜索纯逻辑：GroupName 为空/空白归入「未分组」；
    // 分组按名称排序（未分组永远在最后），组内按 SortOrder 再按名称。
    public static class SnippetGrouping
    {
        public const string UngroupedTitle = "未分组";

        public static string DisplayName(string groupName)
        {
            if (string.IsNullOrWhiteSpace(groupName))
            {
                return UngroupedTitle;
            }
            return groupName.Trim();
        }

        public static bool IsUngrouped(string groupName)
        {
            return string.IsNullOrWhiteSpace(groupName);
        }

        public static List<SnippetGroupVm> Group(
            IReadOnlyList<Snippet> items, string search)
        {
            string q = search == null ? string.Empty : search.Trim();
            var byGroup = new Dictionary<string, SnippetGroupVm>(StringComparer.Ordinal);
            var order = new List<string>();
            if (items != null)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    Snippet s = items[i];
                    if (s == null)
                    {
                        continue;
                    }
                    if (q.Length > 0
                        && !Contains(s.Name, q)
                        && !Contains(s.Content, q)
                        && !Contains(s.GroupName, q))
                    {
                        continue;
                    }
                    string key = IsUngrouped(s.GroupName)
                        ? string.Empty
                        : s.GroupName.Trim();
                    SnippetGroupVm group;
                    if (!byGroup.TryGetValue(key, out group))
                    {
                        group = new SnippetGroupVm
                        {
                            GroupName = key,
                            Title = DisplayName(key)
                        };
                        byGroup[key] = group;
                        order.Add(key);
                    }
                    group.Rows.Add(ToRow(s));
                }
            }
            order.Sort((a, b) =>
            {
                bool ae = a.Length == 0;
                bool be = b.Length == 0;
                if (ae != be)
                {
                    return ae ? 1 : -1;
                }
                return string.Compare(a, b, StringComparison.Ordinal);
            });
            var result = new List<SnippetGroupVm>(order.Count);
            for (int i = 0; i < order.Count; i++)
            {
                SnippetGroupVm group = byGroup[order[i]];
                group.Rows.Sort((a, b) =>
                {
                    int ao = a.Source == null ? 0 : a.Source.SortOrder;
                    int bo = b.Source == null ? 0 : b.Source.SortOrder;
                    if (ao != bo)
                    {
                        return ao.CompareTo(bo);
                    }
                    return string.Compare(
                        a.Name ?? string.Empty, b.Name ?? string.Empty,
                        StringComparison.Ordinal);
                });
                result.Add(group);
            }
            return result;
        }

        public static List<string> ExistingGroups(IReadOnlyList<Snippet> items)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (items == null)
            {
                return names;
            }
            for (int i = 0; i < items.Count; i++)
            {
                string g = items[i] == null ? null : items[i].GroupName;
                if (string.IsNullOrWhiteSpace(g))
                {
                    continue;
                }
                string trimmed = g.Trim();
                if (seen.Add(trimmed))
                {
                    names.Add(trimmed);
                }
            }
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        private static SnippetRowVm ToRow(Snippet s)
        {
            return new SnippetRowVm
            {
                Id = s.Id,
                Name = s.Name ?? string.Empty,
                Preview = PreviewOf(s.Content),
                GroupName = s.GroupName ?? string.Empty,
                SendEnter = s.SendEnter,
                Source = s
            };
        }

        private static string PreviewOf(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return string.Empty;
            }
            int end = content.Length;
            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] == '\r' || content[i] == '\n')
                {
                    end = i;
                    break;
                }
            }
            string first = content.Substring(0, end).Trim();
            const int max = 60;
            if (first.Length > max)
            {
                return first.Substring(0, max) + "…";
            }
            return first;
        }

        private static bool Contains(string value, string q)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
