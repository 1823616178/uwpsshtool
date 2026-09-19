using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.App.Views;
using SshTool.Core.Common;
using SshTool.Core.Hosts;
using SshTool.Core.Models;
using SshTool.Core.Mvvm;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;

namespace SshTool.App.ViewModels
{
    public sealed class HostListViewModel : ViewModelBase
    {
        private readonly HostRepository _hosts;
        private readonly GroupRepository _groups;
        private readonly TunnelRepository _tunnels;
        private readonly ConfigService _config;
        private readonly SettingsRepository _settings;
        private readonly IHostStatusProvider _status;
        private readonly Dictionary<string, Host> _hostById = new Dictionary<string, Host>(StringComparer.Ordinal);
        private string _searchText = string.Empty;
        private string _quickConnectText = string.Empty;
        private bool _isSearchOpen;
        private bool _isEmpty = true;
        private bool _hasNoMatches;
        private bool _canQuickConnectFromSearch;
        private bool _showQuickConnect = true;
        private bool _isListVisible;

        public HostListViewModel(AppServices services, IHostStatusProvider status = null)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }
            _hosts = services.Hosts;
            _groups = services.Groups;
            _tunnels = services.Tunnels;
            _config = services.Config;
            _settings = services.Settings;
            _status = status ?? NullHostStatusProvider.Instance;
            Groups = new ObservableCollection<HostListGroup>();

            NewHostCommand = new RelayCommand(() => OpenPlaceholder("新建主机", "M3"));
            SignInCommand = new RelayCommand(() => OpenPlaceholder("登录", "M5"));
            ToggleSearchCommand = new RelayCommand(ToggleSearch);
            QuickConnectCommand = new RelayCommand(QuickConnect, () => QuickConnectParser.TryParse(_quickConnectText, out _));
            QuickConnectFromSearchCommand = new RelayCommand(QuickConnectFromSearch, () => _canQuickConnectFromSearch);
            GenerateTestHostsCommand = new AsyncCommand(GenerateTestHostsAsync, onError: OnError);

