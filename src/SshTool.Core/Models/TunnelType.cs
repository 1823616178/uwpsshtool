namespace SshTool.Core.Models
{
    // 与桌面端 TunnelConfig 同构（01-DESIGN.md §8.1）
    public enum TunnelType
    {
        Local,
        Remote,
        Dynamic,
        Relay
    }
}
