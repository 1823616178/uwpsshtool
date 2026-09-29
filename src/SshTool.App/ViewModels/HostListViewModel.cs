using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
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
        private bool _isEmpty = true;
        private bool _hasNoMatches;
        private bool _canQuickConnectFromSearch;
        private bool _showQuickConnect = true;
        private bool _quickConnectExpanded;
        private bool _isListVisible;
        private bool _detached;

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
            IHostStatusProvider resolved = status;
            if (resolved == null && services.Sessions != null)
            {
                resolved = services.Sessions;
            }
            _status = resolved ?? NullHostStatusProvider.Instance;
            Groups = new ObservableCollection<HostListGroup>();

            NewHostCommand = new RelayCommand(() => Navigation.Navigate<HostEditPage>(HostEditArgs.New()));
            SignInCommand = new RelayCommand(() => Navigation.Navigate<Views.Sync.LoginPage>());
            QuickConnectCommand = new RelayCommand(QuickConnect, () => QuickConnectParser.TryParse(_quickConnectText, out _));
            QuickConnectFromSearchCommand = new RelayCommand(QuickConnectFromSearch, () => _canQuickConnectFromSearch);
            ToggleQuickConnectCommand = new RelayCommand(ToggleQuickConnect);
            GenerateTestHostsCommand = new AsyncCommand(GenerateTestHostsAsync, onError: OnError);

            // O03：仓库与设置都是应用级单例，本 VM 随 MainPage 每次导航重建。
            // 不退订的话，每回一次首页就多一个永远活着、仍在响应 Changed 并
            // 触发 RefreshAsync 的死 VM（既泄漏、又是「返回后重复刷新」的成因）。
            _hosts.Changed += OnRepoChanged;
            _groups.Changed += OnRepoChanged;
            _tunnels.Changed += OnRepoChanged;
            _settings.Changed += OnSettingChanged;
            RefreshAsync().Forget("HostListViewModel.Refresh", AppLog.Logger);
        }

        public ObservableCollection<HostListGroup> Groups { get; private set; }

        public ICommand NewHostCommand { get; private set; }
        public ICommand SignInCommand { get; private set; }
        public ICommand QuickConnectCommand { get; private set; }
        public ICommand QuickConnectFromSearchCommand { get; private set; }
        public ICommand ToggleQuickConnectCommand { get; private set; }
        public ICommand GenerateTestHostsCommand { get; private set; }

        public string SearchText
        {
            get { return _searchText; }
            set
            {
                if (SetProperty(ref _searchText, value ?? string.Empty))
                {
                    RefreshAsync().Forget("HostListViewModel.Refresh", AppLog.Logger);
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

        // V02（05 §6.1）：快速连接折叠行 ↔ 展开表单。展开态记忆 hostQuickConnectExpanded。
        public bool QuickConnectExpanded
        {
            get { return _quickConnectExpanded; }
            private set { SetProperty(ref _quickConnectExpanded, value); }
        }

        public bool IsListVisible
        {
            get { return _isListVisible; }
            private set { SetProperty(ref _isListVisible, value); }
        }

        // V02：搜索框常驻后 back 没有「关闭搜索」可退，只负责清空非空搜索词。
        public bool CloseSearch()
        {
            if (_searchText.Length == 0)
            {
                return false;
            }
            SearchText = string.Empty;
            return true;
        }

        // 立即落属性给出反馈，再持久化（Settings.Changed 会触发 Refresh 复读到同值）。
        public void ToggleQuickConnect()
        {
            bool next = !_settings.HostQuickConnectExpanded;
            QuickConnectExpanded = next;
            _settings.HostQuickConnectExpanded = next;
        }

        public void ToggleGroup(string groupId)
        {
            HashSet<string> current = HostListBuilder.DecodeCollapsed(_settings.HostGroupCollapsed);
            HashSet<string> next = HostListBuilder.ToggleCollapsed(current, groupId ?? HostListGroup.UngroupedId);
            _settings.HostGroupCollapsed = HostListBuilder.EncodeCollapsed(next);
            RefreshAsync().Forget("HostListViewModel.Refresh", AppLog.Logger);
        }

        public void Connect(HostListRow row)
        {
            if (row == null)
            {
                return;
            }
            Navigation.Navigate<TerminalPage>(new TerminalArgs { HostId = row.HostId });
        }

        public void NewSession(HostListRow row)
        {
            if (row == null)
            {
                return;
            }
            Navigation.Navigate<TerminalPage>(new TerminalArgs { HostId = row.HostId });
        }

        public void Edit(HostListRow row)
        {
            if (row == null)
            {
                return;
            }
            Navigation.Navigate<HostEditPage>(HostEditArgs.Edit(row.HostId));
        }

        public void Duplicate(HostListRow row)
        {
            if (row == null)
            {
                return;
            }
            Navigation.Navigate<HostEditPage>(HostEditArgs.Duplicate(row.HostId));
        }

        public void OpenSftp(HostListRow row)
        {
            if (row == null)
            {
                return;
            }
            // F03：SFTP 页复用该主机的已连接会话，无则新建专用连接（无 shell）。
            Navigation.Navigate<SftpPage>(new SftpArgs { HostId = row.HostId });
        }

        // W03：收藏是本机专有字段（不进同步文档），切换后列表经 Changed 自动刷新。
        public async Task ToggleFavoriteAsync(HostListRow row)
        {
            Host host = row == null ? null : await _hosts.GetByIdAsync(row.HostId).ConfigureAwait(true);
            if (host == null)
            {
                return;
            }
            Host next = host.Clone();
            next.Favorite = !host.Favorite;
            await _hosts.UpdateAsync(next).ConfigureAwait(true);
        }

        // W03：固定到开始屏幕（系统弹确认）。
        public async Task PinAsync(HostListRow row)
        {
            Host host = row == null ? null : await _hosts.GetByIdAsync(row.HostId).ConfigureAwait(true);
            if (host != null)
            {
                await HostTileService.PinAsync(host).ConfigureAwait(true);
            }
        }

        // W03：磁贴 / ssh:// 激活交接。主机存在则直接连接；ssh:// 只预填快速连接并展开。
        public async Task ApplyLaunchRequestAsync(string hostId, string quickConnectText)
        {
            if (!string.IsNullOrEmpty(hostId))
            {
                Host host = await _hosts.GetByIdAsync(hostId).ConfigureAwait(true);
                if (host != null)
                {
                    Navigation.Navigate<TerminalPage>(new TerminalArgs { HostId = hostId });
                }
                else
                {
                    Logger.Log(LogLevel.Warning, "HostList", "磁贴指向的主机已不存在");
                }
                return;
            }
            if (!string.IsNullOrEmpty(quickConnectText))
            {
                QuickConnectText = quickConnectText;
                if (!_settings.ShowQuickConnect)
                {
                    _settings.ShowQuickConnect = true;
                }
                if (!_settings.HostQuickConnectExpanded)
                {
                    ToggleQuickConnect();
                }
            }
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

        // R03 (C-05)：刷新类操作「最后一次请求生效」——搜索/排序/分组/仓库变更都可
        // 并发触发 RefreshAsync；每次调用前取当前世代，await 后若世代已推进则丢弃本次结果，
        // 避免旧刷新覆盖新刷新。
        private int _refreshGeneration;

        public async Task RefreshAsync()
        {
            int generation = Interlocked.Increment(ref _refreshGeneration);
            IReadOnlyList<Host> hosts = await _hosts.GetAllAsync().ConfigureAwait(true);
            IReadOnlyList<HostGroup> groups = await _groups.GetAllAsync().ConfigureAwait(true);
            IReadOnlyList<Tunnel> tunnels = await _tunnels.GetAllAsync().ConfigureAwait(true);
            int current = Volatile.Read(ref _refreshGeneration);
            if (current != generation)
            {
                // 排队期间已有更新的刷新请求启动：本次结果丢弃，由新请求的最新一次 Apply。
                return;
            }
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
                    HostListGroup group = snap.Groups[i];
                    // W03：视图段的标题由 Core 给兜底文案，这里换成本地化标题。
                    if (group.GroupId == HostListGroup.FavoritesId)
                    {
                        group.Name = Localized.Get("Hosts_SectionFavorites", group.Name);
                    }
                    else if (group.GroupId == HostListGroup.RecentId)
                    {
                        group.Name = Localized.Get("Hosts_SectionRecent", group.Name);
                    }
                    else if (group.GroupId == HostListGroup.UngroupedId)
                    {
                        group.Name = Localized.Get("Hosts_SectionUngrouped", group.Name);
                    }
                    Groups.Add(group);
                }
            }
            IsEmpty = snap.IsEmpty;
            HasNoMatches = snap.HasNoMatches;
            QuickConnectTarget ignored;
            CanQuickConnectFromSearch = snap.HasNoMatches && QuickConnectParser.TryParse(_searchText, out ignored);
            ShowQuickConnect = _settings.ShowQuickConnect;
            QuickConnectExpanded = _settings.HostQuickConnectExpanded;
            IsListVisible = !snap.IsEmpty && !snap.HasNoMatches;
        }

        private void QuickConnect()
        {
            QuickConnectTarget target;
            if (!QuickConnectParser.TryParse(_quickConnectText, out target))
            {
                return;
            }
            Navigation.Navigate<TerminalPage>(new TerminalArgs { Quick = target });
        }

        private void QuickConnectFromSearch()
        {
            QuickConnectTarget target;
            if (!QuickConnectParser.TryParse(_searchText, out target))
            {
                return;
            }
            Navigation.Navigate<TerminalPage>(new TerminalArgs { Quick = target });
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

        // O03：由 MainViewModel.Detach 级联调用（页面 OnNavigatedFrom）。幂等。
        public void Detach()
        {
            if (_detached)
            {
                return;
            }
            _detached = true;
            _hosts.Changed -= OnRepoChanged;
            _groups.Changed -= OnRepoChanged;
            _tunnels.Changed -= OnRepoChanged;
            _settings.Changed -= OnSettingChanged;
        }

        private void OnRepoChanged(object sender, RepositoryChangedEventArgs e)
        {
            DispatcherHelper.Post(() =>
            {
                RefreshAsync().Forget("HostListViewModel.Refresh", AppLog.Logger);
            });
        }

        private void OnSettingChanged(object sender, SettingChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }
            if (e.Key == "hostGroupCollapsed" || e.Key == "hostSortMode"
                || e.Key == "showQuickConnect" || e.Key == "hostQuickConnectExpanded")
            {
                DispatcherHelper.Post(() =>
                {
                    RefreshAsync().Forget("HostListViewModel.Refresh", AppLog.Logger);
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

    }
}
