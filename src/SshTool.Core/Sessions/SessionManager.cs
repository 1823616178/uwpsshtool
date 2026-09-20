using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Hosts;
using SshTool.Core.Models;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;

namespace SshTool.Core.Sessions
{
    public sealed class SessionManager : IHostStatusProvider
    {
        private readonly HostRepository _hosts;
        private readonly KnownHostRepository _knownHosts;
        private readonly KeyRepository _keys;
        private readonly ISecretStore _secrets;
        private readonly SettingsRepository _settings;
        private readonly ISshSessionFactory _factory;
        private readonly IHostKeyPrompter _hostKeys;
        private readonly ICredentialPrompter _credentials;
        private readonly ITimerFactory _timers;
        private readonly IUiDispatcher _ui;
        private readonly ILogger _logger;
        private readonly ISshAgent _agent;
        private readonly List<SessionInfo> _sessions = new List<SessionInfo>();
        private readonly Dictionary<string, IDisposable> _reconnectTimers =
            new Dictionary<string, IDisposable>(StringComparer.Ordinal);

        public SessionManager(
            HostRepository hosts,
            KnownHostRepository knownHosts,
            KeyRepository keys,
            ISecretStore secrets,
            SettingsRepository settings,
            ISshSessionFactory factory,
            IHostKeyPrompter hostKeys,
            ICredentialPrompter credentials,
            ITimerFactory timers,
            IUiDispatcher ui,
            ILogger logger = null,
            ISshAgent agent = null)
        {
            if (hosts == null) throw new ArgumentNullException("hosts");
            if (knownHosts == null) throw new ArgumentNullException("knownHosts");
            if (keys == null) throw new ArgumentNullException("keys");
            if (secrets == null) throw new ArgumentNullException("secrets");
            if (settings == null) throw new ArgumentNullException("settings");
            if (factory == null) throw new ArgumentNullException("factory");
            if (hostKeys == null) throw new ArgumentNullException("hostKeys");
            if (credentials == null) throw new ArgumentNullException("credentials");
            if (timers == null) throw new ArgumentNullException("timers");
            if (ui == null) throw new ArgumentNullException("ui");
            _hosts = hosts;
            _knownHosts = knownHosts;
            _keys = keys;
            _secrets = secrets;
            _settings = settings;
            _factory = factory;
            _hostKeys = hostKeys;
            _credentials = credentials;
            _timers = timers;
            _ui = ui;
            _logger = logger;
            _agent = agent;
            if (_agent != null)
            {
                _agent.SetTimeout(ReadAgentTimeoutMinutes());
            }
            _timers.SchedulePeriodic(1000, TickReconnectCountdown);
        }

        public event EventHandler SessionsChanged;

        public IReadOnlyList<SessionInfo> Sessions
        {
            get { return _sessions; }
        }

        public int ActiveSessionCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _sessions.Count; i++)
                {
                    if (_sessions[i].State != SessionUiState.Closed)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        public async Task<SessionInfo> OpenAsync(SessionOpenRequest request)
        {
            SessionInfo info = await StartOpenAsync(request).ConfigureAwait(false);
            while (info.State == SessionUiState.Connecting || info.State == SessionUiState.Authenticating)
            {
                await Task.Delay(5).ConfigureAwait(false);
            }
            return info;
        }

        public async Task<SessionInfo> StartOpenAsync(SessionOpenRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException("request");
            }
            Host host = null;
            string hostName;
            int port;
            string user;
            string title;
            if (!string.IsNullOrEmpty(request.HostId))
            {
                host = await _hosts.GetByIdAsync(request.HostId).ConfigureAwait(false);
                if (host == null)
                {
                    throw new InvalidOperationException("主机不存在: " + request.HostId);
                }
                hostName = host.HostName;
                port = host.Port;
                user = host.Username;
                title = string.IsNullOrEmpty(host.Name) ? user + "@" + hostName : host.Name;
            }
            else if (request.QuickConnect != null)
            {
                hostName = request.QuickConnect.HostName;
                port = request.QuickConnect.Port;
                user = request.QuickConnect.Username;
                title = user + "@" + hostName + ":" + port.ToString();
            }
            else
            {
                throw new ArgumentException("需要 hostId 或 quickConnect");
            }

            var info = new SessionInfo
            {
                SessionId = IdGenerator.NewId(),
                HostId = host == null ? null : host.Id,
                Title = title,
                State = SessionUiState.Connecting,
                Cols = request.Cols < 1 ? 80 : request.Cols,
                Rows = request.Rows < 1 ? 24 : request.Rows,
                HostName = hostName,
                Port = port,
                Username = user,
                ShellOpened = request.OpenShell
            };
            // D06/WRONG_THREAD：ConnectCoreAsync 后续在线程池里改 info.*；
            // 字段同步更新、PropertyChanged 经 _ui 回 UI 线程，否则 XAML 绑定
            //（如 StatusDot 的 Ellipse）会在后台线程触碰 STA 对象而抛 0x8001010E。
            info.SetDispatcherPost(_ui.Post);
            _sessions.Add(info);
            RaiseChanged();
            var ignore = ConnectCoreAsync(info, host, false).ContinueWith(t =>
            {
                if (t.IsFaulted && !info.UserClosed
                    && (info.State == SessionUiState.Connecting || info.State == SessionUiState.Authenticating))
                {
                    Fail(info, SshErrorCode.InternalError, false);
                }
            });
            return info;
        }

