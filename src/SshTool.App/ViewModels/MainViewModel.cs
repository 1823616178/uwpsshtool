using System;
using System.Threading.Tasks;
using System.Windows.Input;
using SshTool.App.Infrastructure;
using SshTool.App.Views;
using SshTool.Core.Common;
using SshTool.Core.Mvvm;
using SshTool.Core.Sync;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace SshTool.App.ViewModels
{
    public sealed class MainViewModel : ViewModelBase
    {
        private string _syncIconGlyph;
        private Brush _syncIconForeground;
        private bool _isSyncSpinning;

        public MainViewModel()
        {
            // StateChanged 可能在线程池触发：属性通知统一封送回 UI 线程，
            // 避免 XAML 绑定跨线程读属性（见 ObservableObject.SetDispatcherPost）。
            SetDispatcherPost(action => DispatcherHelper.Post(action));
            AppServices services = AppServices.Current;
            if (services != null)
            {
                Hosts = new HostListViewModel(services);
                NewHostCommand = Hosts.NewHostCommand;
                SearchCommand = Hosts.ToggleSearchCommand;
                SignInCommand = Hosts.SignInCommand;
                SessionsPaneViewModel sessionsVm;
                if (!ServiceRegistry.TryGet(out sessionsVm))
                {
                    sessionsVm = new SessionsPaneViewModel(services);
                    ServiceRegistry.Register(sessionsVm);
                }
                Sessions = sessionsVm;
            }
            else
            {
                NewHostCommand = new RelayCommand(() => OpenPlaceholder("新建主机", "M3"));
                SearchCommand = new RelayCommand(() => OpenPlaceholder("搜索", "M3"));
                SignInCommand = new RelayCommand(() => Navigation.Navigate<Views.Sync.LoginPage>());
            }
            // S14：同步按钮=手动触发（U17 建 AccountSyncPage 后再加导航）。
            // 同步栈缺失（设计器/构造失败）时回退到占位页，保持可点击。
            SyncCommand = new AsyncCommand(ManualSyncAsync, onError: OnManualSyncError);
            SettingsCommand = new RelayCommand(() => Navigation.Navigate<SettingsPage>());
            KeysCommand = new RelayCommand(() => Navigation.Navigate<Views.Keys.KeysPage>());
            KnownHostsCommand = new RelayCommand(() => Navigation.Navigate<KnownHostsPage>());
            SnippetsCommand = new RelayCommand(() => Navigation.Navigate<SnippetsPage>(SnippetsArgs.Manage()));
            AppearanceCommand = new RelayCommand(() => Navigation.Navigate<AppearanceListPage>());
            AboutCommand = new RelayCommand(() => OpenPlaceholder("关于", "M8"));
            RefreshSyncIcon(services != null && services.Sync != null ? services.Sync.State : null);
            if (services != null && services.Sync != null)
            {
                services.Sync.StateChanged += OnSyncStateChanged;
            }
        }

        public HostListViewModel Hosts { get; private set; }
        public SessionsPaneViewModel Sessions { get; private set; }

        // S14：MainPage 同步图标绑定 SyncState（02-UI-DESIGN.md §5.1，映射见 SyncIconMap）。
        public string SyncIconGlyph
        {
            get { return _syncIconGlyph; }
            private set { SetProperty(ref _syncIconGlyph, value); }
        }

        // null = 默认前景（跟随 CommandBar）。
        public Brush SyncIconForeground
        {
            get { return _syncIconForeground; }
            private set { SetProperty(ref _syncIconForeground, value); }
        }

        public bool IsSyncSpinning
        {
            get { return _isSyncSpinning; }
            private set { SetProperty(ref _isSyncSpinning, value); }
        }

        public ICommand NewHostCommand { get; private set; }
        public ICommand SearchCommand { get; private set; }
        public ICommand SyncCommand { get; private set; }
        public ICommand SettingsCommand { get; private set; }
        public ICommand KeysCommand { get; private set; }
        public ICommand KnownHostsCommand { get; private set; }
        public ICommand SnippetsCommand { get; private set; }
        public ICommand AppearanceCommand { get; private set; }
        public ICommand AboutCommand { get; private set; }
        public ICommand SignInCommand { get; private set; }

        private void OpenPlaceholder(string title, string milestone)
        {
            Navigation.Navigate<PlaceholderPage>(new PlaceholderArgs(title, milestone));
        }

        private async Task ManualSyncAsync()
        {
            AppServices services = AppServices.Current;
            SyncTriggers triggers = services != null ? services.SyncTriggers : null;
            if (triggers == null)
            {
                OpenPlaceholder("同步", "M5");
                return;
            }
            // 结果经图标相位展示（syncing→synced/error），此处只等待完成。
            await triggers.RequestManualSyncAsync().ConfigureAwait(true);
        }

        private void OnManualSyncError(Exception ex)
        {
            try
            {
                Logger.Log(LogLevel.Warning, "Sync", "手动同步失败 " + ex.GetType().Name);
            }
            catch (Exception)
            {
            }
        }

        private void OnSyncStateChanged(SyncState state)
        {
            DispatcherHelper.Post(() => RefreshSyncIcon(state));
        }

        private void RefreshSyncIcon(SyncState state)
        {
            SyncIconSpec spec;
            try
            {
                spec = SyncIconMap.ForPhase(state == null ? SyncPhase.SignedOut : state.Phase);
            }
            catch (Exception)
            {
                spec = new SyncIconSpec { GlyphKey = "IconSyncError", BrushKey = "AppDangerBrush" };
            }
            SyncIconGlyph = ResolveGlyph(spec.GlyphKey);
            SyncIconForeground = ResolveBrush(spec.BrushKey);
            IsSyncSpinning = spec.Spin;
        }

        private static string ResolveGlyph(string key)
        {
            try
            {
                ResourceDictionary resources = Application.Current != null
                    ? Application.Current.Resources : null;
                if (resources != null && resources.ContainsKey(key))
                {
                    string value = resources[key] as string;
                    if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
            }
            catch (Exception)
            {
            }
            return "\uE895";
        }

        private static Brush ResolveBrush(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }
            try
            {
                ResourceDictionary resources = Application.Current != null
                    ? Application.Current.Resources : null;
                if (resources != null && resources.ContainsKey(key))
                {
                    return resources[key] as Brush;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }
    }
}
