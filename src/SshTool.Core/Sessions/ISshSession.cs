using System;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Terminal;

namespace SshTool.Core.Sessions
{
    // N09b：ExecAsync 结果（镜像 native Bridge.ExecResult）。
    // 打开/传输失败时 ExitCode=-1、Stderr 带诊断文本。
    public sealed class SshExecResult
    {
        public SshExecResult(int exitCode, string stdout, string stderr)
        {
            ExitCode = exitCode;
            Stdout = stdout ?? string.Empty;
            Stderr = stderr ?? string.Empty;
        }

        public int ExitCode { get; }
        public string Stdout { get; }
        public string Stderr { get; }
    }

    // N09b：SSH 会话抽象（01-DESIGN §4.1：Core 不引用 Native；§6.2 接口草图
    // 的 Core 镜像，只用 Core 自有类型）。
    //
    // 线程纪律：所有事件在会话 I/O 线程触发，订阅方如需碰 UI 必须自行经
    // DispatcherHelper 封送（§4.2）。返回码为 SshErrorCode（None = 成功）。
    public interface ISshSession : IDisposable
    {
        string Id { get; }
        SessionStateKind State { get; }

        // T03 终端渲染接入前为 null（ITerminalScreen 先定义，T03 实现）。
        ITerminalScreen Screen { get; }

        Task<SshErrorCode> ConnectAsync(SshConnectRequest request);
        // F07: ProxyJump — connect via direct-tcpip channel through jumpSession.
        // jumpSession must be authenticated (Established state) already.
        Task<SshErrorCode> ConnectJumpAsync(SshConnectRequest request, ISshSession jumpSession);
        Task<SshErrorCode> AuthenticatePasswordAsync(string password);
        // privateKeyPem 归调用方所有：调用方须在 await 结束后（finally）清零；实现方不得在
        // 返回后继续引用该数组（真身 NativeSshSession 也会在 finally 中清零）。
        Task<SshErrorCode> AuthenticatePublicKeyAsync(byte[] privateKeyPem, string passphrase);
        // K03：应用内 agent 认证——只传 keyId，私钥材料由 native 直接从 agent
        // 托管内存中取（不经过 C# 层）。agent 未解锁/无此 keyId/已超时时 native
        // 拒绝受理，返回 InternalError（上层按不可用处理，换下一个密钥）。
        Task<SshErrorCode> AuthenticateAgentAsync(string keyId);
        Task<SshErrorCode> AuthenticateKeyboardInteractiveAsync();
        // fix/functional-pass：服务器声明的认证方式（只在 Authenticating 态有效；失败返回 Unknown）。
        Task<AuthMethodsInfo> QueryAuthMethodsAsync();
        Task<SshErrorCode> OpenShellAsync(int cols, int rows);
        Task<SshExecResult> ExecAsync(string command);

        void Write(byte[] data);
        void Resize(int cols, int rows);
        void ProbeNow();
        void Close();

        event EventHandler<SessionStateChangedEventArgs> StateChanged;
        event EventHandler<HostKeyCheckEventArgs> HostKeyCheck;
        event EventHandler<AuthPromptEventArgs> AuthPrompt;

        // 重绘唤醒信号（01-DESIGN §4.2）：native 的 I/O 线程按 16 ms 窗合并投递，
        // 订阅方收到后唤醒帧调度器，再按 Screen.Revision 拷脏行——终端字节不走事件。
        // 事件在 I/O 线程触发，订阅方自行封送 UI 线程。
        event EventHandler ContentDirty;
    }
}
