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
        // K03：agent 认证脚本化——按 keyId 查表，未命中用 DefaultAgentResult。
        public SshErrorCode DefaultAgentResult = SshErrorCode.None;
        public readonly Dictionary<string, SshErrorCode> AgentResults =
            new Dictionary<string, SshErrorCode>(StringComparer.Ordinal);
        public readonly List<string> AgentAttempts = new List<string>();
        // fix/functional-pass：公钥认证按序结果（取完回落 PublicKeyResult）与每次收到的短语。
        public readonly Queue<SshErrorCode> PublicKeyResultSequence = new Queue<SshErrorCode>();
        public readonly List<string> Passphrases = new List<string>();
        // fix/functional-pass：agent 认证按序结果（优先于 AgentResults 表，取完回落）。
        public readonly Queue<SshErrorCode> AgentResultSequence = new Queue<SshErrorCode>();
        public SshErrorCode OpenShellResult = SshErrorCode.None;
        public SshExecResult ExecResult = new SshExecResult(0, string.Empty, string.Empty);
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
        public readonly List<byte[]> Writes = new List<byte[]>();
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

        // 非空时 ConnectAsync 先抛 HostKeyCheck 并等待 Accept/Reject。
        public HostKeyInfo HostKeyOnConnect;
        public IReadOnlyList<string> KiPrompts;
        public TaskCompletionSource<SshErrorCode> ConnectHold;
        public bool HostKeyOnJump;
        public ISshSession LastJumpSession;

        public async Task<SshErrorCode> ConnectAsync(SshConnectRequest request)
        {
            Calls.Add("Connect");
            LastConnectRequest = request;
            if (ConnectHold != null)
            {
                return await ConnectHold.Task.ConfigureAwait(false);
            }
            if (HostKeyOnConnect != null && HostKeyCheck != null)
            {
                var tcs = new TaskCompletionSource<bool>();
                var args = new HostKeyCheckEventArgs(HostKeyOnConnect, accept => tcs.TrySetResult(accept));
                HostKeyCheck.Invoke(this, args);
                bool accepted = await tcs.Task.ConfigureAwait(false);
                HostKeyDecision = accepted;
                if (!accepted)
                {
                    FireStateChanged(SessionStateKind.Error, SshErrorCode.HostKeyMismatch);
                    return SshErrorCode.HostKeyMismatch;
                }
            }
            if (ConnectResult != SshErrorCode.None)
            {
                FireStateChanged(SessionStateKind.Error, ConnectResult);
                return ConnectResult;
            }
            FireStateChanged(SessionStateKind.Authenticating);
            return SshErrorCode.None;
        }

        // F07: ProxyJump fake — behaves like ConnectAsync for testing purposes.
        public async Task<SshErrorCode> ConnectJumpAsync(SshConnectRequest request, ISshSession jumpSession)
        {
            Calls.Add("ConnectJump");
            LastConnectRequest = request;
            LastJumpSession = jumpSession;
            // fix/functional-pass：opt-in——经跳板连接也走主机密钥校验（隧道跳板链用例）。
            if (HostKeyOnJump && HostKeyOnConnect != null && HostKeyCheck != null)
            {
                var tcs = new TaskCompletionSource<bool>();
                var args = new HostKeyCheckEventArgs(HostKeyOnConnect, accept => tcs.TrySetResult(accept));
                HostKeyCheck.Invoke(this, args);
                bool accepted = await tcs.Task.ConfigureAwait(false);
                HostKeyDecision = accepted;
                if (!accepted)
                {
                    FireStateChanged(SessionStateKind.Error, SshErrorCode.HostKeyMismatch);
                    return SshErrorCode.HostKeyMismatch;
                }
            }
            if (ConnectResult != SshErrorCode.None)
            {
                FireStateChanged(SessionStateKind.Error, ConnectResult);
                return ConnectResult;
            }
            FireStateChanged(SessionStateKind.Authenticating);
            return SshErrorCode.None;
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
            Passphrases.Add(passphrase);
            if (PublicKeyResultSequence.Count > 0)
            {
                return Task.FromResult(PublicKeyResultSequence.Dequeue());
            }
            return Task.FromResult(PublicKeyResult);
        }

        // K03：agent 认证（只收 keyId，与真身一致不接触私钥材料）。
        public Task<SshErrorCode> AuthenticateAgentAsync(string keyId)
        {
            Calls.Add("AuthAgent:" + keyId);
            AgentAttempts.Add(keyId);
            SshErrorCode code;
            if (AgentResultSequence.Count > 0)
            {
                return Task.FromResult(AgentResultSequence.Dequeue());
            }
            if (!AgentResults.TryGetValue(keyId, out code))
            {
                code = DefaultAgentResult;
            }
            return Task.FromResult(code);
        }

        // fix/functional-pass：按序返回的方法列表（取完后一直返回最后一个；空 = Unknown）。
        public readonly Queue<string> AuthMethodsSequence = new Queue<string>();
        public string AuthMethodsRaw = string.Empty;
        public int AuthMethodsQueries;

        public Task<AuthMethodsInfo> QueryAuthMethodsAsync()
        {
            // 不进 Calls：既有用例断言的调用序列保持不变。
            AuthMethodsQueries++;
            if (AuthMethodsSequence.Count > 0)
            {
                AuthMethodsRaw = AuthMethodsSequence.Dequeue();
            }
            return Task.FromResult(AuthMethodsInfo.Parse(AuthMethodsRaw));
        }

        public async Task<SshErrorCode> AuthenticateKeyboardInteractiveAsync()
        {
            Calls.Add("AuthKeyboardInteractive");
            if (KiPrompts != null && KiPrompts.Count > 0)
            {
                var tcs = new TaskCompletionSource<bool>();
                var echo = new bool[KiPrompts.Count];
                var args = new AuthPromptEventArgs("", "", KiPrompts, echo,
                    (answered, answers) =>
                    {
                        AuthAnswered = answered;
                        AuthAnswers = answers;
                        tcs.TrySetResult(answered);
                    });
                AuthPrompt?.Invoke(this, args);
                bool ok = await tcs.Task.ConfigureAwait(false);
                if (!ok)
                {
                    return SshErrorCode.AuthKeyboardInteractiveFailed;
                }
            }
            return KeyboardInteractiveResult;
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
            Writes.Add(data);
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

        public readonly Queue<FakeSshSession> Queue = new Queue<FakeSshSession>();

        public ISshSession Create()
        {
            FakeSshSession session;
            if (Queue.Count > 0)
            {
                session = Queue.Dequeue();
            }
            else
            {
                session = Next ?? new FakeSshSession();
            }
            Created.Add(session);
            return session;
        }
    }
}
