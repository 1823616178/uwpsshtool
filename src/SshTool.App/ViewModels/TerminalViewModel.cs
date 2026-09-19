using System;
using System.Threading.Tasks;
using System.Windows.Input;
using SshTool.App.Infrastructure;
using SshTool.App.Views;
using SshTool.Core.Hosts;
using SshTool.Core.Mvvm;
using SshTool.Core.Sessions;

namespace SshTool.App.ViewModels
{
    public sealed class TerminalArgs
    {
        public string SessionId { get; set; }
        public string HostId { get; set; }
        public QuickConnectTarget Quick { get; set; }
        public int Cols { get; set; }
        public int Rows { get; set; }

        public TerminalArgs()
        {
            Cols = 80;
            Rows = 24;
        }
    }

    public sealed class TerminalViewModel : ViewModelBase
    {
        private SessionInfo _session;
        private bool _infoCollapsed;

        public TerminalViewModel()
        {
            ToggleInfoCommand = new RelayCommand(() => InfoCollapsed = !InfoCollapsed);
            DisconnectCommand = new RelayCommand(Disconnect);
            CloseSessionCommand = new RelayCommand(CloseSession);
            PasteCommand = new RelayCommand(() => { });
            // U13：终端菜单/键条的片段入口直接由 TerminalPage 打开选择器；
            // 此命令保留给其他绑定方：带当前会话进入片段管理页（可发送）。
            SnippetsCommand = new RelayCommand(() =>
                Navigation.Navigate<SnippetsPage>(
                    SnippetsArgs.WithSession(_session == null ? null : _session.SessionId)));
        }

        public ICommand ToggleInfoCommand { get; private set; }
        public ICommand DisconnectCommand { get; private set; }
        public ICommand CloseSessionCommand { get; private set; }
        public ICommand PasteCommand { get; private set; }
        public ICommand SnippetsCommand { get; private set; }

        public SessionInfo Session
        {
            get { return _session; }
            private set { SetProperty(ref _session, value); }
        }

        public bool InfoCollapsed
        {
            get { return _infoCollapsed; }
            set { SetProperty(ref _infoCollapsed, value); }
        }

        public string AddressLine
        {
            get
            {
                if (_session == null)
                {
                    return string.Empty;
                }
                return (_session.Username ?? "") + "@" + (_session.HostName ?? "") + ":" + _session.Port.ToString();
            }
        }

        public async Task LoadAsync(TerminalArgs args)
        {
            SessionManager manager = AppServices.Current.Sessions;
            if (manager == null || args == null)
            {
                return;
            }
            if (!string.IsNullOrEmpty(args.SessionId))
            {
                Session = Find(manager, args.SessionId);
                return;
            }
            if (!string.IsNullOrEmpty(args.HostId))
            {
                SessionInfo existing = FindConnected(manager, args.HostId);
                if (existing != null)
                {
                    Session = existing;
                    return;
                }
                Session = await manager.StartOpenAsync(new SessionOpenRequest
                {
                    HostId = args.HostId,
                    Cols = args.Cols,
                    Rows = args.Rows
                }).ConfigureAwait(true);
                return;
            }
            if (args.Quick != null)
            {
                Session = await manager.StartOpenAsync(new SessionOpenRequest
                {
                    QuickConnect = args.Quick,
                    Cols = args.Cols,
                    Rows = args.Rows
                }).ConfigureAwait(true);
            }
        }

        public void AttachNative(Action<ISshSession> attach)
        {
            if (attach != null && _session != null)
            {
                attach(_session.NativeSession);
                _session.PropertyChanged += (s, e) =>
                {
                    // NativeSession 切换会进 TerminalView.Session（碰 Canvas/DispatcherTimer），
                    // 必须回 UI 线程；SessionInfo 已封送一次，这里是双保险
                    //（DispatcherHelper.Post 在 UI 线程是同步直行，无开销）。
                    DispatcherHelper.Post(() =>
                    {
                        if (e.PropertyName == "NativeSession" || string.IsNullOrEmpty(e.PropertyName))
                        {
                            attach(_session.NativeSession);
                        }
                        RaisePropertyChanged("AddressLine");
                    });
                };
            }
        }

        private void Disconnect()
        {
            if (_session != null && AppServices.Current.Sessions != null)
            {
                AppServices.Current.Sessions.CancelReconnect(_session.SessionId);
                if (_session.NativeSession != null)
                {
                    _session.NativeSession.Close();
                }
            }
        }

        private void CloseSession()
        {
            if (_session != null && AppServices.Current.Sessions != null)
            {
                AppServices.Current.Sessions.Close(_session.SessionId);
            }
            Navigation.GoBack();
        }

        private static SessionInfo Find(SessionManager manager, string id)
        {
            for (int i = 0; i < manager.Sessions.Count; i++)
            {
                if (manager.Sessions[i].SessionId == id)
                {
                    return manager.Sessions[i];
                }
            }
            return null;
        }

        private static SessionInfo FindConnected(SessionManager manager, string hostId)
        {
            for (int i = 0; i < manager.Sessions.Count; i++)
            {
                SessionInfo s = manager.Sessions[i];
                if (string.Equals(s.HostId, hostId, StringComparison.Ordinal)
                    && s.State == SessionUiState.Connected)
                {
                    return s;
                }
            }
            return null;
        }
    }
}
