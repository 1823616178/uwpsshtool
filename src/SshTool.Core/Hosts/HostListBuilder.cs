using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SshTool.Core.Models;
using SshTool.Core.Storage;

namespace SshTool.Core.Hosts
{
    public static class HostListBuilder
    {
        // W03（01-DESIGN §16.3）：「最近」段最多展示的主机数。
        public const int RecentLimit = 5;

        public static HostListSnapshot Build(
            IReadOnlyList<Host> hosts,
            IReadOnlyList<HostGroup> groups,
            IReadOnlyList<Tunnel> tunnels,
            string searchText,
            string sortMode,
            string collapsedJson,
            IHostStatusProvider statusProvider)
        {
            hosts = hosts ?? Array.Empty<Host>();
            groups = groups ?? Array.Empty<HostGroup>();
            tunnels = tunnels ?? Array.Empty<Tunnel>();
            IHostStatusProvider status = statusProvider ?? NullHostStatusProvider.Instance;
            HashSet<string> collapsed = DecodeCollapsed(collapsedJson);
            HashSet<string> tunnelHosts = BuildTunnelHostIds(tunnels);
            bool recent = string.Equals(sortMode, "recent", StringComparison.OrdinalIgnoreCase);

            List<Host> matched = Filter(hosts, searchText);
            List<HostGroup> orderedGroups = SortGroups(groups);
            HashSet<string> knownGroups = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < orderedGroups.Count; i++)
            {
                if (!string.IsNullOrEmpty(orderedGroups[i].Id))
                {
                    knownGroups.Add(orderedGroups[i].Id);
                }
            }

            var resultGroups = new List<HostListGroup>();
            if (string.IsNullOrWhiteSpace(searchText))
            {
                AddViewSections(matched, resultGroups, collapsed, status, tunnelHosts);
            }
            for (int i = 0; i < orderedGroups.Count; i++)
            {
                HostGroup group = orderedGroups[i];
                List<Host> members = MembersOf(matched, group.Id, knownGroups, grouped: true);
                if (members.Count == 0)
                {
                    continue;
                }
                resultGroups.Add(ToGroup(group.Id, group.Name, group.Color, group.Order, members, collapsed, recent, status, tunnelHosts));
            }

            List<Host> ungrouped = MembersOf(matched, HostListGroup.UngroupedId, knownGroups, grouped: false);
            if (ungrouped.Count > 0)
            {
                resultGroups.Add(ToGroup(
                    HostListGroup.UngroupedId, "未分组", string.Empty, int.MaxValue,
                    ungrouped, collapsed, recent, status, tunnelHosts));
            }

            return new HostListSnapshot
            {
                Groups = resultGroups,
                TotalHostCount = hosts.Count,
                MatchCount = matched.Count,
                IsEmpty = hosts.Count == 0,
                HasNoMatches = hosts.Count > 0 && matched.Count == 0
            };
        }

        // 收藏段（按名称）与最近段（最近连接的 RecentLimit 个）。名称是 Core 兜底文案，
        // App 按段 id 换成本地化标题。
        private static void AddViewSections(
            List<Host> hosts,
            List<HostListGroup> target,
            HashSet<string> collapsed,
            IHostStatusProvider status,
            HashSet<string> tunnelHosts)
        {
            var favorites = new List<Host>();
            var recent = new List<Host>();
            for (int i = 0; i < hosts.Count; i++)
            {
                if (hosts[i].Favorite)
                {
                    favorites.Add(hosts[i]);
                }
                if (!string.IsNullOrEmpty(hosts[i].LastConnectedAt))
                {
                    recent.Add(hosts[i]);
                }
            }
            if (favorites.Count > 0)
            {
                target.Add(ToGroup(HostListGroup.FavoritesId, "收藏", string.Empty, int.MinValue,
                    favorites, collapsed, false, status, tunnelHosts));
            }
            if (recent.Count > 0)
            {
                recent.Sort(CompareRecent);
                if (recent.Count > RecentLimit)
                {
                    recent.RemoveRange(RecentLimit, recent.Count - RecentLimit);
                }
                target.Add(ToGroup(HostListGroup.RecentId, "最近", string.Empty, int.MinValue + 1,
                    recent, collapsed, true, status, tunnelHosts));
            }
        }