        public void Close(string sessionId)
        {
            SessionInfo info = Find(sessionId);
            if (info == null)
            {
                return;
            }
            info.UserClosed = true;
            CancelTimer(info.SessionId);
            Detach(info);
            if (info.NativeSession != null)
            {
                info.NativeSession.Close();
                info.NativeSession.Dispose();
                info.NativeSession = null;
            }
            info.State = SessionUiState.Closed;
            _sessions.Remove(info);
            RaiseChanged();
        }

        public void CloseAll()
        {
            SessionInfo[] copy = _sessions.ToArray();
            for (int i = 0; i < copy.Length; i++)
            {
                Close(copy[i].SessionId);
            }
        }

        public void ReconnectNow(string sessionId)
        {
            SessionInfo info = Find(sessionId);
            if (info == null || info.UserClosed)
            {
                return;
            }
            CancelTimer(info.SessionId);
            info.ReconnectInSeconds = 0;
            var ignore = ReconnectAsync(info);
        }

        public void CancelReconnect(string sessionId)
        {
            SessionInfo info = Find(sessionId);
            if (info == null)
            {
                return;
            }
            CancelTimer(info.SessionId);
            info.State = SessionUiState.Disconnected;
            RaiseChanged();
        }

        // P01：按后台策略挂起全部活会话（01-DESIGN.md §10；对齐鸿蒙端
        // suspendAllForPolicy）：只断连接、不销毁面孔——SessionInfo 与窗格绑定原样
        // 保留，状态置 Disconnected、错误码 405、原因写入 ErrorMessage，回前台由
        // ResumeSuspended 原地重连（SessionId 不变、native 句柄换新）。
        // 跳过：已关闭/用户已关/已挂起/已是终态（无 native 又无重连计时器，如用户取消
        // 重连或认证失败——不覆写其终态文案）。幂等，返回被挂起的会话数。
        // 日志只记计数（reason 是固定文案，安全）。
        public int SuspendAllForPolicy(string reason)
        {
            int count = 0;
            for (int i = 0; i < _sessions.Count; i++)
            {
                SessionInfo info = _sessions[i];
                if (info.UserClosed || info.PolicySuspended
                    || info.State == SessionUiState.Closed || !IsAliveForPolicy(info))
                {
                    continue;
                }
                CancelTimer(info.SessionId);
                if (info.NativeSession != null)
                {
                    ISshSession native = info.NativeSession;
                    info.NativeSession = null;
                    native.Close();
                    native.Dispose();
                }
                info.PolicySuspended = true;
                info.State = SessionUiState.Disconnected;
                info.ErrorCode = SshErrorCode.PolicyDisconnect;
                info.ErrorMessage = reason ?? string.Empty;
                info.ReconnectAttempt = 0;
                info.ReconnectInSeconds = 0;
                count++;
            }
            if (count > 0)
            {
                if (_logger != null)
                {
                    _logger.Log(LogLevel.Info, "Session", "policy suspend " + count.ToString());
                }
                RaiseChanged();
            }
            return count;
        }

        // P01：恢复被策略挂起的会话（回前台/亮屏）：逐条重新拨号。
        // 策略性断开不是链路失败，重连计数从零起算，不消耗重试预算。
        // 幂等，返回被恢复的会话数。
        public int ResumeSuspended()
        {
            int count = 0;
            for (int i = 0; i < _sessions.Count; i++)
            {
                SessionInfo info = _sessions[i];
                if (!info.PolicySuspended || info.UserClosed
                    || info.State == SessionUiState.Closed)
                {
                    continue;
                }
                info.PolicySuspended = false;
                info.ReconnectAttempt = 0;
                info.ReconnectInSeconds = 0;
                info.ErrorCode = SshErrorCode.None;
                info.ErrorMessage = string.Empty;
                count++;
                ReconnectNow(info.SessionId);
            }
            if (count > 0 && _logger != null)
            {
                _logger.Log(LogLevel.Info, "Session", "policy resume " + count.ToString());
            }
            return count;
        }

