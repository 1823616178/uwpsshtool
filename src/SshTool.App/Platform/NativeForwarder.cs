using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.Core.Forwarding;
using SshTool.Core.Models;
using SshTool.Core.Sessions;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;
using NativeBridge = SshTool.Native.Bridge;

namespace SshTool.App.Platform
{
    // F05：ITunnelRuntime 的原生实现（01-DESIGN.md §11.2；04-TASKS F05）。
    //
    // 每条运行中的隧道独占一条专用 NativeSshSession 与一个 NativeBridge.Forwarder 监听挂载。
    // 隔离性：隧道连接不占用终端会话，终端关闭或退出不影响后台隧道。
    // 线程安全：活跃隧道映射由线程安全字典维护；事件与统计读取不阻塞任何线程。
    public sealed class NativeForwarder : ITunnelRuntime, IDisposable
    {
        private sealed class ActiveTunnel : IDisposable
        {
            public string TunnelId { get; set; }
            public NativeSshSession Session { get; set; }
            public NativeBridge.Forwarder Forwarder { get; set; }
            public int BoundPort { get; set; }
            public string RouteDescription { get; set; }
            public int DroppedFired;

            public void Dispose()
            {
                if (Forwarder != null)
                {
                    try { Forwarder.Stop(); } catch { }
                    try { Forwarder.Dispose(); } catch { }
                    Forwarder = null;
                }
                if (Session != null)
                {
                    try { Session.Close(); } catch { }
                    try { Session.Dispose(); } catch { }
                    Session = null;
                }
            }
        }

        private readonly HostRepository _hosts;
        private readonly KnownHostRepository _knownHosts;
        private readonly KeyRepository _keys;
        private readonly ISecretStore _secrets;
        private readonly SettingsRepository _settings;
        private readonly NativeSshAgent _agent;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, ActiveTunnel> _activeTunnels =
            new ConcurrentDictionary<string, ActiveTunnel>(StringComparer.Ordinal);
        private bool _disposed;

        public event EventHandler<TunnelRuntimeDroppedEventArgs> Dropped;

        public NativeForwarder(
            HostRepository hosts,
            KnownHostRepository knownHosts,
            KeyRepository keys,
            ISecretStore secrets,
            SettingsRepository settings,
            NativeSshAgent agent,
            ILogger logger)
        {
            _hosts = hosts;
            _knownHosts = knownHosts;
            _keys = keys;
            _secrets = secrets;
            _settings = settings;
            _agent = agent;
            _logger = logger;
        }

