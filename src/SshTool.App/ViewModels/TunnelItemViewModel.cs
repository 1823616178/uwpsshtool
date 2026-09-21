using System;
using System.Globalization;
using SshTool.App.Controls;
using SshTool.App.Infrastructure;
using SshTool.Core.Forwarding;
using SshTool.Core.Models;

namespace SshTool.App.ViewModels
{
    // F06：单个隧道行视图模型（01-DESIGN §11.2；02-UI §5.3；05-CODE-AUDIT §6.1）。
    public sealed class TunnelItemViewModel : ViewModelBase
    {
        private readonly Tunnel _tunnel;
        private string _serverName;
        private string _groupName;
        private TunnelStateKind _state = TunnelStateKind.Idle;
        private string _statusMessage = string.Empty;
        private ulong _activeConnections;
        private ulong _totalConnections;
        private ulong _rateIn;
        private ulong _rateOut;

        public TunnelItemViewModel(Tunnel tunnel, string serverName = null, string groupName = null)
        {
            _tunnel = tunnel ?? throw new ArgumentNullException(nameof(tunnel));
            _serverName = serverName ?? string.Empty;
            _groupName = groupName ?? string.Empty;
        }

        public Tunnel Tunnel => _tunnel;
        public string Id => _tunnel.Id;
        public string Name => string.IsNullOrWhiteSpace(_tunnel.Name) ? DefaultTitle : _tunnel.Name;
        public TunnelType Type => _tunnel.Type;
        public string ServerId => _tunnel.ServerId;
        public string GroupId => _tunnel.GroupId;
        public bool AutoReconnect => _tunnel.AutoReconnect;
        public bool AutoStart => _tunnel.AutoStart;
        public bool Enabled => _tunnel.Enabled;

        public bool CanToggle => _tunnel.Type != TunnelType.Relay;

        public string ServerName
        {
            get => _serverName;
            set
            {
                if (SetProperty(ref _serverName, value))
                {
                    RaisePropertyChanged(nameof(Subtitle));
                }
            }
        }

        public string GroupName
        {
            get => _groupName;
            set => SetProperty(ref _groupName, value);
        }

        public TunnelStateKind State
        {
            get => _state;
            set
            {
                if (SetProperty(ref _state, value))
                {
                    RaisePropertyChanged(nameof(IsRunning));
                    RaisePropertyChanged(nameof(StatusDotState));
                    RaisePropertyChanged(nameof(StateText));
                    RaisePropertyChanged(nameof(Subtitle));
                }
            }
        }

        public bool IsRunning => _state == TunnelStateKind.Running || _state == TunnelStateKind.Connecting || _state == TunnelStateKind.Reconnecting;

        public StatusDotState StatusDotState
        {
            get
            {
                switch (_state)
                {
                    case TunnelStateKind.Running:
                        return StatusDotState.Connected;
                    case TunnelStateKind.Connecting:
                        return StatusDotState.Connecting;
                    case TunnelStateKind.Reconnecting:
                        return StatusDotState.Reconnecting;
                    case TunnelStateKind.Error:
                        return StatusDotState.Error;
                    default:
                        return StatusDotState.Disconnected;
                }
            }
        }

        public string StateText
        {
            get
            {
                if (_tunnel.Type == TunnelType.Relay)
                {
                    return AppServices.Current?.GetString("TunnelState_DesktopOnly") ?? "仅桌面端运行";
                }
                switch (_state)
                {
                    case TunnelStateKind.Running:
                        return AppServices.Current?.GetString("TunnelState_Running") ?? "运行中";
                    case TunnelStateKind.Connecting:
                        return AppServices.Current?.GetString("TunnelState_Connecting") ?? "连接中";
                    case TunnelStateKind.Reconnecting:
                        return AppServices.Current?.GetString("TunnelState_Reconnecting") ?? "重连中";
                    case TunnelStateKind.Error:
                        return string.IsNullOrEmpty(_statusMessage) ? (AppServices.Current?.GetString("TunnelState_Error") ?? "错误") : _statusMessage;
                    default:
                        return AppServices.Current?.GetString("TunnelState_Idle") ?? "已停止";
                }
            }
        }

