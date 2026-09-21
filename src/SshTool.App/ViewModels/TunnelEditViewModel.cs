using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Storage;
using SshTool.Core.Validation;

namespace SshTool.App.ViewModels
{
    // F06：隧道编辑/新建视图模型（01-DESIGN §11.2；02-UI §5.18；05-CODE-AUDIT §6.4）。
    public sealed class TunnelEditViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private string _id;
        private bool _isNew;
        private string _name = string.Empty;
        private TunnelType _type = TunnelType.Local;
        private string _serverId = string.Empty;
        private string _groupId = string.Empty;
        private string _listenHost = "127.0.0.1";
        private string _listenPortText = "8080";
        private string _destHost = "127.0.0.1";
        private string _destPortText = "80";
        private string _destServerId = string.Empty;
        private bool _autoReconnect = true;
        private bool _autoStart;
        private bool _enabled = true;
        private bool _isDirty;

        private readonly Dictionary<string, string> _errors = new Dictionary<string, string>(StringComparer.Ordinal);

        public TunnelEditViewModel(AppServices services = null)
        {
            _services = services ?? AppServices.Current;
            Hosts = new ObservableCollection<IdNameOption>();
            Groups = new ObservableCollection<IdNameOption>();
            string localName = _services?.GetString("TunnelType_Local") ?? "本地转发 (Local)";
            string remoteName = _services?.GetString("TunnelType_Remote") ?? "远程转发 (Remote)";
            string dynamicName = _services?.GetString("TunnelType_Dynamic") ?? "动态 SOCKS5 (Dynamic)";
            string relayName = _services?.GetString("TunnelType_Relay") ?? "中转转发 (Relay)";
            Types = new List<TunnelTypeOption>
            {
                new TunnelTypeOption(TunnelType.Local, localName),
                new TunnelTypeOption(TunnelType.Remote, remoteName),
                new TunnelTypeOption(TunnelType.Dynamic, dynamicName),
                new TunnelTypeOption(TunnelType.Relay, relayName)
            };
        }

        public bool IsNew => _isNew;
        public ObservableCollection<IdNameOption> Hosts { get; }
        public ObservableCollection<IdNameOption> Groups { get; }
        public IReadOnlyList<TunnelTypeOption> Types { get; }

