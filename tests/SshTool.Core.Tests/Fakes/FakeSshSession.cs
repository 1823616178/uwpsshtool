using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Sessions;
using SshTool.Core.Terminal;

namespace SshTool.Core.Tests.Fakes
{
    // N09b：ISshSession 的可脚本化假实现（供 SessionManager 等 Core 逻辑单测）。
    // 预设每个方法的返回码/结果；调用全部记录在 Calls 与 Last* 字段；
    // 事件由 Fire* 方法手动触发；HostKeyCheck/AuthPrompt 的作答记录在
    // HostKeyDecision/AuthAnswered/AuthAnswers。无任何线程与网络。
    public sealed class FakeSshSession : ISshSession
    {
        // ---- 脚本化结果 ----
        public SshErrorCode ConnectResult = SshErrorCode.None;
        public SshErrorCode PasswordResult = SshErrorCode.None;
        public SshErrorCode PublicKeyResult = SshErrorCode.None;
        public SshErrorCode KeyboardInteractiveResult = SshErrorCode.None;
        public SshErrorCode OpenShellResult = SshErrorCode.None;
        public SshExecResult ExecResult = new SshExecResult(0, string.Empty, string.Empty);
        public byte[] PendingOutput = new byte[0];
        public string Id { get; set; } = "fake-1";
        public ITerminalScreen ScreenInstance;

        // ---- 调用记录 ----
        public readonly List<string> Calls = new List<string>();
        public SshConnectRequest LastConnectRequest;
        public string LastPassword;
        public byte[] LastPrivateKey;
        public string LastPassphrase;
        public string LastExecCommand;
        public byte[] LastWritten;
        public int LastOpenShellCols;
        public int LastOpenShellRows;
        public int LastResizeCols;
        public int LastResizeRows;
        public int ProbeNowCount;
        public int CloseCount;
        public int DisposeCount;

        // ---- 事件作答记录 ----
        public bool? HostKeyDecision;   // true=Accept / false=Reject / null=未作答
        public bool? AuthAnswered;      // true=Respond / false=Cancel / null=未答复
        public IReadOnlyList<string> AuthAnswers;

        public SessionStateKind State { get; private set; } = SessionStateKind.Idle;

        public ITerminalScreen Screen
        {
            get { return ScreenInstance; }
        }

        public event EventHandler<SessionStateChangedEventArgs> StateChanged;
        public event EventHandler<HostKeyCheckEventArgs> HostKeyCheck;
        public event EventHandler<AuthPromptEventArgs> AuthPrompt;
        public event EventHandler ContentDirty;

        public Task<SshErrorCode> ConnectAsync(SshConnectRequest request)
        {
            Calls.Add("Connect");
            LastConnectRequest = request;
            return Task.FromResult(ConnectResult);
        }

        public Task<SshErrorCode> AuthenticatePasswordAsync(string password)
        {
            Calls.Add("AuthPassword");
            LastPassword = password;
            return Task.FromResult(PasswordResult);
        }

        public Task<SshErrorCode> AuthenticatePublicKeyAsync(byte[] privateKeyPem, string passphrase)
        {
            Calls.Add("AuthPublicKey");
            LastPrivateKey = privateKeyPem;
            LastPassphrase = passphrase;
            return Task.FromResult(PublicKeyResult);
        }

        public Task<SshErrorCode> AuthenticateKeyboardInteractiveAsync()
        {
            Calls.Add("AuthKeyboardInteractive");
            return Task.FromResult(KeyboardInteractiveResult);
        }

        public Task<SshErrorCode> OpenShellAsync(int cols, int rows)
        {
            Calls.Add("OpenShell");
            LastOpenShellCols = cols;
            LastOpenShellRows = rows;
            return Task.FromResult(OpenShellResult);
        }

        public Task<SshExecResult> ExecAsync(string command)
        {
            Calls.Add("Exec");
            LastExecCommand = command;
            return Task.FromResult(ExecResult);
        }

        public void Write(byte[] data)
        {
            Calls.Add("Write");
            LastWritten = data;
        }

        public void Resize(int cols, int rows)
        {
            Calls.Add("Resize");
            LastResizeCols = cols;
            LastResizeRows = rows;
        }

        public void ProbeNow()
        {
            Calls.Add("ProbeNow");
            ProbeNowCount++;
        }

        public void Close()
        {
            Calls.Add("Close");
            CloseCount++;
        }

        public byte[] FetchPendingOutput()
        {
            Calls.Add("FetchPendingOutput");
            byte[] chunk = PendingOutput;
            PendingOutput = new byte[0]; // 与真身一致：拉取即复位
            return chunk;
        }

        public void Dispose()
        {
            DisposeCount++;
        }

        // ---- 手动触发事件 ----

        public void FireStateChanged(SessionStateKind state, SshErrorCode errorCode = SshErrorCode.None,
                                     string detail = "")
        {
            State = state;
            StateChanged?.Invoke(this, new SessionStateChangedEventArgs(state, errorCode, detail));
        }

        // 返回 args：无订阅时测试可直接对其作答。
        public HostKeyCheckEventArgs FireHostKeyCheck(HostKeyInfo info)
        {
            var args = new HostKeyCheckEventArgs(info, accept => HostKeyDecision = accept);
            HostKeyCheck?.Invoke(this, args);
            return args;
        }

        public AuthPromptEventArgs FireAuthPrompt(IReadOnlyList<string> prompts, IReadOnlyList<bool> echo,
                                                  string name = "", string instruction = "")
        {
            var args = new AuthPromptEventArgs(name, instruction, prompts, echo,
                (answered, answers) =>
                {
                    AuthAnswered = answered;
                    AuthAnswers = answers;
                });
            AuthPrompt?.Invoke(this, args);
            return args;
        }

        public void FireContentDirty()
        {
            ContentDirty?.Invoke(this, EventArgs.Empty);
        }
    }

    // 可脚本化工厂：预设 Next，或每次 Create 新实例；Created 供断言。
    public sealed class FakeSshSessionFactory : ISshSessionFactory
    {
        public FakeSshSession Next;
        public readonly List<FakeSshSession> Created = new List<FakeSshSession>();

        public ISshSession Create()
        {
            FakeSshSession session = Next ?? new FakeSshSession();
            Created.Add(session);
            return session;
        }
    }
}
