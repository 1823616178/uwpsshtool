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
            private readonly object _gate = new object();
            private EventHandler<SessionStateChangedEventArgs> _onState;

            public string TunnelId { get; set; }
            // 目标会话 + 跳板会话（fix/functional-pass：跳板链随隧道一起持有、一起释放）。
            public TunnelConnectResult Connection { get; set; }
            public NativeBridge.Forwarder Forwarder { get; set; }
            public int BoundPort { get; set; }
            public string RouteDescription { get; set; }
            public int DroppedFired;

            public void Subscribe(EventHandler<SessionStateChangedEventArgs> onState)
            {
                lock (_gate)
                {
                    _onState = onState;
                    ForEachSession(s => s.StateChanged += onState);
                }
            }

            public void Dispose()
            {
                NativeBridge.Forwarder forwarder;
                TunnelConnectResult connection;
                lock (_gate)
                {
                    if (_onState != null)
                    {
                        EventHandler<SessionStateChangedEventArgs> handler = _onState;
                        ForEachSession(s => s.StateChanged -= handler);
                        _onState = null;
                    }
                    forwarder = Forwarder;
                    Forwarder = null;
                    connection = Connection;
                    Connection = null;
                }
                if (forwarder != null)
                {
                    try { forwarder.Stop(); } catch { }
                    try { forwarder.Dispose(); } catch { }
                }
                TunnelConnector.Close(connection);
            }

            private void ForEachSession(Action<ISshSession> action)
            {
                TunnelConnectResult c = Connection;
                if (c == null)
                {
                    return;
                }
                if (c.Session != null)
                {
                    action(c.Session);
                }
                if (c.JumpSessions != null)
                {
                    for (int i = 0; i < c.JumpSessions.Count; i++)
                    {
                        action(c.JumpSessions[i]);
                    }
                }
            }
        }

        private const int DefaultConnectTimeoutMs = 15000;

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
                    Message = Localized.Get("Forwarder_Disposed", "Forwarding runtime has been released")
                };
            }
            if (tunnel == null)
            {
                return new TunnelRuntimeStartResult
                {
                    Code = SshErrorCode.InternalError,
                    Message = Localized.Get("Forwarder_EmptyConfig", "Tunnel configuration is empty")
                };
            }
            if (tunnel.Type == TunnelType.Relay)
            {
                return new TunnelRuntimeStartResult
                {
                    Code = SshErrorCode.InternalError,
                    Message = Localized.Get("Forwarder_RelayDesktopOnly", "Relay tunnels only run on desktop")
                };
            }

            // 先停止已存在的旧实例，保证同一隧道 ID 单实例
            Stop(tunnel.Id);

            if (string.IsNullOrEmpty(tunnel.ServerId))
            {
                return new TunnelRuntimeStartResult
                {
                    Code = SshErrorCode.InternalError,
                    Message = Localized.Get("Forwarder_NoHost", "No target host specified")
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
                    Message = Localized.Format("Forwarder_HostNotFound", "Host configuration not found: {0}", tunnel.ServerId)
                };
            }

            cancellation.ThrowIfCancellationRequested();

            // fix/functional-pass（P1-3）：建链交给 Core TunnelConnector——与终端一致复用跳板链
            // （JumpChainPlanner + ConnectJumpAsync），每跳非交互主机密钥校验，agent 遍历密钥；
            // 失败按类别本地化（TunnelText）。
            int timeoutMs = _settings != null ? _settings.ConnectTimeoutSeconds * 1000 : DefaultConnectTimeoutMs;
            if (timeoutMs < 1000)
            {
                timeoutMs = DefaultConnectTimeoutMs;
            }
            var connector = new TunnelConnector(
                _hosts, _knownHosts, _keys, _secrets, _agent, new NativeSshSessionFactory(_agent), _logger);

            TunnelConnectResult connection = null;
            NativeBridge.Forwarder forwarder = null;
            ActiveTunnel active = null;
            try
            {
                connection = await connector.ConnectAsync(host, timeoutMs, cancellation).ConfigureAwait(false);
                if (!connection.Success)
                {
                    return new TunnelRuntimeStartResult
                    {
                        Code = connection.Code,
                        Message = TunnelText.ConnectFailure(connection)
                    };
                }
                var session = connection.Session as NativeSshSession;
                if (session == null)
                {
                    TunnelConnector.Close(connection);
                    return new TunnelRuntimeStartResult
                    {
                        Code = SshErrorCode.InternalError,
                        Message = Localized.Get("Tunnel_StartFailed", "Failed to start")
                    };
                }

                cancellation.ThrowIfCancellationRequested();

                // 挂载转发监听
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
                        TunnelConnector.Close(connection);
                        return new TunnelRuntimeStartResult
                        {
                            Code = SshErrorCode.InternalError,
                            Message = Localized.Get("Forwarder_UnsupportedType", "Unsupported tunnel type")
                        };
                }
                forwarder = new NativeBridge.Forwarder(session.Native);

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
                    forwarder = null;
                    TunnelConnector.Close(connection);
                    return new TunnelRuntimeStartResult
                    {
                        Code = (SshErrorCode)fwdResult.Code,
                        Message = string.IsNullOrEmpty(fwdResult.Message)
                            ? Localized.Format("Forwarder_StartFailed", "Failed to start forwarding ({0})", fwdResult.Code)
                            : fwdResult.Message
                    };
                }

                int boundPort = fwdResult.BoundPort > 0 ? fwdResult.BoundPort : listenPort;
                string routeDesc = FormatRouteDescription(tunnel.Type, listenHost, boundPort, destHost, destPort);

                active = new ActiveTunnel
                {
                    TunnelId = tunnel.Id,
                    Connection = connection,
                    Forwarder = forwarder,
                    BoundPort = boundPort,
                    RouteDescription = routeDesc
                };
                forwarder = null;

                // 订阅链路异常中断：目标会话或任一跳板断开都算隧道掉线。
                ActiveTunnel owner = active;
                string tunnelId = tunnel.Id;
                EventHandler<SessionStateChangedEventArgs> onState = (s, args) =>
                {
                    if (args.State != SessionStateKind.Disconnected && args.State != SessionStateKind.Error)
                    {
                        return;
                    }
                    if (Interlocked.Exchange(ref owner.DroppedFired, 1) != 0)
                    {
                        return;
                    }
                    ActiveTunnel removed;
                    if (_activeTunnels.TryRemove(tunnelId, out removed) && removed != null)
                    {
                        // 跳板先断时目标会话可能还挂着：一并收尾，不留半截链路。
                        var ignoreTeardown = Task.Run(() => removed.Dispose());
                    }
                    Dropped?.Invoke(this, new TunnelRuntimeDroppedEventArgs
                    {
                        TunnelId = tunnelId,
                        Reason = Localized.Get("Forwarder_LinkLost", "Session link lost"),
                        ErrorCode = args.ErrorCode != SshErrorCode.None ? args.ErrorCode : SshErrorCode.SocketError
                    });
                };
                active.Subscribe(onState);

                _activeTunnels[tunnel.Id] = active;

                return new TunnelRuntimeStartResult
                {
                    Code = SshErrorCode.None,
                    BoundPort = boundPort,
                    RouteDescription = routeDesc,
                    Message = Localized.Get("Forwarder_Started", "Started")
                };
            }
            catch (OperationCanceledException)
            {
                CleanupFailedStart(tunnel.Id, active, forwarder, connection);
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Error("NativeForwarder", "StartAsync exception", ex);
                CleanupFailedStart(tunnel.Id, active, forwarder, connection);
                return new TunnelRuntimeStartResult
                {
                    Code = SshErrorCode.InternalError,
                    Message = Localized.Format("Forwarder_StartException", "Tunnel failed to start: {0}", ex.GetType().Name)
                };
            }
        }

        private void CleanupFailedStart(string tunnelId, ActiveTunnel active, NativeBridge.Forwarder forwarder, TunnelConnectResult connection)
        {
            if (active != null)
            {
                ActiveTunnel removed;
                _activeTunnels.TryRemove(tunnelId, out removed);
                active.Dispose();
                return;
            }
            if (forwarder != null)
            {
                try { forwarder.Dispose(); } catch (Exception) { }
            }
            TunnelConnector.Close(connection);
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

        private static string FormatRouteDescription(TunnelType type, string listenHost, int listenPort, string destHost, int destPort)
        {
            switch (type)
            {
                case TunnelType.Local:
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}:{1} -> {2}:{3}", listenHost, listenPort, destHost, destPort);
                case TunnelType.Remote:
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}:{1} -> {2}:{3}", Localized.Get("Forwarder_RemotePrefix", "(remote)"), listenPort, destHost, destPort);
                case TunnelType.Dynamic:
                    return string.Format(System.Globalization.CultureInfo.InvariantCulture, "SOCKS5 {0}:{1}", listenHost, listenPort);
                default:
                    return Localized.Get("Forwarder_Generic", "Forward");
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