        public string Title => _isNew ? (_services?.GetString("TunnelEdit_Title_New") ?? "新建隧道") : (_services?.GetString("TunnelEdit_Title_Edit") ?? "编辑隧道");

        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value ?? string.Empty))
                {
                    _isDirty = true;
                    ClearError("name");
                }
            }
        }

        public TunnelType Type
        {
            get => _type;
            set
            {
                if (SetProperty(ref _type, value))
                {
                    _isDirty = true;
                    RaisePropertyChanged(nameof(IsDestVisible));
                    RaisePropertyChanged(nameof(IsRelayVisible));
                    RaisePropertyChanged(nameof(RoutePreview));
                }
            }
        }

        public bool IsDestVisible => _type != TunnelType.Dynamic;
        public bool IsRelayVisible => _type == TunnelType.Relay;

        public string ServerId
        {
            get => _serverId;
            set
            {
                if (SetProperty(ref _serverId, value ?? string.Empty))
                {
                    _isDirty = true;
                    ClearError("serverId");
                    RaisePropertyChanged(nameof(RoutePreview));
                }
            }
        }

        public string GroupId
        {
            get => _groupId;
            set
            {
                if (SetProperty(ref _groupId, value ?? string.Empty))
                {
                    _isDirty = true;
                }
            }
        }

        public string ListenHost
        {
            get => _listenHost;
            set
            {
                if (SetProperty(ref _listenHost, value ?? string.Empty))
                {
                    _isDirty = true;
                    ClearError("listenHost");
                    RaisePropertyChanged(nameof(RoutePreview));
                }
            }
        }

        public string ListenPortText
        {
            get => _listenPortText;
            set
            {
                if (SetProperty(ref _listenPortText, value ?? string.Empty))
                {
                    _isDirty = true;
                    ClearError("listenPort");
                    RaisePropertyChanged(nameof(RoutePreview));
                }
            }
        }

        public string DestHost
        {
            get => _destHost;
            set
            {
                if (SetProperty(ref _destHost, value ?? string.Empty))
                {
                    _isDirty = true;
                    ClearError("destHost");
                    RaisePropertyChanged(nameof(RoutePreview));
                }
            }
        }

        public string DestPortText
        {
            get => _destPortText;
            set
            {
                if (SetProperty(ref _destPortText, value ?? string.Empty))
                {
                    _isDirty = true;
                    ClearError("destPort");
                    RaisePropertyChanged(nameof(RoutePreview));
                }
            }
        }

        public string DestServerId
        {
            get => _destServerId;
            set
            {
                if (SetProperty(ref _destServerId, value ?? string.Empty))
                {
                    _isDirty = true;
                    ClearError("destServerId");
                    RaisePropertyChanged(nameof(RoutePreview));
                }
            }
        }

        public bool AutoReconnect
        {
            get => _autoReconnect;
            set
            {
                if (SetProperty(ref _autoReconnect, value))
                {
                    _isDirty = true;
                }
            }
        }

        public bool AutoStart
        {
            get => _autoStart;
            set
            {
                if (SetProperty(ref _autoStart, value))
                {
                    _isDirty = true;
                }
            }
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (SetProperty(ref _enabled, value))
                {
                    _isDirty = true;
                }
            }
        }

        public bool IsDirty => _isDirty;

        public string RoutePreview
        {
            get
            {
                string srvName = Hosts.FirstOrDefault(h => h.Id == _serverId)?.Name ?? "目标主机";
                string lHost = string.IsNullOrWhiteSpace(_listenHost) ? "127.0.0.1" : _listenHost.Trim();
                string lPort = string.IsNullOrWhiteSpace(_listenPortText) ? "?" : _listenPortText.Trim();
                string dHost = string.IsNullOrWhiteSpace(_destHost) ? "?" : _destHost.Trim();
                string dPort = string.IsNullOrWhiteSpace(_destPortText) ? "?" : _destPortText.Trim();

                switch (_type)
                {
                    case TunnelType.Local:
                        return string.Format(CultureInfo.InvariantCulture, "本机 {0}:{1} → {2} → {3}:{4}", lHost, lPort, srvName, dHost, dPort);
                    case TunnelType.Remote:
                        return string.Format(CultureInfo.InvariantCulture, "{0} 监听 :{1} → {2}:{3}", srvName, lPort, dHost, dPort);
                    case TunnelType.Dynamic:
                        return string.Format(CultureInfo.InvariantCulture, "本机 SOCKS5 代理 {0}:{1} → {2}", lHost, lPort, srvName);
                    case TunnelType.Relay:
                        string relayName = Hosts.FirstOrDefault(h => h.Id == _destServerId)?.Name ?? "目标服务器";
                        return string.Format(CultureInfo.InvariantCulture, "{0} :{1} → {2} → {3}:{4}", srvName, lPort, relayName, dHost, dPort);
                    default:
                        return string.Empty;
                }
            }
        }

        public async Task LoadAsync(string tunnelId, string serverId = null)
        {
            // 加载主机列表
            Hosts.Clear();
            if (_services?.Hosts != null)
            {
                var hosts = await _services.Hosts.GetAllAsync().ConfigureAwait(true);
                foreach (var h in hosts)
                {
                    string name = string.IsNullOrWhiteSpace(h.Name) ? $"{h.Username}@{h.HostName}" : h.Name;
                    Hosts.Add(new IdNameOption(h.Id, name));
                }
            }

            // 加载分组列表
            Groups.Clear();
            string noGroup = _services?.GetString("TunnelEdit_NoGroup") ?? "未分组";
            Groups.Add(new IdNameOption(string.Empty, noGroup));
            if (_services?.Groups != null)
            {
                var groups = await _services.Groups.GetAllAsync().ConfigureAwait(true);
                foreach (var g in groups)
                {
                    Groups.Add(new IdNameOption(g.Id, g.Name));
                }
            }

            if (string.IsNullOrEmpty(tunnelId))
            {
                _isNew = true;
                _id = IdGenerator.NewId();
                _name = string.Empty;
                _type = TunnelType.Local;
                _serverId = serverId ?? Hosts.FirstOrDefault()?.Id ?? string.Empty;
                _groupId = string.Empty;
                _listenHost = "127.0.0.1";
                _listenPortText = "8080";
                _destHost = "127.0.0.1";
                _destPortText = "80";
                _destServerId = string.Empty;
                _autoReconnect = true;
                _autoStart = false;
                _enabled = true;
                _isDirty = false;
            }
            else
            {
                _isNew = false;
                _id = tunnelId;
                Tunnel tunnel = null;
                if (_services?.Tunnels != null)
                {
                    tunnel = await _services.Tunnels.GetByIdAsync(tunnelId).ConfigureAwait(true);
                }
                if (tunnel != null)
                {
                    _name = tunnel.Name ?? string.Empty;
                    _type = tunnel.Type;
                    _serverId = tunnel.ServerId ?? string.Empty;
                    _groupId = tunnel.GroupId ?? string.Empty;
                    _listenHost = tunnel.ListenHost ?? "127.0.0.1";
                    _listenPortText = tunnel.ListenPort.ToString(CultureInfo.InvariantCulture);
                    _destHost = tunnel.DestHost ?? "127.0.0.1";
                    _destPortText = tunnel.DestPort.ToString(CultureInfo.InvariantCulture);
                    _destServerId = tunnel.DestServerId ?? string.Empty;
                    _autoReconnect = tunnel.AutoReconnect;
                    _autoStart = tunnel.AutoStart;
                    _enabled = tunnel.Enabled;
                }
                _isDirty = false;
            }

            RaisePropertyChanged(string.Empty);
        }

        public string GetError(string propertyName)
        {
            _errors.TryGetValue(propertyName, out string error);
            return error;
        }

        private void ClearError(string propertyName)
        {
            if (_errors.Remove(propertyName))
            {
                RaisePropertyChanged("Error_" + propertyName);
            }
        }

        public string FirstErrorField()
        {
            return _errors.Keys.FirstOrDefault();
        }

        public async Task<bool> SaveAsync()
        {
            _errors.Clear();

            int listenPort;
            if (!int.TryParse(_listenPortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out listenPort) || listenPort < 1 || listenPort > 65535)
            {
                _errors["listenPort"] = ValidationKeys.PortRange;
            }

            int destPort = 0;
            if (_type != TunnelType.Dynamic)
            {
                if (!int.TryParse(_destPortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out destPort) || destPort < 1 || destPort > 65535)
                {
                    _errors["destPort"] = ValidationKeys.PortRange;
                }
            }

            var draft = new Tunnel
            {
                Id = _id,
                Name = string.IsNullOrWhiteSpace(_name) ? RoutePreview : _name.Trim(),
                Type = _type,
                ServerId = _serverId,
                GroupId = string.IsNullOrEmpty(_groupId) ? null : _groupId,
                ListenHost = string.IsNullOrWhiteSpace(_listenHost) ? "127.0.0.1" : _listenHost.Trim(),
                ListenPort = listenPort,
                DestHost = _type == TunnelType.Dynamic ? string.Empty : (_destHost ?? string.Empty).Trim(),
                DestPort = destPort,
                DestServerId = _type == TunnelType.Relay ? (_destServerId ?? string.Empty) : null,
                AutoReconnect = _autoReconnect,
                AutoStart = _autoStart,
                Enabled = _enabled
            };

            var valResult = TunnelValidator.Validate(draft, id => Hosts.Any(h => h.Id == id));
            foreach (var kv in valResult.Errors)
            {
                if (!_errors.ContainsKey(kv.Key))
                {
                    _errors[kv.Key] = kv.Value;
                }
            }

            if (_errors.Count > 0)
            {
                foreach (var k in _errors.Keys)
                {
                    RaisePropertyChanged("Error_" + k);
                }
                return false;
            }

            if (_services?.Tunnels != null)
            {
                if (_isNew)
                {
                    await _services.Tunnels.AddAsync(draft, ChangeOrigin.User).ConfigureAwait(true);
                }
                else
                {
                    await _services.Tunnels.UpdateAsync(draft, ChangeOrigin.User).ConfigureAwait(true);
                }
            }

            _isDirty = false;
            return true;
        }

        public async Task<bool> DeleteAsync()
        {
            if (_isNew || string.IsNullOrEmpty(_id) || _services?.Tunnels == null)
            {
                return false;
            }
            _services.TunnelManager?.Stop(_id);
            await _services.Tunnels.RemoveAsync(_id, ChangeOrigin.User).ConfigureAwait(true);
            _isDirty = false;
            return true;
        }
    }

    public sealed class TunnelTypeOption
    {
        public TunnelTypeOption(TunnelType type, string name)
        {
            Type = type;
            Name = name;
        }

        public TunnelType Type { get; }
        public string Name { get; }
    }
}
