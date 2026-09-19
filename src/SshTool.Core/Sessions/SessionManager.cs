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
            ILogger logger = null)
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
                Username = user
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
                    return SshErrorCode.NoLocalCredential;
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
