namespace SshTool.Core.Sync
{
    // 03-SYNC-PROTOCOL.md §5.2 保护 2：运行中隧道探测。
    // 实现由隧道管理器提供（S17 的 IsBusy），Core 只定义接口以便 SyncLocalAdapter 下行保护使用。
    public interface ITunnelBusyProbe
    {
        bool IsBusy(string tunnelId);
    }
}
