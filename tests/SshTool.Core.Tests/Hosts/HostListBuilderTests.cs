using System.Collections.Generic;
using System.Linq;
using SshTool.Core.Hosts;
using SshTool.Core.Models;
using Xunit;

namespace SshTool.Core.Tests.Hosts
{
    public class HostListBuilderTests
    {
        private static Host H(string id, string name, string groupId = null, string host = "10.0.0.1", string user = "root", int port = 22)
        {
            var hostRow = Defaults.NewHost();
            hostRow.Id = id;
            hostRow.Name = name;
            hostRow.GroupId = groupId;
            hostRow.HostName = host;
            hostRow.Username = user;
            hostRow.Port = port;
            return hostRow;
        }

        private static HostGroup G(string id, string name, int order)
        {
            var g = Defaults.NewGroup(name);
            g.Id = id;
            g.Order = order;
            g.Color = "#4F8CFF";
            return g;
        }

        private static HostListSnapshot Build(
            IReadOnlyList<Host> hosts,
            IReadOnlyList<HostGroup> groups = null,
            IReadOnlyList<Tunnel> tunnels = null,
            string search = "",
            string sort = "name",
            string collapsed = "{}",
            IHostStatusProvider status = null)
        {
            return HostListBuilder.Build(hosts, groups, tunnels, search, sort, collapsed, status);
        }

        [Fact]
        public void EmptyHosts_IsEmpty()
        {
            HostListSnapshot snap = Build(new Host[0], new[] { G("g1", "生产", 0) });
            Assert.True(snap.IsEmpty);
            Assert.False(snap.HasNoMatches);
            Assert.Empty(snap.Groups);
        }

        [Fact]
        public void Groups_OrderThenUngroupedLast()
        {
            HostListSnapshot snap = Build(
                new[] { H("a", "nas"), H("b", "web", "g2"), H("c", "db", "g1") },
                new[] { G("g2", "测试", 2), G("g1", "生产", 1) });
            Assert.Equal(new[] { "生产", "测试", "未分组" }, snap.Groups.Select(x => x.Name).ToArray());
            Assert.Equal("c", snap.Groups[0].Rows[0].HostId);
            Assert.Equal("b", snap.Groups[1].Rows[0].HostId);
            Assert.Equal("a", snap.Groups[2].Rows[0].HostId);
            Assert.Equal(HostListGroup.UngroupedId, snap.Groups[2].GroupId);
        }

        [Fact]
        public void EmptyGroup_Omitted()
        {
            HostListSnapshot snap = Build(
                new[] { H("a", "web", "g1") },
                new[] { G("g1", "生产", 0), G("g2", "空", 1) });
            Assert.Equal(new[] { "生产" }, snap.Groups.Select(x => x.Name).ToArray());
        }

        [Fact]
        public void UnknownGroupId_GoesUngrouped()
        {
            HostListSnapshot snap = Build(
                new[] { H("a", "ghost", "missing") },
                new[] { G("g1", "生产", 0) });
            Assert.Single(snap.Groups);
            Assert.Equal("未分组", snap.Groups[0].Name);
            Assert.Equal("a", snap.Groups[0].Rows[0].HostId);
        }

        [Fact]
        public void Sort_ByName_OrdinalIgnoreCase()
        {
            HostListSnapshot snap = Build(
                new[] { H("a", "zeta", "g"), H("b", "Alpha", "g"), H("c", "beta", "g") },
                new[] { G("g", "组", 0) },
                sort: "name");
            Assert.Equal(new[] { "Alpha", "beta", "zeta" }, snap.Groups[0].Rows.Select(r => r.Name).ToArray());
        }

        [Fact]
        public void Sort_ByRecent_EmptyLastThenName()
        {
            Host a = H("a", "old", "g");
            a.LastConnectedAt = "2020-01-01T00:00:00Z";
            Host b = H("b", "new", "g");
            b.LastConnectedAt = "2026-01-01T00:00:00Z";
            Host c = H("c", "never", "g");
            HostListSnapshot snap = Build(new[] { a, b, c }, new[] { G("g", "组", 0) }, sort: "recent");
            // W03：非搜索态先有「最近」视图段，真实分组在其后。
            HostListGroup group = snap.Groups.Single(g => g.GroupId == "g");
            Assert.Equal(new[] { "new", "old", "never" }, group.Rows.Select(r => r.Name).ToArray());
        }