        public string DefaultTitle
        {
            get
            {
                string listenHost = string.IsNullOrEmpty(_tunnel.ListenHost) ? "127.0.0.1" : _tunnel.ListenHost;
                switch (_tunnel.Type)
                {
                    case TunnelType.Local:
                        string localPrefix = AppServices.Current?.GetString("Tunnel_Default_Local") ?? "本地";
                        return string.Format(CultureInfo.InvariantCulture, "{0} {1}:{2} → {3}:{4}", localPrefix, listenHost, _tunnel.ListenPort, _tunnel.DestHost, _tunnel.DestPort);
                    case TunnelType.Remote:
                        string remotePrefix = AppServices.Current?.GetString("Tunnel_Default_Remote") ?? "远程";
                        return string.Format(CultureInfo.InvariantCulture, "{0} :{1} → {2}:{3}", remotePrefix, _tunnel.ListenPort, _tunnel.DestHost, _tunnel.DestPort);
                    case TunnelType.Dynamic:
                        return string.Format(CultureInfo.InvariantCulture, "SOCKS5 {0}:{1}", listenHost, _tunnel.ListenPort);
                    case TunnelType.Relay:
                        string relayPrefix = AppServices.Current?.GetString("Tunnel_Default_Relay") ?? "中转";
                        return string.Format(CultureInfo.InvariantCulture, "{0} :{1} → {2}:{3}", relayPrefix, _tunnel.ListenPort, _tunnel.DestHost, _tunnel.DestPort);
                    default:
                        return AppServices.Current?.GetString("Tunnel_Default_Forward") ?? "转发";
                }
            }
        }

        public string Subtitle
        {
            get
            {
                if (_tunnel.Type == TunnelType.Relay)
                {
                    return AppServices.Current?.GetString("TunnelState_DesktopOnly") ?? "仅桌面端运行";
                }
                string via = string.IsNullOrEmpty(_serverName) ? string.Empty : "via " + _serverName + " · ";
                if (_state == TunnelStateKind.Running)
                {
                    string stats = string.Empty;
                    if (_activeConnections > 0 || _rateIn > 0 || _rateOut > 0)
                    {
                        string connFmt = AppServices.Current?.GetString("TunnelState_Connections") ?? "{0} 连接";
                        stats = string.Format(CultureInfo.InvariantCulture, " · " + connFmt + " · ↓{1} ↑{2}",
                            _activeConnections, FormatRate(_rateIn), FormatRate(_rateOut));
                    }
                    string running = AppServices.Current?.GetString("TunnelState_Running") ?? "运行中";
                    return via + running + stats;
                }
                return via + StateText;
            }
        }

        public void ApplyStatus(TunnelStatusSnapshot status)
        {
            if (status == null)
            {
                State = TunnelStateKind.Idle;
                _statusMessage = string.Empty;
                _activeConnections = 0;
                _totalConnections = 0;
                _rateIn = 0;
                _rateOut = 0;
            }
            else
            {
                _statusMessage = status.Message ?? string.Empty;
                State = status.State;
                if (status.Stats != null)
                {
                    _activeConnections = status.Stats.ActiveConnections;
                    _totalConnections = status.Stats.TotalConnections;
                    _rateIn = status.Stats.RateIn;
                    _rateOut = status.Stats.RateOut;
                }
                else
                {
                    _activeConnections = 0;
                    _rateIn = 0;
                    _rateOut = 0;
                }
            }
            RaisePropertyChanged(nameof(Subtitle));
        }

        private static string FormatRate(ulong bytesPerSec)
        {
            if (bytesPerSec < 1024)
            {
                return bytesPerSec.ToString(CultureInfo.InvariantCulture) + " B/s";
            }
            const double Ki = 1024d;
            const double Mi = 1024d * 1024d;
            if (bytesPerSec < Mi)
            {
                return (bytesPerSec / Ki).ToString("0.#", CultureInfo.InvariantCulture) + " KB/s";
            }
            return (bytesPerSec / Mi).ToString("0.#", CultureInfo.InvariantCulture) + " MB/s";
        }
    }
}