            _hosts.Changed += OnRepoChanged;
            _groups.Changed += OnRepoChanged;
            _tunnels.Changed += OnRepoChanged;
            _settings.Changed += OnSettingChanged;
            var ignore = RefreshAsync();
        }

        public ObservableCollection<HostListGroup> Groups { get; private set; }

        public ICommand NewHostCommand { get; private set; }
        public ICommand SignInCommand { get; private set; }
        public ICommand ToggleSearchCommand { get; private set; }
        public ICommand QuickConnectCommand { get; private set; }
        public ICommand QuickConnectFromSearchCommand { get; private set; }
        public ICommand GenerateTestHostsCommand { get; private set; }

        public string SearchText
        {
            get { return _searchText; }
            set
            {
                if (SetProperty(ref _searchText, value ?? string.Empty))
                {
                    var ignore = RefreshAsync();
                }
            }
        }

        public string QuickConnectText
        {
            get { return _quickConnectText; }
            set
            {
                if (SetProperty(ref _quickConnectText, value ?? string.Empty))
                {
                    ((RelayCommand)QuickConnectCommand).RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsSearchOpen
        {
            get { return _isSearchOpen; }
            private set { SetProperty(ref _isSearchOpen, value); }
        }

        public bool IsEmpty
        {
            get { return _isEmpty; }
            private set { SetProperty(ref _isEmpty, value); }
        }

        public bool HasNoMatches
        {
            get { return _hasNoMatches; }
            private set { SetProperty(ref _hasNoMatches, value); }
        }

        public bool CanQuickConnectFromSearch
        {
            get { return _canQuickConnectFromSearch; }
            private set
            {
                if (SetProperty(ref _canQuickConnectFromSearch, value))
                {
                    ((RelayCommand)QuickConnectFromSearchCommand).RaiseCanExecuteChanged();
                }
            }
        }

        public bool ShowQuickConnect
        {
            get { return _showQuickConnect; }
            private set { SetProperty(ref _showQuickConnect, value); }
        }

        public bool IsListVisible
        {
            get { return _isListVisible; }
            private set { SetProperty(ref _isListVisible, value); }
        }

        public void ToggleSearch()
        {
            IsSearchOpen = !IsSearchOpen;
            ShowQuickConnect = _settings.ShowQuickConnect && !IsSearchOpen;
            if (!IsSearchOpen && _searchText.Length > 0)
            {
                SearchText = string.Empty;
            }
        }

        public bool CloseSearch()
        {
            if (!IsSearchOpen && _searchText.Length == 0)
            {
                return false;
            }
            IsSearchOpen = false;
            ShowQuickConnect = _settings.ShowQuickConnect && !IsSearchOpen;
            if (_searchText.Length > 0)
            {
                SearchText = string.Empty;
            }
            return true;
        }

        public void ToggleGroup(string groupId)
        {
            HashSet<string> current = HostListBuilder.DecodeCollapsed(_settings.HostGroupCollapsed);
            HashSet<string> next = HostListBuilder.ToggleCollapsed(current, groupId ?? HostListGroup.UngroupedId);
            _settings.HostGroupCollapsed = HostListBuilder.EncodeCollapsed(next);
            var ignore = RefreshAsync();
        }

        public void Connect(HostListRow row)
        {
            if (row == null)
            {
                return;
            }
            OpenPlaceholder("连接 " + row.Name, "M4");
        }

        public void NewSession(HostListRow row)
        {
            if (row == null)
            {
                return;
            }
            OpenPlaceholder("新会话 " + row.Name, "M4");
        }

        public void Edit(HostListRow row)
        {
            if (row == null)
            {
                return;
            }
            OpenPlaceholder("编辑主机", "M3");
        }

        public void OpenSftp(HostListRow row)
        {
            if (row == null)
            {
                return;
            }
            OpenPlaceholder("SFTP", "M7");
        }

        public async Task DuplicateAsync(HostListRow row)
        {
            if (row == null)
            {
                return;
            }
            Host source;
            if (!_hostById.TryGetValue(row.HostId, out source) || source == null)
            {
                return;
            }
            Host clone = source.Clone();
            clone.Id = IdGenerator.NewId();
            clone.Name = (source.Name ?? string.Empty) + " 副本";
            clone.LastConnectedAt = null;
            await _hosts.AddAsync(clone, ChangeOrigin.User).ConfigureAwait(true);
        }

        public async Task DeleteAsync(HostListRow row)
        {
            if (row == null)
            {
                return;
            }
            int tunnels = await CountTunnelsAsync(row.HostId).ConfigureAwait(true);
            string message = "删除主机「" + row.Name + "」？";
            if (tunnels > 0)
            {
                message += " 将同时删除 " + tunnels.ToString() + " 条隧道。";
            }
            ConfirmDialogResult confirm = await ConfirmDialog.ShowAsync(
                "删除主机", message, "删除", "取消", true).ConfigureAwait(true);
            if (!confirm.Confirmed)
            {
                return;
            }
            await _config.DeleteHostAsync(row.HostId).ConfigureAwait(true);
        }

        public async Task RefreshAsync()
        {
            IReadOnlyList<Host> hosts = await _hosts.GetAllAsync().ConfigureAwait(true);
            IReadOnlyList<HostGroup> groups = await _groups.GetAllAsync().ConfigureAwait(true);
            IReadOnlyList<Tunnel> tunnels = await _tunnels.GetAllAsync().ConfigureAwait(true);
            HostListSnapshot snap = HostListBuilder.Build(
                hosts, groups, tunnels, _searchText, _settings.HostSortMode,
                _settings.HostGroupCollapsed, _status);
            Apply(snap, hosts);
        }

        private void Apply(HostListSnapshot snap, IReadOnlyList<Host> hosts)
        {
            _hostById.Clear();
            if (hosts != null)
            {
                for (int i = 0; i < hosts.Count; i++)
                {
                    if (hosts[i] != null && !string.IsNullOrEmpty(hosts[i].Id))
                    {
                        _hostById[hosts[i].Id] = hosts[i];
                    }
                }
            }
            Groups.Clear();
            if (snap.Groups != null)
            {
                for (int i = 0; i < snap.Groups.Count; i++)
                {
                    Groups.Add(snap.Groups[i]);
                }
            }
            IsEmpty = snap.IsEmpty;
            HasNoMatches = snap.HasNoMatches;
            QuickConnectTarget ignored;
            CanQuickConnectFromSearch = snap.HasNoMatches && QuickConnectParser.TryParse(_searchText, out ignored);
            ShowQuickConnect = _settings.ShowQuickConnect && !IsSearchOpen;
            IsListVisible = !snap.IsEmpty && !snap.HasNoMatches;
        }

        private void QuickConnect()
        {
            QuickConnectTarget target;
            if (!QuickConnectParser.TryParse(_quickConnectText, out target))
            {
                return;
            }
            OpenPlaceholder("快速连接 " + target.Username + "@" + target.HostName, "M4");
        }

        private void QuickConnectFromSearch()
        {
            QuickConnectTarget target;
            if (!QuickConnectParser.TryParse(_searchText, out target))
            {
                return;
            }
            OpenPlaceholder("快速连接 " + target.Username + "@" + target.HostName, "M4");
        }

        private async Task GenerateTestHostsAsync()
        {
            HostGroup group = await EnsureTestGroupAsync().ConfigureAwait(true);
            var batch = new List<Host>(100);
            for (int i = 1; i <= 100; i++)
            {
                Host host = Defaults.NewHost();
                string n = i.ToString("000");
                host.Name = "test-" + n;
                host.HostName = "10.0." + (i / 50).ToString() + "." + (i % 50).ToString();
                host.Username = "user";
                host.Port = 22;
                if (i % 2 == 0)
                {
                    host.GroupId = group.Id;
                }
                if (i % 3 == 0)
                {
                    host.AuthType = AuthType.Key;
                }
                if (i % 5 == 0)
                {
                    host.TmuxAutoAttach = true;
                }
                batch.Add(host);
            }
            await _hosts.AddManyAsync(batch, ChangeOrigin.User).ConfigureAwait(true);
        }

        private async Task<HostGroup> EnsureTestGroupAsync()
        {
            IReadOnlyList<HostGroup> groups = await _groups.GetAllAsync().ConfigureAwait(true);
            for (int i = 0; i < groups.Count; i++)
            {
                if (string.Equals(groups[i].Name, "测试主机", StringComparison.Ordinal))
                {
                    return groups[i];
                }
            }
            HostGroup created = Defaults.NewGroup("测试主机");
            created.Order = 999;
            await _groups.AddAsync(created, ChangeOrigin.User).ConfigureAwait(true);
            return created;
        }

        private async Task<int> CountTunnelsAsync(string hostId)
        {
            int count = 0;
            IReadOnlyList<Tunnel> tunnels = await _tunnels.GetAllAsync().ConfigureAwait(true);
            for (int i = 0; i < tunnels.Count; i++)
            {
                Tunnel t = tunnels[i];
                if (t.ServerId == hostId || t.DestServerId == hostId)
                {
                    count++;
                }
            }
            return count;
        }

        private void OnRepoChanged(object sender, RepositoryChangedEventArgs e)
        {
            DispatcherHelper.Post(() =>
            {
                var ignore = RefreshAsync();
            });
        }

        private void OnSettingChanged(object sender, SettingChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }
            if (e.Key == "hostGroupCollapsed" || e.Key == "hostSortMode" || e.Key == "showQuickConnect")
            {
                DispatcherHelper.Post(() =>
                {
                    var ignore = RefreshAsync();
                });
            }
        }

        private void OnError(Exception ex)
        {
            if (ex == null)
            {
                return;
            }
            Logger.Log(LogLevel.Error, "HostList", ex.GetType().Name);
        }

        private void OpenPlaceholder(string title, string milestone)
        {
            Navigation.Navigate<PlaceholderPage>(new PlaceholderArgs(title, milestone));
        }
    }
}
