using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Sessions;
using SshTool.Core.Terminal;
using NativeBridge = SshTool.Native.Bridge;

namespace SshTool.App.Platform
{
    // N09b：ISshSession 的原生适配（01-DESIGN §4.1：Core 不引用 Native，
    // WinRT 类型转换与 IAsyncOperation→Task 都在本层）。
    //
    // 线程纪律：native 事件在会话 I/O 线程触发，本适配器原样转发（不封送）；
    // 碰 UI 由订阅方走 Infrastructure.DispatcherHelper（§4.2 实现纪律）。
    // Dispose/Close 必须在非会话 I/O 线程调用（Shutdown 会 join 该线程）。
    public sealed class NativeSshSession : ISshSession
    {
        private readonly NativeBridge.SshSession _native;
        private readonly NativeTerminalScreen _screen;
        private readonly NativeSshAgent _agent;
        private bool _disposed;

        public NativeSshSession()
            : this(null)
        {
        }

        // K03：agent 句柄由 NativeSshSessionFactory 注入（与 SessionManager 共用
        // 同一个 NativeSshAgent 实例，多会话复用同一份 native 内存）。
        public NativeSshSession(NativeSshAgent agent)
        {
            _native = new NativeBridge.SshSession();
            _screen = new NativeTerminalScreen(_native.Screen);
            _agent = agent;
            _native.StateChanged += OnStateChanged;
            _native.HostKeyCheck += OnHostKeyCheck;
            _native.AuthPrompt += OnAuthPrompt;
            _native.ContentDirty += OnContentDirty;
        }

        public string Id
        {
            get { return _native.Id; }
        }

        // F02：供 NativeSftpClient 直通 native SFTP 挂载点（同一程序集内可见，
        // 不进 Core；模式见 NativeSshAgent.Native）。
        internal NativeBridge.SshSession Native
        {
            get { return _native; }
        }

        public SessionStateKind State
        {
            get { return MapState(_native.State); }
        }

        public ITerminalScreen Screen
        {
            get { return _screen; }
        }

        public event EventHandler<SessionStateChangedEventArgs> StateChanged;
        public event EventHandler<HostKeyCheckEventArgs> HostKeyCheck;
        public event EventHandler<AuthPromptEventArgs> AuthPrompt;
        public event EventHandler ContentDirty;

        public async Task<SshErrorCode> ConnectAsync(SshConnectRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            var options = new NativeBridge.ConnectOptions
            {
                Host = request.Host,
                Port = request.Port,
                Username = request.Username,
                ConnectTimeoutMs = request.ConnectTimeoutMs,
                KeepaliveSeconds = request.KeepaliveSeconds,
                TermType = request.TermType,
                Cols = request.Cols,
                Rows = request.Rows,
                // WinRT Platform::String^ 属性不接收托管 null；F05 尚未启用时传空串。
                JumpSessionId = request.JumpSessionId ?? string.Empty,
            };
            if (request.Env != null)
            {
                // netstandard1.4 / C# 7.3：Dictionary 没有 IReadOnlyDictionary 构造器。
                var env = new Dictionary<string, string>();
                foreach (KeyValuePair<string, string> pair in request.Env)
                {
                    env[pair.Key] = pair.Value;
                }
                options.Env = env;
            }
            int code = await _native.ConnectAsync(options).AsTask().ConfigureAwait(false);
            return (SshErrorCode)code;
        }

        // F07: ProxyJump — route new session through jumpSession's direct-tcpip channel.
        public async Task<SshErrorCode> ConnectJumpAsync(SshConnectRequest request, ISshSession jumpSession)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (jumpSession == null) throw new ArgumentNullException(nameof(jumpSession));
            var jumpNative = jumpSession as NativeSshSession;
            if (jumpNative == null)
            {
                throw new ArgumentException("jumpSession must be a NativeSshSession", nameof(jumpSession));
            }
            var options = new NativeBridge.ConnectOptions
            {
                Host = request.Host,
                Port = request.Port,
                Username = request.Username,
                ConnectTimeoutMs = request.ConnectTimeoutMs,
                KeepaliveSeconds = request.KeepaliveSeconds,
                TermType = request.TermType,
                Cols = request.Cols,
                Rows = request.Rows,
                JumpSessionId = string.Empty,
            };
            if (request.Env != null)
            {
                var env = new System.Collections.Generic.Dictionary<string, string>();
                foreach (var pair in request.Env)
                {
                    env[pair.Key] = pair.Value;
                }
                options.Env = env;
            }
            int code = await _native.ConnectJumpAsync(options, jumpNative.Native).AsTask().ConfigureAwait(false);
            return (SshErrorCode)code;
        }


        public async Task<SshErrorCode> AuthenticatePasswordAsync(string password)
        {
            int code = await _native.AuthenticatePasswordAsync(password).AsTask().ConfigureAwait(false);
            return (SshErrorCode)code;
        }

        public async Task<SshErrorCode> AuthenticatePublicKeyAsync(byte[] privateKeyPem, string passphrase)
        {
            if (privateKeyPem == null)
            {
                throw new ArgumentNullException(nameof(privateKeyPem));
            }
            int code = await _native.AuthenticatePublicKeyAsync(privateKeyPem, passphrase)
                .AsTask().ConfigureAwait(false);
            return (SshErrorCode)code;
        }

