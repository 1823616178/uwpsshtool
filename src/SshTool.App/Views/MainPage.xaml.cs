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
            HostsEmpty.PrimaryCommand = ViewModel.NewHostCommand;
            HostsEmpty.SecondaryCommand = ViewModel.SignInCommand;
            ApplyStatusBar();
            ShowLoadWarnings();
            LogBuildInfo();
#if DEBUG_PAGES
            DebugConnectItem.Visibility = Visibility.Visible;
            DebugSpikeItem.Visibility = Visibility.Visible;
            DebugTokenItem.Visibility = Visibility.Visible;
            DebugRenderItem.Visibility = Visibility.Visible;
            DebugInputItem.Visibility = Visibility.Visible;
            DebugPlatformItem.Visibility = Visibility.Visible;
            DebugBuildItem.Visibility = Visibility.Visible;
#endif
        }

        public MainViewModel ViewModel { get; private set; }

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
            if (MainPivot.SelectedIndex != 0)
            {
                MainPivot.SelectedIndex = 0;
                return true;
            }
            return false;
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

        private void ApplyStatusBar()
        {
            if (!ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar"))
            {
                return;
            }
            try
            {
                StatusBar bar = StatusBar.GetForCurrentView();
                bar.BackgroundOpacity = 1;
                object bg = Application.Current.Resources["AppBgBrush"];
                object fg = Application.Current.Resources["AppTextBrush"];
                var bgBrush = bg as SolidColorBrush;
                var fgBrush = fg as SolidColorBrush;
                if (bgBrush != null)
                {
                    bar.BackgroundColor = bgBrush.Color;
                }
                if (fgBrush != null)
                {
                    bar.ForegroundColor = fgBrush.Color;
                }
            }
            catch (Exception)
            {
            }
        }

        private void OnNewClick(object sender, RoutedEventArgs e)
        {
            ViewModel.NewHostCommand.Execute(null);
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
    }
}
