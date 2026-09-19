namespace SshTool.Core.Hosts
{
    // 02-UI-DESIGN.md §5.1：主机行状态点。多会话取最「活跃」：
    // Connected > Reconnecting > Connecting > Error > None。
    public enum HostListStatus
    {
        None = 0,
        Error = 1,
        Connecting = 2,
        Reconnecting = 3,
        Connected = 4
    }
}