        // P01：对仍在线（Connected 且 native 仍在）的会话做一次主动探测
        //（回前台收敛、切网收敛共用；P02 的 NetworkMonitor 复用本方法）。
        // 返回被探测的会话数。
        public int ProbeAll()
        {
            int count = 0;
            for (int i = 0; i < _sessions.Count; i++)
            {
                SessionInfo info = _sessions[i];
                if (info.PolicySuspended || info.UserClosed
                    || info.State != SessionUiState.Connected || info.NativeSession == null)
                {
                    continue;
                }
                info.NativeSession.ProbeNow();
                count++;
            }
            if (count > 0 && _logger != null)
            {
                _logger.Log(LogLevel.Info, "Session", "probe " + count.ToString());
            }
            return count;
        }

        // P02：默认网变化收敛（对齐鸿蒙端 SessionManager.notifyNetworkChanged，
        // 见 01-DESIGN.md §10；调用方是 App 层 NetworkMonitor→LifecycleService，
        // 已过 1 s 防抖与「适配器 id + 连接级别」判据）：
        //   - 退避倒计时中的：撤掉倒计时立即重连（新网络多半已经可用）；
        //   - 已连接的：ProbeNow() 开 5 s 判定窗口，黑洞即走既有重连链；
        //   - 策略挂起中的不动（回前台统一恢复）；终态（用户取消/不可重试/耗尽）
        //     与正在拨号中的不动（新拨号自己会走新网络）。
        // 返回被处理的会话数。日志只记计数。
        public int OnNetworkChanged()
        {
            int affected = 0;
            SessionInfo[] copy = _sessions.ToArray();
            for (int i = 0; i < copy.Length; i++)
            {
                SessionInfo info = copy[i];
                if (info.UserClosed || info.PolicySuspended
                    || info.State == SessionUiState.Closed)
                {
                    continue;
                }
                if (_reconnectTimers.ContainsKey(info.SessionId))
                {
                    ReconnectNow(info.SessionId);
                    affected++;
                    continue;
                }
                if (info.State != SessionUiState.Connected || info.NativeSession == null)
                {
                    continue;
                }
                info.NativeSession.ProbeNow();
                affected++;
            }
            if (affected > 0 && _logger != null)
            {
                _logger.Log(LogLevel.Info, "Session", "network changed affected=" + affected.ToString());
            }
            return affected;
        }

        // P01：Suspending 时标记「挂起前在线」的会话（01-DESIGN.md §10）。
        public void MarkOnlineBeforeSuspend()
        {
            for (int i = 0; i < _sessions.Count; i++)
            {
                SessionInfo info = _sessions[i];
                info.WasOnlineBeforeSuspend = !info.UserClosed && !info.PolicySuspended
                    && info.State != SessionUiState.Closed
                    && (info.State == SessionUiState.Connected
                        || info.State == SessionUiState.Reconnecting);
            }
        }

        // P01：Resuming/回前台处理完成后清除标记。
        public void ClearSuspendMarks()
        {
            for (int i = 0; i < _sessions.Count; i++)
            {
                _sessions[i].WasOnlineBeforeSuspend = false;
            }
        }

        private bool IsAliveForPolicy(SessionInfo info)
        {
            // 终态（用户取消重连 / 不可重试的失败 / 重试耗尽）且无待发计时器：
            // 尊重其终态文案，不覆写为 405。
            if ((info.State == SessionUiState.Disconnected || info.State == SessionUiState.Error)
                && !_reconnectTimers.ContainsKey(info.SessionId))
            {
                return false;
            }
            if (info.NativeSession == null && !_reconnectTimers.ContainsKey(info.SessionId))
            {
                return false;
            }
            return true;
        }

        public HostListStatus GetStatus(string hostId)
        {
            HostListStatus best = HostListStatus.None;
            for (int i = 0; i < _sessions.Count; i++)
            {
                if (!string.Equals(_sessions[i].HostId, hostId, StringComparison.Ordinal))
                {
                    continue;
                }
                HostListStatus s = ToStatus(_sessions[i].State);
                if ((int)s > (int)best)
                {
                    best = s;
                }
            }
            return best;
        }