        [Fact]
        public void ViewSections_FavoritesThenRecent_BeforeRealGroups()
        {
            Host a = H("a", "beta", "g");
            a.Favorite = true;
            Host b = H("b", "alpha", "g");
            b.Favorite = true;
            b.LastConnectedAt = "2026-01-01T00:00:00Z";
            Host c = H("c", "gamma", "g");
            HostListSnapshot snap = Build(new[] { a, b, c }, new[] { G("g", "组", 0) });
            Assert.Equal(new[] { HostListGroup.FavoritesId, HostListGroup.RecentId, "g" },
                snap.Groups.Select(g => g.GroupId).ToArray());
            Assert.Equal(new[] { "alpha", "beta" }, snap.Groups[0].Rows.Select(r => r.Name).ToArray());
            Assert.True(snap.Groups[0].Rows.All(r => r.IsFavorite));
            Assert.Equal(new[] { "alpha" }, snap.Groups[1].Rows.Select(r => r.Name).ToArray());
            Assert.Equal(3, snap.Groups[2].HostCount); // 段是视图，不从真实分组里拿走主机
        }

        [Fact]
        public void ViewSections_Recent_LimitedAndNewestFirst()
        {
            var hosts = new List<Host>();
            for (int i = 0; i < HostListBuilder.RecentLimit + 3; i++)
            {
                Host h = H("h" + i, "n" + i, "g");
                h.LastConnectedAt = "2026-01-0" + (i + 1) + "T00:00:00Z";
                hosts.Add(h);
            }
            HostListGroup recent = Build(hosts, new[] { G("g", "组", 0) }).Groups
                .Single(g => g.GroupId == HostListGroup.RecentId);
            Assert.Equal(HostListBuilder.RecentLimit, recent.Rows.Count);
            Assert.Equal("h7", recent.Rows[0].HostId);
        }

        [Fact]
        public void ViewSections_HiddenWhileSearching_AndWhenEmpty()
        {
            Host a = H("a", "web", "g");
            a.Favorite = true;
            a.LastConnectedAt = "2026-01-01T00:00:00Z";
            var groups = new[] { G("g", "组", 0) };
            Assert.DoesNotContain(Build(new[] { a }, groups, search: "web").Groups,
                g => g.GroupId == HostListGroup.FavoritesId || g.GroupId == HostListGroup.RecentId);
            Assert.Equal(new[] { "g" }, Build(new[] { H("b", "plain", "g") }, groups).Groups.Select(g => g.GroupId).ToArray());
        }

        [Fact]
        public void ViewSections_Collapsible()
        {
            Host a = H("a", "web", "g");
            a.Favorite = true;
            HostListSnapshot snap = Build(new[] { a }, new[] { G("g", "组", 0) },
                collapsed: "{\"__favorites\":true}");
            HostListGroup fav = snap.Groups.Single(g => g.GroupId == HostListGroup.FavoritesId);
            Assert.True(fav.IsCollapsed);
            Assert.Empty(fav.Rows);
            Assert.Equal(1, fav.HostCount);
        }

        [Fact]
        public void Search_NameHostUser_CaseInsensitive()
        {
            var hosts = new[]
            {
                H("a", "Web-01", "g", "10.0.0.11", "root"),
                H("b", "db", "g", "10.0.0.21", "Admin"),
                H("c", "nas", "g", "files.local", "kim")
            };
            var groups = new[] { G("g", "组", 0) };
            Assert.Equal("a", Build(hosts, groups, search: "web").Groups[0].Rows[0].HostId);
            Assert.Equal("a", Build(hosts, groups, search: "10.0.0.11").Groups[0].Rows[0].HostId);
            Assert.Equal("b", Build(hosts, groups, search: "admin").Groups[0].Rows[0].HostId);
            Assert.True(Build(hosts, groups, search: "nope").HasNoMatches);
            Assert.Equal(0, Build(hosts, groups, search: "nope").MatchCount);
        }

