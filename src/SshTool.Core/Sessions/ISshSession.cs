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
        Task<SshErrorCode> AuthenticatePasswordAsync(string password);
        Task<SshErrorCode> AuthenticatePublicKeyAsync(byte[] privateKeyPem, string passphrase);
        Task<SshErrorCode> AuthenticateKeyboardInteractiveAsync();
        Task<SshErrorCode> OpenShellAsync(int cols, int rows);
        Task<SshExecResult> ExecAsync(string command);

        void Write(byte[] data);
        void Resize(int cols, int rows);
        void ProbeNow();
        void Close();

        // 过渡取数 API：拉走待显示输出并复位 ContentDirty 合并标志
        //（T03 TerminalScreen 接管后移除，见 §6.2 注记）。
        byte[] FetchPendingOutput();

        event EventHandler<SessionStateChangedEventArgs> StateChanged;
        event EventHandler<HostKeyCheckEventArgs> HostKeyCheck;
        event EventHandler<AuthPromptEventArgs> AuthPrompt;
        event EventHandler ContentDirty;
    }
}