        private async Task ConnectCoreAsync(SessionInfo info, Host host, bool reconnect)
        {
            ISshSession native = _factory.Create();
            ISshSession old = info.NativeSession;
            info.NativeSession = native;
            if (old != null)
            {
                Unhook(old);
                old.Close();
                old.Dispose();
            }
            Hook(info, native);
            info.State = reconnect ? SessionUiState.Reconnecting : SessionUiState.Connecting;
            RaiseChanged();

            KnownHost known = await FindKnownAsync(info.HostName, info.Port).ConfigureAwait(false);
            string hostFp = host == null ? null : host.HostFingerprint;
            info.AcceptedKey = null;
            info.WriteKnownHost = false;

            EventHandler<HostKeyCheckEventArgs> onKey = (s, e) => HandleHostKey(info, known, hostFp, e);
            native.HostKeyCheck += onKey;

            var req = new SshConnectRequest
            {
                Host = info.HostName,
                Port = info.Port,
                Username = info.Username,
                ConnectTimeoutMs = _settings.ConnectTimeoutSeconds * 1000,
                KeepaliveSeconds = host == null ? 30 : host.Keepalive,
                TermType = host == null || string.IsNullOrEmpty(host.TermType) ? "xterm-256color" : host.TermType,
                Cols = info.Cols,
                Rows = info.Rows,
                Env = host == null ? null : host.EnvVars
            };
            SshErrorCode code = await native.ConnectAsync(req).ConfigureAwait(false);
            native.HostKeyCheck -= onKey;
            if (info.UserClosed)
            {
                return;
            }
            if (code != SshErrorCode.None)
            {
                Fail(info, code, false);
                return;
            }

            info.State = SessionUiState.Authenticating;
            RaiseChanged();
            code = await AuthenticateAsync(info, host, native).ConfigureAwait(false);
            if (info.UserClosed)
            {
                return;
            }
            if (code != SshErrorCode.None)
            {
                Fail(info, code, false);
                return;
            }

            // F03：无 shell 用途（SFTP 专用连接）不开 shell、不发 AutoRun，
            // 认证通过即视为已连接；重连保持原会话形态（info.ShellOpened）。
            if (info.ShellOpened)
            {
                code = await native.OpenShellAsync(info.Cols, info.Rows).ConfigureAwait(false);
                if (info.UserClosed)
                {
                    return;
                }
                if (code != SshErrorCode.None)
                {
                    Fail(info, code, true);
                    return;
                }

                // U10：每次（含重连）shell 打开后发送自动执行命令，靠 tmux 附着找回现场。
                SendAutoRun(native, host);
            }

            info.State = SessionUiState.Connected;
            info.ErrorCode = SshErrorCode.None;
            info.ReconnectAttempt = 0;
            info.ReconnectInSeconds = 0;
            info.ConnectedAt = DateTime.UtcNow;
            CancelTimer(info.SessionId);
            RaiseChanged();
            await AfterSuccessAsync(info, host).ConfigureAwait(false);
        }

        private void SendAutoRun(ISshSession native, Host host)
        {
            IList<string> commands = AutoRun.BuildCommands(host);
            if (commands.Count == 0)
            {
                return;
            }
            for (int i = 0; i < commands.Count; i++)
            {
                native.Write(Encoding.UTF8.GetBytes(commands[i] + "\r"));
            }
            if (_logger != null)
            {
                _logger.Log(LogLevel.Info, "Session", "autorun " + commands.Count.ToString());
            }
        }

        private void HandleHostKey(SessionInfo info, KnownHost known, string hostFp, HostKeyCheckEventArgs e)
        {
            HostKeyVerdict verdict = HostKeyVerifier.Verify(known, hostFp, e.Info);
            if (verdict.Kind == HostKeyVerdictKind.Accept)
            {
                info.AcceptedKey = e.Info;
                info.WriteKnownHost = verdict.WriteKnownHost;
                e.Accept();
                return;
            }
            if (verdict.Kind == HostKeyVerdictKind.RejectMismatch)
            {
                e.Reject();
                var ignoreMismatch = _ui.RunAsync(() => _hostKeys.PromptMismatchAsync(
                    e.Info, info.Title, known == null ? hostFp : known.FingerprintSha256));
                return;
            }
            _ui.Post(() =>
            {
                var ignore = PromptUnknownAsync(info, e);
            });
        }

        private async Task PromptUnknownAsync(SessionInfo info, HostKeyCheckEventArgs e)
        {
            bool ok = await _hostKeys.PromptUnknownAsync(e.Info, info.Title).ConfigureAwait(true);
            if (ok)
            {
                info.AcceptedKey = e.Info;
                info.WriteKnownHost = true;
                e.Accept();
            }
            else
            {
                e.Reject();
            }
        }

        private async Task<SshErrorCode> AuthenticateAsync(SessionInfo info, Host host, ISshSession native)
        {
            EventHandler<AuthPromptEventArgs> onPrompt = (s, e) =>
            {
                _ui.Post(() =>
                {
                    var ignore = AnswerKiAsync(e);
                });
            };
            native.AuthPrompt += onPrompt;
            try
            {
                AuthType type = host == null ? AuthType.Password : host.AuthType;
                if (type == AuthType.Key)
                {
                    return await AuthenticateKeyAsync(host, native).ConfigureAwait(false);
                }
                if (type == AuthType.Agent)
                {
                    return await AuthenticateAgentAsync(info, host, native).ConfigureAwait(false);
                }
                return await AuthenticatePasswordAsync(info, host, native).ConfigureAwait(false);
            }
            finally
            {
                native.AuthPrompt -= onPrompt;
            }
        }