        public async Task<TunnelRuntimeStartResult> StartAsync(Tunnel tunnel, CancellationToken cancellation)
        {
            if (_disposed)
            {
                return new TunnelRuntimeStartResult
                {
                    Code = SshErrorCode.InternalError,
                    Message = "转发运行时已释放"
                };
            }
            if (tunnel == null)
            {
                return new TunnelRuntimeStartResult
                {
                    Code = SshErrorCode.InternalError,
                    Message = "隧道配置为空"
                };
            }
            if (tunnel.Type == TunnelType.Relay)
            {
                return new TunnelRuntimeStartResult
                {
                    Code = SshErrorCode.InternalError,
                    Message = "中转隧道仅在桌面端运行"
                };
            }

            // 先停止已存在的旧实例，保证同一隧道 ID 单实例
            Stop(tunnel.Id);

            if (string.IsNullOrEmpty(tunnel.ServerId))
            {
                return new TunnelRuntimeStartResult
                {
                    Code = SshErrorCode.InternalError,
                    Message = "未指定目标主机"
                };
            }

            Host host = null;
            if (_hosts != null)
            {
                host = await _hosts.GetByIdAsync(tunnel.ServerId).ConfigureAwait(false);
            }
            if (host == null)
            {
                return new TunnelRuntimeStartResult
                {
                    Code = SshErrorCode.InternalError,
                    Message = "找不到对应的主机配置: " + tunnel.ServerId
                };
            }

            cancellation.ThrowIfCancellationRequested();

            var session = new NativeSshSession(_agent);
            ActiveTunnel active = null;
            try
            {
                int port = host.Port > 0 ? host.Port : 22;
                KnownHost known = await FindKnownAsync(host.HostName, port).ConfigureAwait(false);
                string hostFp = host.HostFingerprint;
                bool acceptedKeyShouldSave = false;
                HostKeyInfo acceptedKey = null;

                EventHandler<HostKeyCheckEventArgs> onKey = (s, args) =>
                {
                    try
                    {
                        HostKeyVerdict verdict = HostKeyVerifier.Verify(known, hostFp, args.Info);
                        if (verdict.Kind == HostKeyVerdictKind.Accept)
                        {
                            if (verdict.WriteKnownHost)
                            {
                                acceptedKeyShouldSave = true;
                                acceptedKey = args.Info;
                            }
                            args.Accept();
                            return;
                        }
                        if (verdict.Kind == HostKeyVerdictKind.RejectMismatch)
                        {
                            args.Reject();
                            return;
                        }
                        // 后台隧道无前台弹窗交互；根据 01-DESIGN / 05-CODE-AUDIT，
                        // 首次连接实行 TOFU：接受并保存指纹
                        acceptedKeyShouldSave = true;
                        acceptedKey = args.Info;
                        args.Accept();
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("NativeForwarder", "host key check error", ex);
                        args.Reject();
                    }
                };

                session.HostKeyCheck += onKey;

                // 2. 建立网络连接
                int timeoutMs = _settings != null ? _settings.ConnectTimeoutSeconds * 1000 : 15000;
                if (timeoutMs < 1000)
                {
                    timeoutMs = 15000;
                }

                var connectReq = new SshConnectRequest
                {
                    Host = host.HostName,
                    Port = port,
                    Username = host.Username ?? string.Empty,
                    ConnectTimeoutMs = timeoutMs,
                    KeepaliveSeconds = host.Keepalive,
                    Cols = 80,
                    Rows = 24,
                    TermType = "none"
                };

                cancellation.ThrowIfCancellationRequested();

                SshErrorCode connectCode = await session.ConnectAsync(connectReq).ConfigureAwait(false);
                session.HostKeyCheck -= onKey;

                if (connectCode != SshErrorCode.None)
                {
                    session.Close();
                    session.Dispose();
                    return new TunnelRuntimeStartResult
                    {
                        Code = connectCode,
                        Message = "连接服务器失败 (" + (int)connectCode + ")"
                    };
                }

                if (acceptedKeyShouldSave && acceptedKey != null)
                {
                    await SaveKnownHostAsync(host.HostName, port, acceptedKey).ConfigureAwait(false);
                }

                cancellation.ThrowIfCancellationRequested();

                // 3. 认证
                SshErrorCode authCode = await AuthenticateAsync(session, host).ConfigureAwait(false);
                if (authCode != SshErrorCode.None)
                {
                    session.Close();
                    session.Dispose();
                    return new TunnelRuntimeStartResult
                    {
                        Code = authCode,
                        Message = "认证失败 (" + (int)authCode + ")"
                    };
                }

                cancellation.ThrowIfCancellationRequested();

                // 4. 挂载转发监听
                var forwarder = new NativeBridge.Forwarder(session.Native);
                NativeBridge.ForwardKind kind;
                switch (tunnel.Type)
                {
                    case TunnelType.Local:
                        kind = NativeBridge.ForwardKind.Local;
                        break;
                    case TunnelType.Remote:
                        kind = NativeBridge.ForwardKind.Remote;
                        break;
                    case TunnelType.Dynamic:
                        kind = NativeBridge.ForwardKind.Dynamic;
                        break;
                    default:
                        forwarder.Dispose();
                        session.Close();
                        session.Dispose();
                        return new TunnelRuntimeStartResult
                        {
                            Code = SshErrorCode.InternalError,
                            Message = "不支持的隧道类型"
                        };
                }

                string listenHost = string.IsNullOrEmpty(tunnel.ListenHost) ? "127.0.0.1" : tunnel.ListenHost;
                int listenPort = tunnel.ListenPort;
                string destHost = tunnel.DestHost ?? string.Empty;
                int destPort = tunnel.DestPort;

                NativeBridge.ForwardStartResult fwdResult = await forwarder.StartAsync(
                    kind,
                    listenHost,
                    listenPort,
                    destHost,
                    destPort
                ).AsTask(cancellation).ConfigureAwait(false);

                if (fwdResult.Code != 0)
                {
                    forwarder.Dispose();
                    session.Close();
                    session.Dispose();
                    return new TunnelRuntimeStartResult
                    {
                        Code = (SshErrorCode)fwdResult.Code,
                        Message = string.IsNullOrEmpty(fwdResult.Message)
                            ? "启动转发失败 (" + fwdResult.Code + ")"
                            : fwdResult.Message
                    };
                }

                int boundPort = fwdResult.BoundPort > 0 ? fwdResult.BoundPort : listenPort;
                string routeDesc = FormatRouteDescription(tunnel.Type, listenHost, boundPort, destHost, destPort);

                active = new ActiveTunnel
                {
                    TunnelId = tunnel.Id,
                    Session = session,
                    Forwarder = forwarder,
                    BoundPort = boundPort,
                    RouteDescription = routeDesc
                };

                // 5. 订阅链路异常中断（StateChanged 抛出）
                session.StateChanged += (s, args) =>
                {
                    if (args.State == SessionStateKind.Disconnected
                        || args.State == SessionStateKind.Error)
                    {
                        if (Interlocked.Exchange(ref active.DroppedFired, 1) == 0)
                        {
                            ActiveTunnel removed;
                            _activeTunnels.TryRemove(tunnel.Id, out removed);
                            Dropped?.Invoke(this, new TunnelRuntimeDroppedEventArgs
                            {
                                TunnelId = tunnel.Id,
                                Reason = "会话链路中断",
                                ErrorCode = args.ErrorCode != SshErrorCode.None ? args.ErrorCode : SshErrorCode.SocketError
                            });
                        }
                    }
                };

                _activeTunnels[tunnel.Id] = active;

                return new TunnelRuntimeStartResult
                {
                    Code = SshErrorCode.None,
                    BoundPort = boundPort,
                    RouteDescription = routeDesc,
                    Message = "已启动"
                };
            }
            catch (OperationCanceledException)
            {
                if (active != null)
                {
                    ActiveTunnel removed;
                    _activeTunnels.TryRemove(tunnel.Id, out removed);
                    active.Dispose();
                }
                else
                {
                    session.Close();
                    session.Dispose();
                }
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Error("NativeForwarder", "StartAsync exception", ex);
                if (active != null)
                {
                    ActiveTunnel removed;
                    _activeTunnels.TryRemove(tunnel.Id, out removed);
                    active.Dispose();
                }
                else
                {
                    session.Close();
                    session.Dispose();
                }
                return new TunnelRuntimeStartResult
                {
                    Code = SshErrorCode.InternalError,
                    Message = "隧道启动异常: " + ex.GetType().Name
                };
            }
        }

