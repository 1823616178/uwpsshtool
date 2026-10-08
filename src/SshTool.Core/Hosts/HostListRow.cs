using System.Collections.Generic;

namespace SshTool.Core.Hosts
{
    public sealed class HostListRow
    {
        public string HostId { get; set; }
        public string Name { get; set; }
        public string AddressLine { get; set; }
        public string GroupId { get; set; }
        // G06：所属分组颜色（#RRGGBB），未分组为空串
        public string GroupColor { get; set; }
        public HostListStatus Status { get; set; }
        public bool ShowKey { get; set; }
        public bool ShowTmux { get; set; }
        public bool ShowJump { get; set; }
        public bool ShowTunnel { get; set; }
        // W03：行菜单据此显示「收藏 / 取消收藏」。
        public bool IsFavorite { get; set; }

        // ui/fix-pass：差量刷新判等——字段全同则保留旧实例（容器不重建）。
        public static bool ContentEquals(HostListRow a, HostListRow b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }
            if (a == null || b == null)
            {
                return false;
            }
            return a.HostId == b.HostId
                && a.Name == b.Name
                && a.AddressLine == b.AddressLine
                && a.GroupId == b.GroupId
                && a.GroupColor == b.GroupColor
                && a.Status == b.Status
                && a.ShowKey == b.ShowKey
                && a.ShowTmux == b.ShowTmux
                && a.ShowJump == b.ShowJump
                && a.ShowTunnel == b.ShowTunnel
                && a.IsFavorite == b.IsFavorite;
        }
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

        // ui/fix-pass：分组头（GroupHeader 显示的字段）是否相同；相同则保留旧分组只差量同步 Rows。
        public static bool HeaderEquals(HostListGroup a, HostListGroup b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }
            if (a == null || b == null)
            {
                return false;
            }
            return a.GroupId == b.GroupId
                && a.Name == b.Name
                && a.Color == b.Color
                && a.Order == b.Order
                && a.HostCount == b.HostCount
                && a.IsCollapsed == b.IsCollapsed;
        }
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
