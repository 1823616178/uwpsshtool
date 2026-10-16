using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using SshTool.App.Infrastructure;
using SshTool.App.Views;
using SshTool.Core.Common;
using SshTool.Core.Forwarding;
using SshTool.Core.Models;
using SshTool.Core.Mvvm;
using SshTool.Core.Storage;

namespace SshTool.App.ViewModels
{
    // M-2（02 §5.3）：隧道分组（按 Tunnel.GroupId，分组名取自 HostGroup）。
    public sealed class TunnelGroupViewModel
    {
        public string Name { get; set; }
        public ObservableCollection<TunnelItemViewModel> Rows { get; } =
            new ObservableCollection<TunnelItemViewModel>();
    }

    // F06：隧道 Pivot 视图模型（01-DESIGN §11.2；02-UI §5.3；05-CODE-AUDIT §6.1）。
    public sealed class TunnelsViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private readonly ObservableCollection<TunnelItemViewModel> _tunnels = new ObservableCollection<TunnelItemViewModel>();
        private readonly ObservableCollection<TunnelGroupViewModel> _groupedTunnels = new ObservableCollection<TunnelGroupViewModel>();
        private bool _isLoading;
        private bool _isEmpty;

        public TunnelsViewModel(AppServices services = null)
        {
            _services = services ?? AppServices.Current;
            Tunnels = new ReadOnlyObservableCollection<TunnelItemViewModel>(_tunnels);

            AddCommand = new RelayCommand(OnAdd);
            StopAllCommand = new RelayCommand(OnStopAll);
            RefreshCommand = new AsyncCommand(LoadAsync);

            if (_services?.TunnelManager != null)
            {
                _services.TunnelManager.StatusesChanged += OnStatusesChanged;
            }
            if (_services?.Tunnels != null)
            {
                _services.Tunnels.Changed += OnRepositoryChanged;
            }
        }

        public ReadOnlyObservableCollection<TunnelItemViewModel> Tunnels { get; }

        // M-2：分组后的隧道（供 CollectionViewSource 分组显示）。
        public ObservableCollection<TunnelGroupViewModel> GroupedTunnels => _groupedTunnels;

        public bool IsLoading
        {
            get => _isLoading;
            private set => SetProperty(ref _isLoading, value);
        }

        private bool _hasError;
        private string _errorMessage = string.Empty;

        public bool HasError
        {
            get => _hasError;
            private set => SetProperty(ref _hasError, value);
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            private set => SetProperty(ref _errorMessage, value);
        }

        public bool IsEmpty
        {
            get => _isEmpty;
            private set => SetProperty(ref _isEmpty, value);
        }

        public ICommand AddCommand { get; }
        public ICommand StopAllCommand { get; }
        public ICommand RefreshCommand { get; }

        public async Task LoadAsync()
        {
            if (_services?.Tunnels == null)
            {
                return;
            }
            IsLoading = true;
            try
            {
                var tunnelsList = await _services.Tunnels.GetAllAsync().ConfigureAwait(true);
                var hostsList = _services.Hosts != null ? await _services.Hosts.GetAllAsync().ConfigureAwait(true) : new List<Host>();
                var groupsList = _services.Groups != null ? await _services.Groups.GetAllAsync().ConfigureAwait(true) : new List<HostGroup>();

                var hostMap = hostsList.ToDictionary(h => h.Id, h => string.IsNullOrEmpty(h.Name) ? h.HostName : h.Name);
                var groupMap = groupsList.ToDictionary(g => g.Id, g => g.Name);

                _tunnels.Clear();
                foreach (var t in tunnelsList)
                {
                    string serverName = null;
                    if (!string.IsNullOrEmpty(t.ServerId))
                    {
                        hostMap.TryGetValue(t.ServerId, out serverName);
                    }
                    string groupName = null;
                    if (!string.IsNullOrEmpty(t.GroupId))
                    {
                        groupMap.TryGetValue(t.GroupId, out groupName);
                    }

                    var item = new TunnelItemViewModel(t, serverName, groupName);
                    if (_services.TunnelManager != null)
                    {
                        item.ApplyStatus(_services.TunnelManager.GetStatus(t.Id));
                    }
                    _tunnels.Add(item);
                }

                RebuildGroups();
                IsEmpty = _tunnels.Count == 0;
                HasError = false;
                ErrorMessage = string.Empty;
            }
            catch (Exception ex)
            {
                AppLog.Error("TunnelsViewModel", "LoadAsync failed", ex);
                HasError = true;
                ErrorMessage = ex.Message;
                IsEmpty = false;
            }
            finally
            {
                IsLoading = false;
            }
        }

