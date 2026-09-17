namespace SshTool.Core.Sync.Api
{
    // 03-SYNC-PROTOCOL.md §2.3：与桌面端 ApiErrorKind 同构。
    public enum ApiErrorKind
    {
        Http,
        Network,
        Timeout,
        Protocol,
        Authentication
    }
}
