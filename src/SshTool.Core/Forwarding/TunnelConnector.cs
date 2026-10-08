using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Sessions;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;

namespace SshTool.Core.Forwarding
{
    // fix/functional-pass：隧道建链失败的类别（App 侧按类别本地化，Core 不出界面文案）。
    public enum TunnelConnectFailure
    {
        None = 0,
        JumpPlanInvalid,
        UnknownHostKey,
        HostKeyMismatch,
        ConnectFailed,
        NoSavedCredential,
        AuthFailed
    }

    public sealed class TunnelConnectResult
    {
        public TunnelConnectFailure Failure { get; set; }
        public SshErrorCode Code { get; set; }
        // 失败发生在跳板时为该跳标题（"[1/2] jump"），目标主机失败为 null。
        public string FailedHop { get; set; }
        // 成功时：已认证的目标会话 + 外→内的跳板会话（须在目标之后释放）。
        public ISshSession Session { get; set; }
        public IReadOnlyList<ISshSession> JumpSessions { get; set; }

        public bool Success
        {
            get { return Failure == TunnelConnectFailure.None; }
        }
    }

    // fix/functional-pass：后台隧道的非交互建链（P1-3：此前 NativeForwarder 只直连目标，
    // 配了 ProxyJump 的主机隧道永远连不上）。与终端 SessionManager 一致：
    //   - JumpChainPlanner 规划链路，外层直连、内层经上一跳 direct-tcpip（ConnectJumpAsync）；
    //   - 每跳 HostKeyVerifier.VerifyNonInteractive：未知主机拒绝（须先在终端信任），钉住指纹
    //     命中则补写 known_hosts；
    //   - 认证只用已保存凭据（无任何弹框）：密码 / 私钥（+已存短语）/ agent。agent 与终端
    //     同样遍历：host.KeyId 指定时只试它，否则试全部本机密钥；锁定的密钥若凭据表里有
    //     私钥（加密钥还需已存短语）则先解锁进 agent；204 → lock 后试下一把，202 → 下一把。
    public sealed class TunnelConnector
    {
        public const int DefaultKeepaliveSeconds = 30;

        private readonly HostRepository _hosts;
        private readonly KnownHostRepository _knownHosts;
        private readonly KeyRepository _keys;
        private readonly ISecretStore _secrets;
        private readonly ISshAgent _agent;
        private readonly ISshSessionFactory _factory;
        private readonly ILogger _logger;

        public TunnelConnector(
            HostRepository hosts,
            KnownHostRepository knownHosts,
            KeyRepository keys,
            ISecretStore secrets,
            ISshAgent agent,
            ISshSessionFactory factory,
            ILogger logger)
        {
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }
            _hosts = hosts;
            _knownHosts = knownHosts;
            _keys = keys;
            _secrets = secrets;
            _agent = agent;
            _factory = factory;
            _logger = logger;
        }