        public void Stop(string tunnelId)
        {
            if (string.IsNullOrEmpty(tunnelId))
            {
                return;
            }
            ActiveTunnel active;
            if (_activeTunnels.TryRemove(tunnelId, out active) && active != null)
            {
                active.Dispose();
            }
        }

        public TunnelStatsSample ReadSample(string tunnelId)
        {
            if (string.IsNullOrEmpty(tunnelId))
            {
                return null;
            }
            ActiveTunnel active;
            if (!_activeTunnels.TryGetValue(tunnelId, out active) || active == null)
            {
                return null;
            }
            NativeBridge.Forwarder fwd = active.Forwarder;
            if (fwd == null || !fwd.IsRunning)
            {
                return null;
            }
            try
            {
                NativeBridge.ForwardStatsSnapshot snap = fwd.SnapshotStats();
                if (snap == null)
                {
                    return null;
                }
                // 方向换算（见 01-DESIGN.md §11.2 与 Forwarder.h）：
                // BytesUp = socket→通道（本机→隧道），换算为 BytesOut
                // BytesDown = 通道→socket（隧道→本机），换算为 BytesIn
                return new TunnelStatsSample
                {
                    ActiveConnections = snap.ActiveConnections,
                    TotalConnections = snap.TotalConnections,
                    BytesIn = snap.BytesDown,
                    BytesOut = snap.BytesUp
                };
            }
            catch
            {
                return null;
            }
        }