        [Fact]
        public void Collapsed_HidesRowsKeepsCount()
        {
            HostListSnapshot snap = Build(
                new[] { H("a", "web", "g"), H("b", "db", "g") },
                new[] { G("g", "生产", 0) },
                collapsed: "{\"g\":true}");
            Assert.True(snap.Groups[0].IsCollapsed);
            Assert.Equal(2, snap.Groups[0].HostCount);
            Assert.Empty(snap.Groups[0].Rows);
        }

        [Fact]
        public void Collapsed_UngroupedUsesEmptyKey()
        {
            HostListSnapshot snap = Build(
                new[] { H("a", "nas") },
                new HostGroup[0],
                collapsed: "{\"\":true}");
            Assert.True(snap.Groups[0].IsCollapsed);
            Assert.Empty(snap.Groups[0].Rows);
        }

        [Fact]
        public void DecodeCollapsed_DirtyData_EmptySet()
        {
            Assert.Empty(HostListBuilder.DecodeCollapsed(null));
            Assert.Empty(HostListBuilder.DecodeCollapsed(""));
            Assert.Empty(HostListBuilder.DecodeCollapsed("[]"));
            Assert.Empty(HostListBuilder.DecodeCollapsed("not-json"));
            Assert.Empty(HostListBuilder.DecodeCollapsed("{\"g\":false,\"x\":1}"));
        }

        [Fact]
        public void DecodeCollapsed_KeepsTrueKeys()
        {
            HashSet<string> set = HostListBuilder.DecodeCollapsed("{\"g1\":true,\"g2\":true}");
            Assert.Contains("g1", set);
            Assert.Contains("g2", set);
        }

        [Fact]
        public void EncodeToggle_RoundTrip()
        {
            HashSet<string> once = HostListBuilder.ToggleCollapsed(new string[0], "g1");
            Assert.Contains("g1", once);
            string json = HostListBuilder.EncodeCollapsed(once);
            Assert.Contains("g1", HostListBuilder.DecodeCollapsed(json));
            HashSet<string> twice = HostListBuilder.ToggleCollapsed(once, "g1");
            Assert.DoesNotContain("g1", twice);
        }

        [Fact]
        public void Badges_KeyTmuxJumpTunnel()
        {
            Host host = H("h1", "web", "g");
            host.AuthType = AuthType.Key;
            host.TmuxAutoAttach = true;
            host.JumpHostId = "jump";
            var tunnel = Defaults.NewTunnel("h1");
            HostListSnapshot snap = Build(
                new[] { host },
                new[] { G("g", "组", 0) },
                new[] { tunnel });
            HostListRow row = snap.Groups[0].Rows[0];
            Assert.True(row.ShowKey);
            Assert.True(row.ShowTmux);
            Assert.True(row.ShowJump);
            Assert.True(row.ShowTunnel);
        }

        [Fact]
        public void Status_FromProvider()
        {
            var status = new MapStatus();
            status.Map["h1"] = HostListStatus.Connected;
            HostListSnapshot snap = Build(
                new[] { H("h1", "web", "g") },
                new[] { G("g", "组", 0) },
                status: status);
            Assert.Equal(HostListStatus.Connected, snap.Groups[0].Rows[0].Status);
        }

        [Fact]
        public void AddressLine_WrapsIpv6()
        {
            HostListSnapshot snap = Build(
                new[] { H("a", "v6", null, "::1", "user", 22) },
                new HostGroup[0]);
            Assert.Equal("user@[::1]:22", snap.Groups[0].Rows[0].AddressLine);
        }

        [Fact]
        public void NullInputs_DoNotThrow()
        {
            HostListSnapshot snap = HostListBuilder.Build(null, null, null, null, null, null, null);
            Assert.True(snap.IsEmpty);
        }

        private sealed class MapStatus : IHostStatusProvider
        {
            public Dictionary<string, HostListStatus> Map = new Dictionary<string, HostListStatus>();

            public HostListStatus GetStatus(string hostId)
            {
                HostListStatus status;
                return Map.TryGetValue(hostId, out status) ? status : HostListStatus.None;
            }
        }
    }
}
