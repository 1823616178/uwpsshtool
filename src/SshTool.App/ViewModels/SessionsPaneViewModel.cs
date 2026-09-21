using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.App.Views;
using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Mvvm;
using SshTool.Core.Sessions;

namespace SshTool.App.ViewModels
{
    // 会话 Pivot 与终端页 SplitView 侧栏共用一个实例（经 ServiceRegistry 共享）。
    public sealed class SessionsPaneViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private SessionSnapshot _restore;

        public SessionsPaneViewModel(AppServices services)
        {
            _services = services;
            Items = new ObservableCollection<SessionInfo>();
            RestoreCommand = new RelayCommand(RestoreAll);
            NewSessionCommand = new RelayCommand(() =>
            {
                if (Navigation.CanGoBack)
                {
                    Navigation.GoBack();
                }
            });
            if (_services != null && _services.Sessions != null)
            {
                _services.Sessions.SessionsChanged += (s, e) => Reload();
                Reload();
            }
        }

        public ObservableCollection<SessionInfo> Items { get; private set; }
        public ICommand RestoreCommand { get; private set; }
        public ICommand NewSessionCommand { get; private set; }

        public bool HasRestore
        {
            get { return _restore != null && _restore.HostIds != null && _restore.HostIds.Count > 0; }
        }

        public int RestoreCount
        {
            get { return HasRestore ? _restore.HostIds.Count : 0; }
        }

        public void Reload()
        {
            Items.Clear();
            if (_services != null && _services.Sessions != null)
            {
                IReadOnlyList<SessionInfo> all = _services.Sessions.Sessions;
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i].State != SessionUiState.Closed)
                    {
                        Items.Add(all[i]);
                    }
                }
            }
            RaisePropertyChanged("Items");
        }

        public void Open(SessionInfo info)
        {
            if (info == null)
            {
                return;
            }
            // F03：无 shell 的 SFTP 专用连接附到终端是黑屏——改为按主机新开
            //（FindConnected 会跳过无 shell 会话并新建带 shell 的会话）。
            if (!info.ShellOpened && !string.IsNullOrEmpty(info.HostId))
            {
                Navigation.Navigate<TerminalPage>(new TerminalArgs { HostId = info.HostId });
                return;
            }
            Navigation.Navigate<TerminalPage>(new TerminalArgs { SessionId = info.SessionId });
        }

        public void Close(SessionInfo info)
        {
            if (info != null && _services.Sessions != null)
            {
                _services.Sessions.Close(info.SessionId);
            }
        }

        public async Task LoadRestoreAsync()
        {
            if (_services == null)
            {
                return;
            }
            var store = new SessionSnapshotStore(_services.FileSystem);
            IReadOnlyList<Host> hosts = await _services.Hosts.GetAllAsync().ConfigureAwait(true);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < hosts.Count; i++)
            {
                ids.Add(hosts[i].Id);
            }
            _restore = await store.LoadAsync(ids).ConfigureAwait(true);
            RaisePropertyChanged("HasRestore");
            RaisePropertyChanged("RestoreCount");
        }

        // 有活跃会话时退出确认；返回 true 表示可以退出（含用户确认后 CloseAll）。
        public async Task<bool> ConfirmExitIfNeededAsync()
        {
            if (_services == null || _services.Sessions == null)
            {
                return true;
            }
            int n = _services.Sessions.ActiveSessionCount;
            if (n <= 0)
            {
                return true;
            }
            ExitWithSessionsDialogResult result =
                await ExitWithSessionsDialog.ShowAsync(n).ConfigureAwait(true);
            if (!result.Exit)
            {
                return false;
            }
            _services.Sessions.CloseAll();
            return true;
        }

        private async void RestoreAll()
        {
            if (!HasRestore || _services == null || _services.Sessions == null)
            {
                return;
            }
            var ids = new List<string>(_restore.HostIds);
            _restore = new SessionSnapshot();
            RaisePropertyChanged("HasRestore");
            RaisePropertyChanged("RestoreCount");
            for (int i = 1; i < ids.Count; i++)
            {
                _services.Sessions.StartOpenAsync(
                    new SessionOpenRequest { HostId = ids[i] })
                    .Forget("SessionsPane.RestoreSession", AppLog.Logger);
            }
            Navigation.Navigate<TerminalPage>(new TerminalArgs { HostId = ids[0] });
        }
    }
}