        private async Task<KnownHost> FindKnownAsync(string hostName, int port)
        {
            if (_knownHosts == null)
            {
                return null;
            }
            try
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
            }
            catch (Exception ex)
            {
                AppLog.Error("NativeForwarder", "FindKnownAsync failed", ex);
            }
            return null;
        }

        private async Task SaveKnownHostAsync(string hostName, int port, HostKeyInfo info)
        {
            if (_knownHosts == null || info == null)
            {
                return;
            }
            try
            {
                KnownHost existing = await FindKnownAsync(hostName, port).ConfigureAwait(false);
                string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
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
                AppLog.Error("NativeForwarder", "SaveKnownHost failed", ex);
            }
        }

        private async Task<SshErrorCode> AuthenticateAsync(NativeSshSession session, Host host)
        {
            switch (host.AuthType)
            {
                case AuthType.Password:
                {
                    string password = null;
                    if (_secrets != null)
                    {
                        password = await _secrets.GetAsync(SecretKeys.HostPassword(host.Id)).ConfigureAwait(false);
                    }
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
                    string privateKey = null;
                    string passphrase = null;
                    if (_secrets != null)
                    {
                        privateKey = await _secrets.GetAsync(SecretKeys.KeyPrivate(host.KeyId)).ConfigureAwait(false);
                        passphrase = await _secrets.GetAsync(SecretKeys.KeyPassphrase(host.KeyId)).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(passphrase))
                        {
                            passphrase = await _secrets.GetAsync(SecretKeys.HostPassphrase(host.Id)).ConfigureAwait(false);
                        }
                    }
                    if (string.IsNullOrEmpty(privateKey))
                    {
                        return SshErrorCode.NoLocalCredential;
                    }
                    byte[] pemBytes = Encoding.UTF8.GetBytes(privateKey);
                    return await session.AuthenticatePublicKeyAsync(pemBytes, passphrase ?? string.Empty).ConfigureAwait(false);
                }
                case AuthType.Agent:
                {
                    if (_agent == null)
                    {
                        return SshErrorCode.InternalError;
                    }
                    if (string.IsNullOrEmpty(host.KeyId))
                    {
                        return SshErrorCode.NoLocalCredential;
                    }
                    if (_agent.IsLocked(host.KeyId))
                    {
                        // 如果在 agent 中已锁定，尝试从 SecretStore 加载并解锁进 agent
                        if (_secrets != null)
                        {
                            string pem = await _secrets.GetAsync(SecretKeys.KeyPrivate(host.KeyId)).ConfigureAwait(false);
                            string pass = await _secrets.GetAsync(SecretKeys.KeyPassphrase(host.KeyId)).ConfigureAwait(false);
                            if (!string.IsNullOrEmpty(pem))
                            {
                                await _agent.UnlockAsync(host.KeyId, pem, pass ?? string.Empty).ConfigureAwait(false);
                            }
                        }
                    }
                    return await session.AuthenticateAgentAsync(host.KeyId).ConfigureAwait(false);
                }
                default:
                    return SshErrorCode.AuthPasswordFailed;
            }
        }

        private static string FormatRouteDescription(TunnelType type, string listenHost, int listenPort, string destHost, int destPort)
        {
            switch (type)
            {
                case TunnelType.Local:
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}:{1} -> {2}:{3}", listenHost, listenPort, destHost, destPort);
                case TunnelType.Remote:
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture, "(远程):{0} -> {1}:{2}", listenPort, destHost, destPort);
                case TunnelType.Dynamic:
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture, "SOCKS5 {0}:{1}", listenHost, listenPort);
                default:
                    return "转发";
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            foreach (string key in _activeTunnels.Keys)
            {
                ActiveTunnel active;
                if (_activeTunnels.TryRemove(key, out active) && active != null)
                {
                    active.Dispose();
                }
            }
        }
    }
}
