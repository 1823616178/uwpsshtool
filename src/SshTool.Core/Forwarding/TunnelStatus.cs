namespace SshTool.Core.Forwarding
{
    // 隧道运行状态机（01-DESIGN.md §11.2，与桌面端 TunnelState 一致）：
    // idle → connecting → running；运行中断 → reconnecting（自动重连退避）
    // 或 error（不自动重连/手动停止之外的首启失败）；reconnecting 定时后回到
    // connecting 语义的重试。relay 类型永远不会进入本状态机（拒绝启动）。
    public enum TunnelStateKind
    {
        Idle,
        Connecting,
        Running,
        Reconnecting,
        Error
    }

    // 单条隧道的对外状态快照（与桌面端 manager.ts statuses() 返回项同构）。
    public sealed class TunnelStatusSnapshot
    {
        public string TunnelId { get; set; }
        public TunnelStateKind State { get; set; }
        public string Message { get; set; }
        public int RetryInSeconds { get; set; }
        public int Attempt { get; set; }
        public TunnelStats Stats { get; set; }
    }

    // 用户发起的启停操作结果（首启失败的消息直接展示给用户）。
    public sealed class TunnelStartResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }

        public static TunnelStartResult Ok()
        {
            return new TunnelStartResult { Success = true, Message = string.Empty };
        }

        public static TunnelStartResult Fail(string message)
        {
            return new TunnelStartResult { Success = false, Message = message ?? string.Empty };
        }
    }
}
