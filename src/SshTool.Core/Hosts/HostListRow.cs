using System.Collections.Generic;

namespace SshTool.Core.Hosts
{
    public sealed class HostListRow
    {
        public string HostId { get; set; }
        public string Name { get; set; }
        public string AddressLine { get; set; }
        public string GroupId { get; set; }
        public HostListStatus Status { get; set; }
        public bool ShowKey { get; set; }
        public bool ShowTmux { get; set; }
        public bool ShowJump { get; set; }
        public bool ShowTunnel { get; set; }
        // W03：行菜单据此显示「收藏 / 取消收藏」。
        public bool IsFavorite { get; set; }
    }

    public sealed class HostListGroup
    {
        public const string UngroupedId = "";
        // W03：非搜索态置顶的两个视图段（不是真实分组，主机归属不变）。
        public const string FavoritesId = "__favorites";
        public const string RecentId = "__recent";

        public string GroupId { get; set; }
        public string Name { get; set; }
        public string Color { get; set; }
        public int Order { get; set; }
        public int HostCount { get; set; }
        public bool IsCollapsed { get; set; }
        public IReadOnlyList<HostListRow> Rows { get; set; }
    }

    public sealed class HostListSnapshot
    {
        public IReadOnlyList<HostListGroup> Groups { get; set; }
        public int TotalHostCount { get; set; }
        public int MatchCount { get; set; }
        public bool IsEmpty { get; set; }
        public bool HasNoMatches { get; set; }
    }
}
