namespace SshTool.Core.Sessions
{
    // N09b：会话状态（镜像 native Bridge.SessionState，01-DESIGN §6.2；
    // core 的 Closing 瞬态不上抛、Closed 映射为 Disconnected，见 §6.2 注记）。
    public enum SessionStateKind
    {
        Idle,
        Connecting,
        Handshaking,
        Authenticating,
        Established,
        Disconnected,
        Error,
    }
}