        private async Task AnswerKiAsync(AuthPromptEventArgs e)
        {
            IReadOnlyList<string> answers = await _credentials.PromptKeyboardInteractiveAsync(e).ConfigureAwait(true);
            if (answers == null)
            {
                e.Cancel();
            }
            else
            {
                e.Respond(answers);
            }
        }

        private async Task<SshErrorCode> AuthenticatePasswordAsync(SessionInfo info, Host host, ISshSession native)
        {
            int attempts = 0;
            string stored = host == null ? null : await _secrets.GetAsync(SecretKeys.HostPassword(host.Id)).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(stored))
            {
                SshErrorCode storedCode = await native.AuthenticatePasswordAsync(stored).ConfigureAwait(false);
                attempts++;
                if (storedCode == SshErrorCode.None)
                {
                    return SshErrorCode.None;
                }
                if (storedCode == SshErrorCode.AuthKeyboardInteractiveFailed)
                {
                    return await native.AuthenticateKeyboardInteractiveAsync().ConfigureAwait(false);
                }
                if (storedCode != SshErrorCode.AuthPasswordFailed)
                {
                    return storedCode;
                }
            }

            string error = null;
            while (attempts < 3)
            {
                PasswordPromptResult prompt = await _ui.RunAsync(
                    () => _credentials.PromptPasswordAsync(info.Title, error)).ConfigureAwait(false);
                if (prompt == null || prompt.Cancelled)
                {
                    return SshErrorCode.NoLocalCredential;
                }
                SshErrorCode code = await native.AuthenticatePasswordAsync(prompt.Password).ConfigureAwait(false);
                attempts++;
                if (code == SshErrorCode.None)
                {
                    if (prompt.Remember && host != null)
                    {
                        await _secrets.SetAsync(SecretKeys.HostPassword(host.Id), prompt.Password).ConfigureAwait(false);
                    }
                    return SshErrorCode.None;
                }
                if (code == SshErrorCode.AuthKeyboardInteractiveFailed)
                {
                    return await native.AuthenticateKeyboardInteractiveAsync().ConfigureAwait(false);
                }
                if (code != SshErrorCode.AuthPasswordFailed)
                {
                    return code;
                }
                error = "密码错误，还可尝试 " + (3 - attempts).ToString() + " 次";
            }
            return SshErrorCode.AuthPasswordFailed;
        }

        private async Task<SshErrorCode> AuthenticateKeyAsync(Host host, ISshSession native)
        {
            if (host == null || string.IsNullOrEmpty(host.KeyId))
            {
                return SshErrorCode.NoLocalCredential;
            }
            KeyEntry key = await _keys.GetByIdAsync(host.KeyId).ConfigureAwait(false);
            if (key == null)
            {
                return SshErrorCode.NoLocalCredential;
            }
            string pem = await _secrets.GetAsync(SecretKeys.KeyPrivate(key.Id)).ConfigureAwait(false);
            if (string.IsNullOrEmpty(pem))
            {
                return SshErrorCode.NoLocalCredential;
            }
            string pass = await _secrets.GetAsync(SecretKeys.KeyPassphrase(key.Id)).ConfigureAwait(false);
            if (key.Encrypted && string.IsNullOrEmpty(pass))
            {
                pass = await _ui.RunAsync(() => _credentials.PromptPassphraseAsync(key.Name)).ConfigureAwait(false);
            }
            byte[] bytes = Encoding.UTF8.GetBytes(pem);
            return await native.AuthenticatePublicKeyAsync(bytes, pass ?? string.Empty).ConfigureAwait(false);
        }

        // K03：应用内 agent 认证（01-DESIGN.md §12.1）：
        //   1. 每次认证前按设置项 agentKeyTimeoutMinutes 对齐超时；
        //   2. 依次尝试已解锁密钥（host.KeyId 指定时只试它，否则试全部本机密钥中
        //      已解锁的）：成功即返回；204（短语错误）→ lock 掉该条目再试下一个
        //      （有效性只能在首次认证时验证，见 native agent.h 头注）；202（服务
        //      器拒绝该密钥）→ 试下一个；KI 回退与密码流一致；
        //   3. 无可用密钥（均锁定或全部失败）→ 提示选择密钥解锁，解锁后只试这
        //      一把（204 同样 lock 掉并返回，不循环弹框）。
        // 私钥明文只在 SecretStore→agent 的单程中短暂存在，C# 侧不保留。
        private async Task<SshErrorCode> AuthenticateAgentAsync(
            SessionInfo info, Host host, ISshSession native)
        {
            ISshAgent agent = _agent;
            if (agent == null)
            {
                return SshErrorCode.NoLocalCredential;
            }
            agent.SetTimeout(ReadAgentTimeoutMinutes());

            List<string> candidates = new List<string>();
            if (host != null && !string.IsNullOrEmpty(host.KeyId))
            {
                candidates.Add(host.KeyId);
            }
            else
            {
                IReadOnlyList<KeyEntry> all = await _keys.GetAllAsync().ConfigureAwait(false);
                for (int i = 0; i < all.Count; i++)
                {
                    if (!string.IsNullOrEmpty(all[i].Id))
                    {
                        candidates.Add(all[i].Id);
                    }
                }
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                string keyId = candidates[i];
                if (agent.IsLocked(keyId))
                {
                    continue;
                }
                SshErrorCode code = await native.AuthenticateAgentAsync(keyId).ConfigureAwait(false);
                if (code == SshErrorCode.None)
                {
                    return SshErrorCode.None;
                }
                if (code == SshErrorCode.AuthKeyboardInteractiveFailed)
                {
                    return await native.AuthenticateKeyboardInteractiveAsync().ConfigureAwait(false);
                }
                if (code == SshErrorCode.PrivateKeyLoadFailed)
                {
                    agent.Lock(keyId);
                    continue;
                }
                if (code == SshErrorCode.AuthPublicKeyFailed)
                {
                    continue;
                }
                return code;
            }

            AgentUnlockAttempt attempt =
                await PromptUnlockAndStoreAsync(info, host, agent).ConfigureAwait(false);
            if (attempt.Cancelled || string.IsNullOrEmpty(attempt.KeyId))
            {
                return attempt.Cancelled ? SshErrorCode.NoLocalCredential : SshErrorCode.PrivateKeyLoadFailed;
            }
            string unlockedId = attempt.KeyId;
            SshErrorCode finalCode = await native.AuthenticateAgentAsync(unlockedId).ConfigureAwait(false);
            if (finalCode == SshErrorCode.PrivateKeyLoadFailed)
            {
                agent.Lock(unlockedId);
            }
            else if (finalCode == SshErrorCode.AuthKeyboardInteractiveFailed)
            {
                return await native.AuthenticateKeyboardInteractiveAsync().ConfigureAwait(false);
            }
            return finalCode;
        }

        // K03：无可用密钥时的解锁流程（见 AgentUnlockAttempt 注释）。
        // 对话框只做选择；短语复用 PromptPassphraseAsync；私钥/短语送入 agent 后
        // 立即丢引用（string 无法清零，尽量缩短存活，见 §6.4 纪律）。
        // K03：无可用密钥时的解锁流程。Cancelled = 用户取消/无选择/无材料；
        // KeyId 非空 = 成功托管（随后只试这一把）。解锁被拒（格式非法）时
        // Cancelled=false 且 KeyId 为空 → 调用方报 204（本地密钥不可用）。
        private sealed class AgentUnlockAttempt
        {
            public string KeyId;
            public bool Cancelled;
        }

        private async Task<AgentUnlockAttempt> PromptUnlockAndStoreAsync(
            SessionInfo info, Host host, ISshAgent agent)
        {
            IReadOnlyList<KeyEntry> all = await _keys.GetAllAsync().ConfigureAwait(false);
            if (all.Count == 0)
            {
                return new AgentUnlockAttempt { Cancelled = true };
            }
            // host.KeyId 指定的密钥排首位，其余保持仓库顺序。
            List<AgentKeyChoice> choices = new List<AgentKeyChoice>(all.Count);
            if (host != null && !string.IsNullOrEmpty(host.KeyId))
            {
                AddChoiceIfExists(choices, all, host.KeyId, agent);
            }
            for (int i = 0; i < all.Count; i++)
            {
                bool dup = false;
                for (int j = 0; j < choices.Count; j++)
                {
                    if (string.Equals(choices[j].KeyId, all[i].Id, StringComparison.Ordinal))
                    {
                        dup = true;
                        break;
                    }
                }
                if (!dup)
                {
                    AddChoiceIfExists(choices, all, all[i].Id, agent);
                }
            }
            if (choices.Count == 0)
            {
                return new AgentUnlockAttempt { Cancelled = true };
            }
            AgentUnlockResult pick = await _ui.RunAsync(
                () => _credentials.PromptAgentUnlockAsync(choices, info.Title)).ConfigureAwait(false);
            if (pick == null || pick.Cancelled || string.IsNullOrEmpty(pick.KeyId))
            {
                return new AgentUnlockAttempt { Cancelled = true };
            }
            KeyEntry picked = null;
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i].Id, pick.KeyId, StringComparison.Ordinal))
                {
                    picked = all[i];
                    break;
                }
            }
            if (picked == null)
            {
                return new AgentUnlockAttempt { Cancelled = true };
            }
            string pem = await _secrets.GetAsync(SecretKeys.KeyPrivate(picked.Id)).ConfigureAwait(false);
            if (string.IsNullOrEmpty(pem))
            {
                return new AgentUnlockAttempt { Cancelled = true };
            }
            string pass = await _secrets.GetAsync(SecretKeys.KeyPassphrase(picked.Id)).ConfigureAwait(false);
            if (picked.Encrypted && string.IsNullOrEmpty(pass))
            {
                pass = await _ui.RunAsync(
                    () => _credentials.PromptPassphraseAsync(picked.Name)).ConfigureAwait(false);
                if (pass == null)
                {
                    return new AgentUnlockAttempt { Cancelled = true };
                }
            }
            bool ok = await agent.UnlockAsync(picked.Id, pem, pass ?? string.Empty).ConfigureAwait(false);
            if (_logger != null)
            {
                _logger.Log(LogLevel.Info, "Session", "agent unlock " + (ok ? "ok" : "rejected"));
            }
            if (!ok)
            {
                return new AgentUnlockAttempt { Cancelled = false };
            }
            return new AgentUnlockAttempt { KeyId = picked.Id };
        }

        private static void AddChoiceIfExists(
            List<AgentKeyChoice> choices, IReadOnlyList<KeyEntry> all, string keyId, ISshAgent agent)
        {
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i].Id, keyId, StringComparison.Ordinal))
                {
                    choices.Add(new AgentKeyChoice
                    {
                        KeyId = all[i].Id,
                        Name = all[i].Name,
                        KeyType = all[i].KeyType,
                        FingerprintSha256 = all[i].FingerprintSha256,
                        Unlocked = !agent.IsLocked(all[i].Id)
                    });
                    return;
                }
            }
        }

        // K03：应用挂起时清除 agent 内存（§12.1）。幂等；返回清除前托管条数
        // （仅计数，无敏感内容）。由 LifecycleService.OnSuspending 调用。
        public int LockAgentKeys()
        {
            ISshAgent agent = _agent;
            if (agent == null)
            {
                return 0;
            }
            int count = agent.KeyCount;
            agent.LockAll();
            if (_logger != null)
            {
                _logger.Log(LogLevel.Info, "Session", "agent locked " + count.ToString());
            }
            return count;
        }

        private int ReadAgentTimeoutMinutes()
        {
            int minutes = 15;
            try
            {
                minutes = _settings.AgentKeyTimeoutMinutes;
            }
            catch (Exception)
            {
            }
            return minutes < 0 ? 15 : minutes;
        }

        private async Task AfterSuccessAsync(SessionInfo info, Host host)
        {
            if (info.WriteKnownHost && info.AcceptedKey != null)
            {
                KnownHost existing = await FindKnownAsync(info.HostName, info.Port).ConfigureAwait(false);
                if (existing == null)
                {
                    var kh = new KnownHost
                    {
                        Id = IdGenerator.NewId(),
                        Host = info.HostName,
                        Port = info.Port,
                        KeyType = info.AcceptedKey.KeyType,
                        FingerprintSha256 = info.AcceptedKey.FingerprintSha256,
                        AddedAt = IsoNow(),
                        LastSeenAt = IsoNow()
                    };
                    await _knownHosts.AddAsync(kh, ChangeOrigin.User).ConfigureAwait(false);
                }
                else
                {
                    existing.LastSeenAt = IsoNow();
                    existing.FingerprintSha256 = info.AcceptedKey.FingerprintSha256;
                    await _knownHosts.UpdateAsync(existing, ChangeOrigin.User).ConfigureAwait(false);
                }
            }
            if (host != null)
            {
                bool dirty = false;
                if (string.IsNullOrEmpty(host.HostFingerprint) && info.AcceptedKey != null)
                {
                    host.HostFingerprint = info.AcceptedKey.FingerprintSha256;
                    dirty = true;
                }
                host.LastConnectedAt = IsoNow();
                dirty = true;
                if (dirty)
                {
                    await _hosts.UpdateAsync(host, ChangeOrigin.User).ConfigureAwait(false);
                }
            }
        }

        private void Hook(SessionInfo info, ISshSession native)
        {
            native.StateChanged += (s, e) => OnNativeState(info, native, e);
        }

        private void Unhook(ISshSession native)
        {
            // 迟到事件靠 sender 比对丢弃；无法轻易 -= 匿名，故以实例比对。
        }

        private void Detach(SessionInfo info)
        {
            info.NativeSession = null;
        }

        private void OnNativeState(SessionInfo info, ISshSession native, SessionStateChangedEventArgs e)
        {
            if (!ReferenceEquals(info.NativeSession, native) || info.UserClosed)
            {
                return;
            }
            _ui.Post(() => HandleNativeState(info, e));
        }

        private void HandleNativeState(SessionInfo info, SessionStateChangedEventArgs e)
        {
            if (info.UserClosed || info.State == SessionUiState.Closed)
            {
                return;
            }
            if (e.State == SessionStateKind.Established)
            {
                return;
            }
            if (e.State == SessionStateKind.Disconnected || e.State == SessionStateKind.Error)
            {
                Fail(info, e.ErrorCode == SshErrorCode.None ? SshErrorCode.RemoteClosed : e.ErrorCode, true);
            }
        }

        private void Fail(SessionInfo info, SshErrorCode code, bool allowReconnect)
        {
            info.ErrorCode = code;
            info.ErrorMessage = code.ToString();
            if (info.UserClosed)
            {
                return;
            }
            if (_logger != null)
            {
                _logger.Log(LogLevel.Info, "Session", "state " + info.State + " code " + ((int)code).ToString());
            }
            if (allowReconnect && ReconnectScheduler.ShouldReconnect(code)
                && info.ReconnectAttempt < _settings.ReconnectMaxAttempts)
            {
                ScheduleReconnect(info);
                return;
            }
            info.State = ReconnectScheduler.ShouldReconnect(code)
                ? SessionUiState.Disconnected
                : SessionUiState.Error;
            RaiseChanged();
        }

        private void ScheduleReconnect(SessionInfo info)
        {
            CancelTimer(info.SessionId);
            info.State = SessionUiState.Reconnecting;
            info.ReconnectAttempt = info.ReconnectAttempt + 1;
            int delay = ReconnectScheduler.DelaySeconds(info.ReconnectAttempt - 1);
            info.ReconnectInSeconds = delay;
            RaiseChanged();
            IDisposable timer = _timers.Schedule(delay * 1000, () =>
            {
                var ignore = ReconnectAsync(info);
            });
            _reconnectTimers[info.SessionId] = timer;
        }

        private async Task ReconnectAsync(SessionInfo info)
        {
            if (info.UserClosed || info.State == SessionUiState.Closed)
            {
                return;
            }
            Host host = string.IsNullOrEmpty(info.HostId)
                ? null
                : await _hosts.GetByIdAsync(info.HostId).ConfigureAwait(false);
            await ConnectCoreAsync(info, host, true).ConfigureAwait(false);
        }

        private void TickReconnectCountdown()
        {
            bool any = false;
            for (int i = 0; i < _sessions.Count; i++)
            {
                SessionInfo s = _sessions[i];
                if (s.State == SessionUiState.Reconnecting && s.ReconnectInSeconds > 0)
                {
                    s.ReconnectInSeconds = s.ReconnectInSeconds - 1;
                    any = true;
                }
            }
            if (any)
            {
                RaiseChanged();
            }
        }

        private void CancelTimer(string sessionId)
        {
            IDisposable timer;
            if (_reconnectTimers.TryGetValue(sessionId, out timer))
            {
                timer.Dispose();
                _reconnectTimers.Remove(sessionId);
            }
        }

        private SessionInfo Find(string sessionId)
        {
            for (int i = 0; i < _sessions.Count; i++)
            {
                if (_sessions[i].SessionId == sessionId)
                {
                    return _sessions[i];
                }
            }
            return null;
        }

        private async Task<KnownHost> FindKnownAsync(string hostName, int port)
        {
            IReadOnlyList<KnownHost> all = await _knownHosts.GetAllAsync().ConfigureAwait(false);
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i].Host, hostName, StringComparison.OrdinalIgnoreCase)
                    && all[i].Port == port)
                {
                    return all[i];
                }
            }
            return null;
        }

        private static HostListStatus ToStatus(SessionUiState state)
        {
            switch (state)
            {
                case SessionUiState.Connected:
                    return HostListStatus.Connected;
                case SessionUiState.Reconnecting:
                    return HostListStatus.Reconnecting;
                case SessionUiState.Connecting:
                case SessionUiState.Authenticating:
                    return HostListStatus.Connecting;
                case SessionUiState.Error:
                    return HostListStatus.Error;
                default:
                    return HostListStatus.None;
            }
        }

        private void RaiseChanged()
        {
            EventHandler handler = SessionsChanged;
            if (handler != null)
            {
                _ui.Post(() => handler(this, EventArgs.Empty));
            }
        }

        private static string IsoNow()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }
    }
}
