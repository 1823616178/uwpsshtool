using System;
using SshTool.App.Controls;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using SshTool.App.Views.Debug;
using SshTool.Core;
using SshTool.Native;
using Windows.Foundation.Metadata;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    public sealed partial class MainPage : Page, IBackHandler
    {
        public MainPage()
        {
            ViewModel = new MainViewModel();
            this.InitializeComponent();
            if (ViewModel.Hosts != null)
            {
                HostsPane.Attach(ViewModel.Hosts);
                HostsPaneWide.Attach(ViewModel.Hosts);
                // U12 宽屏：左侧选主机即在右侧开标签，不跳页。
                HostsPaneWide.WorkspaceOpen = row =>
                {
                    WorkspaceViewModel workspace = EnsureWorkspace();
                    var ignore = workspace.OpenHostInNewTabAsync(row.HostId);
                    return true;
                };
            }
            if (ViewModel.Sessions != null)
            {
                SessionsPivotCtl.Attach(ViewModel.Sessions);
            }
            Workspace.Attach(EnsureWorkspace());
            ApplyStatusBar();
            ShowLoadWarnings();
            LogBuildInfo();
#if DEBUG_PAGES
            DebugConnectItem.Visibility = Visibility.Visible;
            DebugVaultItem.Visibility = Visibility.Visible;
            DebugSpikeItem.Visibility = Visibility.Visible;
            DebugTokenItem.Visibility = Visibility.Visible;
            DebugRenderItem.Visibility = Visibility.Visible;
            DebugInputItem.Visibility = Visibility.Visible;
            DebugPlatformItem.Visibility = Visibility.Visible;
            DebugBuildItem.Visibility = Visibility.Visible;
            DebugGenerateHostsItem.Visibility = Visibility.Visible;
#endif
        }

        public MainViewModel ViewModel { get; private set; }

        // U12：工作区单例经 ServiceRegistry 共享：切到终端页再回来 MainPage 重建，
        // 标签与窗格树不能丢（会话在 SessionManager 里，标签/树在这里）。
        private static WorkspaceViewModel EnsureWorkspace()
        {
            WorkspaceViewModel workspace;
            if (!ServiceRegistry.TryGet(out workspace))
            {
                workspace = new WorkspaceViewModel();
                ServiceRegistry.Register(workspace);
            }
            return workspace;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ApplyStatusBar();
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.RegisterBackHandler(this);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.UnregisterBackHandler(this);
            }
            base.OnNavigatedFrom(e);
        }

        // §4 规则 7：非「主机」Pivot 先切回主机；已在主机则交给系统退出。
        public bool HandleBack()
        {
            if (ViewModel.Hosts != null && ViewModel.Hosts.CloseSearch())
            {
                return true;
            }
            if (MainPivot.SelectedIndex != 0)
            {
                MainPivot.SelectedIndex = 0;
                return true;
            }
            // U09：有活跃会话时退出应用需确认；确认后 CloseAll 再退出。
            if (ViewModel.Sessions != null && AppServices.Current != null
                && AppServices.Current.Sessions != null
                && AppServices.Current.Sessions.ActiveSessionCount > 0)
            {
                var ignoreExit = ConfirmExitAsync();
                return true;
            }
            return false;
        }

        private async System.Threading.Tasks.Task ConfirmExitAsync()
        {
            bool exit = await ViewModel.Sessions.ConfirmExitIfNeededAsync();
            if (exit)
            {
                Application.Current.Exit();
            }
        }

        private void ShowLoadWarnings()
        {
            AppServices services = AppServices.Current;
            if (services == null || services.LoadWarnings.Count == 0)
            {
                return;
            }
            LoadWarningBanner.Severity = BannerSeverity.Warning;
            LoadWarningBanner.Title = "数据文件有告警";
            LoadWarningBanner.Message = services.LoadWarnings[0];
            LoadWarningBanner.Visibility = Visibility.Visible;
        }

        private void LogBuildInfo()
        {
            string build = DebugReport.BuildTag();
            string core = "Core: " + CoreInfo.Version;
            string native = "Native: " + NativeInfo.Version();
            string openssl = "OpenSSL: " + NativeInfo.OpenSslVersion();
            SshTool.Core.Common.ILogger logger;
            if (ServiceRegistry.TryGet(out logger))
            {
                logger.Log(SshTool.Core.Common.LogLevel.Info, "MainPage",
                    build + " | " + core + " | " + native + " | " + openssl);
            }
        }

        // U01 状态栏随主题（A05：实现收拢到 StatusBarService.RefreshTheme，
        // 与 ThemeService.Apply 共用同一套主题字典解析）。
        private void ApplyStatusBar()
        {
            Platform.StatusBarService.RefreshTheme();
        }

        private void OnNewClick(object sender, RoutedEventArgs e)
        {
            ViewModel.NewHostCommand.Execute(null);
        }

        // U12：宽屏主机列表折叠（§5.7 SplitView Inline）。
        private void OnPaneToggleClick(object sender, RoutedEventArgs e)
        {
            WideSplit.IsPaneOpen = !WideSplit.IsPaneOpen;
        }

        private void OnSearchClick(object sender, RoutedEventArgs e)
        {
            ViewModel.SearchCommand.Execute(null);
        }

        private void OnSyncClick(object sender, RoutedEventArgs e)
        {
            ViewModel.SyncCommand.Execute(null);
        }

        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            ViewModel.SettingsCommand.Execute(null);
        }

        private void OnKeysClick(object sender, RoutedEventArgs e)
        {
            ViewModel.KeysCommand.Execute(null);
        }

        private void OnKnownHostsClick(object sender, RoutedEventArgs e)
        {
            ViewModel.KnownHostsCommand.Execute(null);
        }

        private void OnSnippetsClick(object sender, RoutedEventArgs e)
        {
            ViewModel.SnippetsCommand.Execute(null);
        }

        private void OnAppearanceClick(object sender, RoutedEventArgs e)
        {
            ViewModel.AppearanceCommand.Execute(null);
        }

        private void OnAboutClick(object sender, RoutedEventArgs e)
        {
            ViewModel.AboutCommand.Execute(null);
        }

        private void OnSignInClick(object sender, RoutedEventArgs e)
        {
            ViewModel.SignInCommand.Execute(null);
        }

        private void OnDebugConnectClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(DebugConnectPage));
        }

        private void OnDebugVaultClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(VaultSelfCheckPage));
        }

        private void OnSpikeClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(SpikePage));
        }

        private void OnTokenGalleryClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(TokenGalleryPage));
        }

        private void OnRenderSpikeClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(RenderSpikePage));
        }

        private void OnInputSpikeClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(InputSpikePage));
        }

        private void OnPlatformSpikeClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(PlatformSpikePage));
        }

        private void OnBuildInfoClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(SshTool.App.MainPage));
        }

        private void OnGenerateHostsClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Hosts != null && ViewModel.Hosts.GenerateTestHostsCommand.CanExecute(null))
            {
                ViewModel.Hosts.GenerateTestHostsCommand.Execute(null);
            }
        }
    }
}
