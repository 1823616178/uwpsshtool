using System.Collections.Generic;

namespace SshTool.Core.Sessions
{
    // N09b：连接参数（镜像 native Bridge.ConnectOptions，01-DESIGN §6.2）。
    // 由调用方填充后传入 ISshSession.ConnectAsync。
    public sealed class SshConnectRequest
    {
        public string Host { get; set; }
        public int Port { get; set; } = 22;
        public string Username { get; set; }
        public int ConnectTimeoutMs { get; set; } = 15000;
        public int KeepaliveSeconds { get; set; } = 30; // 0 关闭
        public string TermType { get; set; } = "xterm-256color";
        public int Cols { get; set; } = 80;  // OpenShellAsync 默认尺寸
        public int Rows { get; set; } = 24;
        // shell 打开后逐个 setenv（被拒仅忽略，N06 语义）
        public IReadOnlyDictionary<string, string> Env { get; set; }
        public string JumpSessionId { get; set; } = string.Empty; // F05 跳板预留，N09b 未启用
    }
}