        public async Task<TunnelConnectResult> ConnectAsync(Host target, int connectTimeoutMs, CancellationToken cancellation)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }
            JumpChainPlan plan;
            try
            {
                IReadOnlyList<Host> all = _hosts != null
                    ? await _hosts.GetAllAsync().ConfigureAwait(false)
                    : (IReadOnlyList<Host>)new Host[0];
                plan = JumpChainPlanner.Plan(target, all);
            }
            catch (InvalidOperationException ex)
            {
                Log(LogLevel.Warning, "tunnel jump plan error: " + ex.Message);
                return new TunnelConnectResult
                {
                    Failure = TunnelConnectFailure.JumpPlanInvalid,
                    Code = SshErrorCode.InternalError
                };
            }

            var hops = new List<ISshSession>();
            ISshSession previous = null;
            ISshSession current = null;
            try
            {
                for (int i = 0; i < plan.TotalHops; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    JumpHopInfo hop = plan.Hops[i];
                    bool isTarget = i == plan.TotalHops - 1;
                    current = _factory.Create();
                    TunnelConnectResult step = await ConnectHopAsync(
                        current, hop.Host, previous, isTarget, connectTimeoutMs, cancellation).ConfigureAwait(false);
                    if (!step.Success)
                    {
                        step.FailedHop = isTarget ? null : hop.Title;
                        Log(LogLevel.Warning, "tunnel hop " + (i + 1).ToString(CultureInfo.InvariantCulture)
                            + "/" + plan.TotalHops.ToString(CultureInfo.InvariantCulture) + " failed: " + step.Failure);
                        CloseSession(current);
                        current = null;
                        CloseAll(hops);
                        return step;
                    }
                    if (!isTarget)
                    {
                        hops.Add(current);
                        previous = current;
                        current = null;
                    }
                }
                ISshSession established = current;
                current = null;
                return new TunnelConnectResult
                {
                    Failure = TunnelConnectFailure.None,
                    Code = SshErrorCode.None,
                    Session = established,
                    JumpSessions = hops
                };
            }
            catch
            {
                CloseSession(current);
                CloseAll(hops);
                throw;
            }
        }

        // 释放目标会话（如有）与跳板会话（内→外）。幂等容错：单个释放异常不影响其余。
        public static void Close(TunnelConnectResult result)
        {
            if (result == null)
            {
                return;
            }
            CloseSession(result.Session);
            result.Session = null;
            if (result.JumpSessions != null)
            {
                CloseAll(new List<ISshSession>(result.JumpSessions));
            }
            result.JumpSessions = new ISshSession[0];
        }

        private async Task<TunnelConnectResult> ConnectHopAsync(
            ISshSession session, Host host, ISshSession via, bool isTarget, int connectTimeoutMs,
            CancellationToken cancellation)
        {
            int port = host.Port > 0 ? host.Port : 22;
            KnownHost known = _knownHosts != null
                ? await _knownHosts.FindAsync(host.HostName, port).ConfigureAwait(false)
                : null;
            string pinned = host.HostFingerprint;
            HostKeyVerdictKind? rejected = null;
            HostKeyInfo toSave = null;

            EventHandler<HostKeyCheckEventArgs> onKey = (s, e) =>
            {
                try
                {
                    HostKeyVerdict verdict = HostKeyVerifier.VerifyNonInteractive(known, pinned, e.Info);
                    if (verdict.Kind == HostKeyVerdictKind.Accept)
                    {
                        if (verdict.WriteKnownHost)
                        {
                            toSave = e.Info;
                        }
                        e.Accept();
                        return;
                    }
                    rejected = verdict.Kind;
                    e.Reject();
                }
                catch (Exception)
                {
                    rejected = HostKeyVerdictKind.RejectMismatch;
                    e.Reject();
                }
            };

            var request = new SshConnectRequest
            {
                Host = host.HostName,
                Port = port,
                Username = host.Username ?? string.Empty,
                ConnectTimeoutMs = connectTimeoutMs,
                // 目标保持旧行为（host.Keepalive 原值）；跳板与终端跳板链一致默认 30s 保活。
                KeepaliveSeconds = isTarget ? host.Keepalive
                    : (host.Keepalive > 0 ? host.Keepalive : DefaultKeepaliveSeconds),
                Cols = 80,
                Rows = 24,
                TermType = "none"
            };

            SshErrorCode code;
            session.HostKeyCheck += onKey;
            try
            {
                code = via == null
                    ? await session.ConnectAsync(request).ConfigureAwait(false)
                    : await session.ConnectJumpAsync(request, via).ConfigureAwait(false);
            }
            finally
            {
                session.HostKeyCheck -= onKey;
            }

            if (code != SshErrorCode.None)
            {
                TunnelConnectFailure failure;
                if (rejected == HostKeyVerdictKind.RejectUnknown || (code == SshErrorCode.UnknownHostKey && rejected == null))
                {
                    failure = TunnelConnectFailure.UnknownHostKey;
                }
                else if (rejected.HasValue || code == SshErrorCode.HostKeyMismatch)
                {
                    failure = TunnelConnectFailure.HostKeyMismatch;
                }
                else
                {
                    failure = TunnelConnectFailure.ConnectFailed;
                }
                return new TunnelConnectResult
                {
                    Failure = failure,
                    Code = failure == TunnelConnectFailure.UnknownHostKey ? SshErrorCode.UnknownHostKey
                        : failure == TunnelConnectFailure.HostKeyMismatch ? SshErrorCode.HostKeyMismatch
                        : code
                };
            }

            if (toSave != null)
            {
                await SaveKnownHostAsync(host.HostName, port, toSave).ConfigureAwait(false);
            }
            cancellation.ThrowIfCancellationRequested();

            SshErrorCode auth = await AuthenticateAsync(session, host).ConfigureAwait(false);
            if (auth != SshErrorCode.None)
            {
                return new TunnelConnectResult
                {
                    Failure = auth == SshErrorCode.NoLocalCredential
                        ? TunnelConnectFailure.NoSavedCredential
                        : TunnelConnectFailure.AuthFailed,
                    Code = auth
                };
            }
            return new TunnelConnectResult { Failure = TunnelConnectFailure.None, Code = SshErrorCode.None };
        }

        internal async Task<SshErrorCode> AuthenticateAsync(ISshSession session, Host host)
        {
            // 服务器接受 "none"（无需认证）时直接放行；探测失败按原流程。
            try
            {
                AuthMethodsInfo methods = await session.QueryAuthMethodsAsync().ConfigureAwait(false);
                if (methods != null && methods.Authenticated)
                {
                    return SshErrorCode.None;
                }
            }
            catch (Exception)
            {
            }

            switch (host.AuthType)
            {
                case AuthType.Password:
                {
                    string password = await GetSecretAsync(SecretKeys.HostPassword(host.Id)).ConfigureAwait(false);
                    if (password == null)
                    {
                        return SshErrorCode.NoLocalCredential;
                    }
                    return await session.AuthenticatePasswordAsync(password).ConfigureAwait(false);
                }
                case AuthType.Key:
                {
                    if (string.IsNullOrEmpty(host.KeyId))
                    {
                        return SshErrorCode.NoLocalCredential;
                    }
                    string pem = await GetSecretAsync(SecretKeys.KeyPrivate(host.KeyId)).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(pem))
                    {
                        return SshErrorCode.NoLocalCredential;
                    }
                    string passphrase = await GetSecretAsync(SecretKeys.KeyPassphrase(host.KeyId)).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(passphrase))
                    {
                        passphrase = await GetSecretAsync(SecretKeys.HostPassphrase(host.Id)).ConfigureAwait(false);
                    }
                    // 私钥字节数组认证后（含异常）立即清零。
                    byte[] bytes = Encoding.UTF8.GetBytes(pem);
                    try
                    {
                        return await session.AuthenticatePublicKeyAsync(bytes, passphrase ?? string.Empty).ConfigureAwait(false);
                    }
                    finally
                    {
                        Array.Clear(bytes, 0, bytes.Length);
                    }
                }
                case AuthType.Agent:
                    return await AuthenticateAgentAsync(session, host).ConfigureAwait(false);
                default:
                    return SshErrorCode.NoLocalCredential;
            }
        }

        private async Task<SshErrorCode> AuthenticateAgentAsync(ISshSession session, Host host)
        {
            if (_agent == null)
            {
                return SshErrorCode.InternalError;
            }
            var candidates = new List<KeyEntry>();
            if (_keys != null)
            {
                IReadOnlyList<KeyEntry> all = await _keys.GetAllAsync().ConfigureAwait(false);
                for (int i = 0; i < all.Count; i++)
                {
                    KeyEntry k = all[i];
                    if (k == null || string.IsNullOrEmpty(k.Id))
                    {
                        continue;
                    }
                    if (string.IsNullOrEmpty(host.KeyId) || string.Equals(k.Id, host.KeyId, StringComparison.Ordinal))
                    {
                        candidates.Add(k);
                    }
                }
            }
            if (candidates.Count == 0 && !string.IsNullOrEmpty(host.KeyId))
            {
                // 密钥仓库里没有该条目（例如仅 agent 中托管），仍按 id 尝试一次。
                candidates.Add(new KeyEntry { Id = host.KeyId });
            }

            SshErrorCode last = SshErrorCode.NoLocalCredential;
            for (int i = 0; i < candidates.Count; i++)
            {
                KeyEntry key = candidates[i];
                if (_agent.IsLocked(key.Id) && !await TryUnlockFromStoreAsync(key).ConfigureAwait(false))
                {
                    continue;
                }
                SshErrorCode code = await session.AuthenticateAgentAsync(key.Id).ConfigureAwait(false);
                if (code == SshErrorCode.None)
                {
                    return code;
                }
                if (code == SshErrorCode.PrivateKeyLoadFailed)
                {
                    _agent.Lock(key.Id);
                    last = code;
                    continue;
                }
                if (code == SshErrorCode.AuthPublicKeyFailed)
                {
                    last = code;
                    continue;
                }
                return code;
            }
            return last;
        }

        private async Task<bool> TryUnlockFromStoreAsync(KeyEntry key)
        {
            string pem = await GetSecretAsync(SecretKeys.KeyPrivate(key.Id)).ConfigureAwait(false);
            if (string.IsNullOrEmpty(pem))
            {
                return false;
            }
            string pass = await GetSecretAsync(SecretKeys.KeyPassphrase(key.Id)).ConfigureAwait(false);
            if (key.Encrypted && string.IsNullOrEmpty(pass))
            {
                return false; // 隧道不能弹框索要短语
            }
            return await _agent.UnlockAsync(key.Id, pem, pass ?? string.Empty).ConfigureAwait(false);
        }

        private async Task<string> GetSecretAsync(string name)
        {
            if (_secrets == null)
            {
                return null;
            }
            return await _secrets.GetAsync(name).ConfigureAwait(false);
        }

        private async Task SaveKnownHostAsync(string hostName, int port, HostKeyInfo info)
        {
            if (_knownHosts == null || info == null)
            {
                return;
            }
            try
            {
                string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                KnownHost existing = await _knownHosts.FindAsync(hostName, port).ConfigureAwait(false);
                if (existing == null)
                {
                    await _knownHosts.AddAsync(new KnownHost
                    {
                        Id = IdGenerator.NewId(),
                        Host = hostName,
                        Port = port,
                        KeyType = info.KeyType ?? "unknown",
                        FingerprintSha256 = info.FingerprintSha256,
                        AddedAt = now,
                        LastSeenAt = now
                    }, ChangeOrigin.User).ConfigureAwait(false);
                }
                else
                {
                    existing.LastSeenAt = now;
                    existing.FingerprintSha256 = info.FingerprintSha256;
                    await _knownHosts.UpdateAsync(existing, ChangeOrigin.User).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warning, "tunnel known host save failed: " + ex.GetType().Name);
            }
        }

        private static void CloseAll(List<ISshSession> sessions)
        {
            for (int i = sessions.Count - 1; i >= 0; i--)
            {
                CloseSession(sessions[i]);
            }
            sessions.Clear();
        }

        private static void CloseSession(ISshSession session)
        {
            if (session == null)
            {
                return;
            }
            try { session.Close(); } catch (Exception) { }
            try { session.Dispose(); } catch (Exception) { }
        }

        private void Log(LogLevel level, string message)
        {
            if (_logger != null)
            {
                _logger.Log(level, "Tunnel", message);
            }
        }
    }
}