        // M-2：按 GroupName 分组；未分组的归入「未分组」组（02 §5.3 线框的分组头）。
        private void RebuildGroups()
        {
            _groupedTunnels.Clear();
            string ungroupedName = Localized.Get("Tunnels_Ungrouped", "未分组");
            var map = new Dictionary<string, TunnelGroupViewModel>(StringComparer.Ordinal);
            var order = new List<TunnelGroupViewModel>();
            foreach (var item in _tunnels)
            {
                string name = string.IsNullOrEmpty(item.GroupName) ? ungroupedName : item.GroupName;
                TunnelGroupViewModel group;
                if (!map.TryGetValue(name, out group))
                {
                    group = new TunnelGroupViewModel { Name = name };
                    map[name] = group;
                    order.Add(group);
                }
                group.Rows.Add(item);
            }
            // 未分组沉底，其余按名称排序（与主机列表分组排序习惯一致）。
            foreach (var group in order.OrderBy(g => g.Name == ungroupedName ? 1 : 0).ThenBy(g => g.Name, StringComparer.Ordinal))
            {
                _groupedTunnels.Add(group);
            }
        }

        public async Task ToggleTunnelAsync(TunnelItemViewModel item, Func<Task<bool>> confirmLoopbackPrompt = null)
        {
            if (item == null || item.Tunnel.Type == TunnelType.Relay)
            {
                return;
            }

            var mgr = _services.TunnelManager;
            if (mgr == null)
            {
                return;
            }

            if (item.IsRunning)
            {
                mgr.Stop(item.Id);
            }
            else
            {
                // 如果是本地转发或动态转发，且未提示过回环隔离限制，触发提示
                if (confirmLoopbackPrompt != null && (item.Type == TunnelType.Local || item.Type == TunnelType.Dynamic))
                {
                    bool proceed = await confirmLoopbackPrompt().ConfigureAwait(true);
                    if (!proceed)
                    {
                        item.ApplyStatus(mgr.GetStatus(item.Id));
                        return;
                    }
                }

                var result = await mgr.StartAsync(item.Tunnel).ConfigureAwait(true);
                if (!result.Success)
                {
                    item.ApplyStatus(mgr.GetStatus(item.Id));
                }
            }
        }

        public void StopTunnel(string tunnelId)
        {
            _services.TunnelManager?.Stop(tunnelId);
        }

        public void StopAll()
        {
            _services.TunnelManager?.StopAll();
        }

        public async Task<bool> DeleteTunnelAsync(string tunnelId)
        {
            if (string.IsNullOrEmpty(tunnelId) || _services?.Tunnels == null)
            {
                return false;
            }
            try
            {
                _services.TunnelManager?.Stop(tunnelId);
                await _services.Tunnels.RemoveAsync(tunnelId, ChangeOrigin.User).ConfigureAwait(true);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("TunnelsViewModel", "DeleteTunnelAsync failed", ex);
                return false;
            }
        }

        private void OnStatusesChanged(object sender, EventArgs e)
        {
            var mgr = _services.TunnelManager;
            if (mgr == null)
            {
                return;
            }

            for (int i = 0; i < _tunnels.Count; i++)
            {
                _tunnels[i].ApplyStatus(mgr.GetStatus(_tunnels[i].Id));
            }
        }

        private void OnRepositoryChanged(object sender, RepositoryChangedEventArgs e)
        {
            LoadAsync().Forget("TunnelsViewModel.OnRepositoryChanged", AppLog.Logger);
        }

        private void OnAdd()
        {
            Navigation.Navigate<TunnelEditPage>(TunnelEditArgs.New());
        }

        private void OnStopAll()
        {
            StopAll();
        }

        public void Detach()
        {
            if (_services?.TunnelManager != null)
            {
                _services.TunnelManager.StatusesChanged -= OnStatusesChanged;
            }
            if (_services?.Tunnels != null)
            {
                _services.Tunnels.Changed -= OnRepositoryChanged;
            }
        }
    }
}