        public static HashSet<string> DecodeCollapsed(string json)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }
            JObject obj;
            try
            {
                obj = JsonText.ParseObject(json.Trim());
            }
            catch (Exception)
            {
                return result;
            }
            if (obj == null)
            {
                return result;
            }
            foreach (var property in obj.Properties())
            {
                if (property.Value != null && property.Value.Type == JTokenType.Boolean && property.Value.Value<bool>())
                {
                    result.Add(property.Name);
                }
            }
            return result;
        }

        public static string EncodeCollapsed(IEnumerable<string> ids)
        {
            var obj = new JObject();
            if (ids != null)
            {
                foreach (string id in ids)
                {
                    if (id != null)
                    {
                        obj[id] = true;
                    }
                }
            }
            return obj.ToString(Formatting.None);
        }

        public static HashSet<string> ToggleCollapsed(IEnumerable<string> current, string groupId)
        {
            var next = new HashSet<string>(StringComparer.Ordinal);
            if (current != null)
            {
                foreach (string id in current)
                {
                    next.Add(id);
                }
            }
            if (groupId == null)
            {
                return next;
            }
            if (!next.Add(groupId))
            {
                next.Remove(groupId);
            }
            return next;
        }

        private static List<Host> Filter(IReadOnlyList<Host> hosts, string searchText)
        {
            string keyword = searchText == null ? string.Empty : searchText.Trim();
            var result = new List<Host>();
            for (int i = 0; i < hosts.Count; i++)
            {
                Host host = hosts[i];
                if (host == null)
                {
                    continue;
                }
                if (keyword.Length == 0 || Matches(host, keyword))
                {
                    result.Add(host);
                }
            }
            return result;
        }

        private static bool Matches(Host host, string keyword)
        {
            return Contains(host.Name, keyword)
                || Contains(host.HostName, keyword)
                || Contains(host.Username, keyword);
        }

        private static bool Contains(string value, string keyword)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static List<HostGroup> SortGroups(IReadOnlyList<HostGroup> groups)
        {
            var list = new List<HostGroup>();
            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i] != null && !string.IsNullOrEmpty(groups[i].Id))
                {
                    list.Add(groups[i]);
                }
            }
            list.Sort(CompareGroups);
            return list;
        }

        private static int CompareGroups(HostGroup a, HostGroup b)
        {
            int order = a.Order.CompareTo(b.Order);
            if (order != 0)
            {
                return order;
            }
            return string.Compare(a.Name ?? string.Empty, b.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private static List<Host> MembersOf(
            List<Host> hosts, string groupId, HashSet<string> knownGroups, bool grouped)
        {
            var members = new List<Host>();
            for (int i = 0; i < hosts.Count; i++)
            {
                string id = hosts[i].GroupId;
                bool isKnown = !string.IsNullOrEmpty(id) && knownGroups.Contains(id);
                if (grouped)
                {
                    if (string.Equals(id, groupId, StringComparison.Ordinal))
                    {
                        members.Add(hosts[i]);
                    }
                }
                else if (!isKnown)
                {
                    members.Add(hosts[i]);
                }
            }
            return members;
        }

        private static HostListGroup ToGroup(
            string groupId,
            string name,
            string color,
            int order,
            List<Host> members,
            HashSet<string> collapsed,
            bool recent,
            IHostStatusProvider status,
            HashSet<string> tunnelHosts)
        {
            members.Sort(recent ? (Comparison<Host>)CompareRecent : CompareName);
            bool isCollapsed = collapsed.Contains(groupId ?? HostListGroup.UngroupedId);
            var rows = new List<HostListRow>();
            if (!isCollapsed)
            {
                for (int i = 0; i < members.Count; i++)
                {
                    rows.Add(ToRow(members[i], groupId, status, tunnelHosts));
                }
            }
            return new HostListGroup
            {
                GroupId = groupId ?? HostListGroup.UngroupedId,
                Name = name,
                Color = color ?? string.Empty,
                Order = order,
                HostCount = members.Count,
                IsCollapsed = isCollapsed,
                Rows = rows
            };
        }

        private static int CompareName(Host a, Host b)
        {
            return string.Compare(a.Name ?? string.Empty, b.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareRecent(Host a, Host b)
        {
            int byTime = string.Compare(
                b.LastConnectedAt ?? string.Empty,
                a.LastConnectedAt ?? string.Empty,
                StringComparison.Ordinal);
            if (byTime != 0)
            {
                return byTime;
            }
            return CompareName(a, b);
        }

        private static HostListRow ToRow(
            Host host, string groupId, IHostStatusProvider status, HashSet<string> tunnelHosts)
        {
            return new HostListRow
            {
                HostId = host.Id,
                Name = host.Name ?? string.Empty,
                AddressLine = FormatAddress(host.Username, host.HostName, host.Port),
                GroupId = groupId ?? HostListGroup.UngroupedId,
                Status = status.GetStatus(host.Id),
                ShowKey = host.AuthType == AuthType.Key,
                ShowTmux = host.TmuxAutoAttach,
                ShowJump = !string.IsNullOrWhiteSpace(host.JumpHostId),
                ShowTunnel = tunnelHosts.Contains(host.Id),
                IsFavorite = host.Favorite
            };
        }

        public static string FormatAddress(string username, string hostName, int port)
        {
            string host = hostName ?? string.Empty;
            if (host.IndexOf(':') >= 0 && host.Length > 0 && host[0] != '[')
            {
                host = "[" + host + "]";
            }
            string user = username ?? string.Empty;
            int shown = port < 1 ? 22 : port;
            return user + "@" + host + ":" + shown.ToString();
        }

        private static HashSet<string> BuildTunnelHostIds(IReadOnlyList<Tunnel> tunnels)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < tunnels.Count; i++)
            {
                Tunnel tunnel = tunnels[i];
                if (tunnel == null)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(tunnel.ServerId))
                {
                    ids.Add(tunnel.ServerId);
                }
                if (!string.IsNullOrEmpty(tunnel.DestServerId))
                {
                    ids.Add(tunnel.DestServerId);
                }
            }
            return ids;
        }
    }
}