        public async Task<SshErrorCode> AuthenticateKeyboardInteractiveAsync()
        {
            int code = await _native.AuthenticateKeyboardInteractiveAsync().AsTask().ConfigureAwait(false);
            return (SshErrorCode)code;
        }

        // K03：agent 认证——只传 keyId，私钥材料由 core 直接从 agent 托管内存
        // 中取，不经过 C# 层。未注入 agent 或 native 未受理时返回 InternalError。
        public async Task<SshErrorCode> AuthenticateAgentAsync(string keyId)
        {
            NativeSshAgent agent = _agent;
            if (agent == null || agent.Native == null)
            {
                return SshErrorCode.InternalError;
            }
            int code = await _native.AuthenticateAgentAsync(agent.Native, keyId)
                .AsTask().ConfigureAwait(false);
            return (SshErrorCode)code;
        }

        public async Task<SshErrorCode> OpenShellAsync(int cols, int rows)
        {
            int code = await _native.OpenShellAsync(cols, rows).AsTask().ConfigureAwait(false);
            return (SshErrorCode)code;
        }

        public async Task<SshExecResult> ExecAsync(string command)
        {
            NativeBridge.ExecResult result = await _native.ExecAsync(command).AsTask().ConfigureAwait(false);
            return new SshExecResult(result.ExitCode, result.Stdout, result.Stderr);
        }

        public void Write(byte[] data)
        {
            if (data != null)
            {
                _native.Write(data);
            }
        }

        public void Resize(int cols, int rows)
        {
            _native.Resize(cols, rows);
        }

        public void ProbeNow()
        {
            _native.ProbeNow();
        }

        public void Close()
        {
            _native.Close(); // native 侧幂等
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _native.StateChanged -= OnStateChanged;
            _native.HostKeyCheck -= OnHostKeyCheck;
            _native.AuthPrompt -= OnAuthPrompt;
            _native.ContentDirty -= OnContentDirty;
            ((IDisposable)_native).Dispose(); // 内含 Close + 停线程
        }

        // ---------------------------------------------------------------- native 事件转发（I/O 线程）

        private void OnStateChanged(object sender, NativeBridge.StateChangedEventArgs e)
        {
            StateChanged?.Invoke(this,
                new SessionStateChangedEventArgs(MapState(e.State), (SshErrorCode)e.ErrorCode, e.Detail));
        }

        private void OnHostKeyCheck(object sender, NativeBridge.HostKeyCheckEventArgs e)
        {
            EventHandler<HostKeyCheckEventArgs> handler = HostKeyCheck;
            if (handler == null)
            {
                e.Reject(); // Core 侧无人编排 known_hosts：fail-closed
                return;
            }
            // 作答前持有 Deferral：native 决策窗挂起期间不计时（用户思考时间
            // 不受 60 s 限制）；作答后 Complete 交还。处理方必须最终作答，
            // 否则会话停留在 Handshaking（见 §6.2 注记）。
            Windows.Foundation.Deferral deferral = e.GetDeferral();
            var info = new HostKeyInfo(e.Info.KeyType, e.Info.FingerprintSha256, e.Info.RandomArt);
            var args = new HostKeyCheckEventArgs(info, accept =>
            {
                if (accept)
                {
                    e.Accept();
                }
                else
                {
                    e.Reject();
                }
                deferral.Complete();
            });
            try
            {
                handler(this, args);
            }
            catch
            {
                if (!args.IsDecided)
                {
                    args.Reject();
                }
                throw;
            }
        }

        private void OnAuthPrompt(object sender, NativeBridge.AuthPromptEventArgs e)
        {
            EventHandler<AuthPromptEventArgs> handler = AuthPrompt;
            if (handler == null)
            {
                e.Cancel(); // 空答复：服务器拒绝本轮，core 报 203
                return;
            }
            var args = new AuthPromptEventArgs(e.Name, e.Instruction,
                new List<string>(e.Prompts), new List<bool>(e.Echo),
                (answered, answers) =>
                {
                    if (answered)
                    {
                        e.Respond(new List<string>(answers));
                    }
                    else
                    {
                        e.Cancel();
                    }
                });
            try
            {
                handler(this, args);
            }
            catch
            {
                if (!args.IsDecided)
                {
                    args.Cancel();
                }
                throw;
            }
        }

        private void OnContentDirty(object sender, object args)
        {
            ContentDirty?.Invoke(this, EventArgs.Empty);
        }

        private static SessionStateKind MapState(NativeBridge.SessionState state)
        {
            switch (state)
            {
                case NativeBridge.SessionState.Idle: return SessionStateKind.Idle;
                case NativeBridge.SessionState.Connecting: return SessionStateKind.Connecting;
                case NativeBridge.SessionState.Handshaking: return SessionStateKind.Handshaking;
                case NativeBridge.SessionState.Authenticating: return SessionStateKind.Authenticating;
                case NativeBridge.SessionState.Established: return SessionStateKind.Established;
                case NativeBridge.SessionState.Disconnected: return SessionStateKind.Disconnected;
                case NativeBridge.SessionState.Error: return SessionStateKind.Error;
                default: return SessionStateKind.Error;
            }
        }
    }
}
