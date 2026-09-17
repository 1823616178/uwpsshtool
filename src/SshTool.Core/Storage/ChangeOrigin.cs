namespace SshTool.Core.Storage
{
    // 01-DESIGN.md §8.4：Changed 事件携带来源；Sync 来源不触发同步标脏，防止回环。
    public enum ChangeOrigin
    {
        User,
        Sync
    }
}
