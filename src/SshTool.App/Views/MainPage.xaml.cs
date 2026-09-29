using System;
using System.ComponentModel;
using System.Collections.Generic;
using SshTool.App.Controls;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.App.ViewModels;
using SshTool.App.Views.Debug;
using SshTool.Core;
using SshTool.Core.Common;
using SshTool.Native;
using Windows.ApplicationModel.Resources;
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
        private static readonly ResourceLoader Loader = ResourceLoader.GetForCurrentView();

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
                    if (workspace != null)
                    {
                        workspace.OpenHostInNewTabAsync(row.HostId).Forget("MainPage.OpenHostInNewTab", AppLog.Logger);
                    }
                    return true;
                };
            }
            if (ViewModel.Sessions != null)
            {
                SessionsPivotCtl.Attach(ViewModel.Sessions);
                SessionsPivotCtl.ViewHostsRequested += (s, e) => MainPivot.SelectedIndex = 0;
            }
            Workspace.Attach(EnsureWorkspace());
            ApplyStatusBar();
            ShowLoadWarnings();
            LogBuildInfo();
            // S14：同步中图标旋转（SyncIconGlyph/Foreground 走 x:Bind，起停只能走代码）。
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateSyncSpin();
#if DEBUG_PAGES
            // V02：9 个调试入口集中到 DevToolsPage，主页溢出只留这一个（05 §6.1 Phase 2 出口）。
            DevToolsItem.Visibility = Visibility.Visible;
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
            ConsumeLaunchRequest();
            OfferSharedUploadsAsync().Forget("MainPage.OfferSharedUploads", AppLog.Logger);
        }

        // W05（01-DESIGN §16.5）：分享目标收下的文件在这里认领——选主机后打开 SFTP 页排队上传。
        private async System.Threading.Tasks.Task OfferSharedUploadsAsync()
        {
            AppServices services = AppServices.Current;
            if (services == null || await ShareInbox.CountAsync() == 0)
            {
                return;
            }
            IReadOnlyList<SshTool.Core.Models.Host> hosts = await services.Hosts.GetAllAsync();
            var list = new ListView { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true };
            foreach (SshTool.Core.Models.Host host in hosts)
            {
                list.Items.Add(new ListViewItem
                {
                    Content = string.IsNullOrEmpty(host.Name) ? host.HostName : host.Name,
                    Tag = host.Id
                });
            }
            var dialog = new ContentDialog
            {
                Title = Localized.Get("Share_PickHostTitle", "上传分享的文件到…"),
                Content = list,
                PrimaryButtonText = Localized.Get("Share_Later", "稍后"),
                SecondaryButtonText = Localized.Get("Share_Discard", "丢弃")
            };
            string pickedHostId = null;
            list.ItemClick += (s, a) =>
            {
                var item = list.ContainerFromItem(a.ClickedItem) as ListViewItem ?? a.ClickedItem as ListViewItem;
                pickedHostId = item != null ? item.Tag as string : null;
                dialog.Hide();
            };
            DialogService dialogs;
            ContentDialogResult result = ServiceRegistry.TryGet(out dialogs)
                ? await dialogs.ShowAsync(dialog)
                : await dialog.ShowAsync();
            if (pickedHostId != null)
            {
                IReadOnlyList<Windows.Storage.StorageFile> files = await ShareInbox.TakeAsync();
                NavigationService nav;
                if (files.Count > 0 && ServiceRegistry.TryGet(out nav))
                {
                    nav.Navigate<SftpPage>(new SftpArgs { HostId = pickedHostId, PendingUploads = files });
                }
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await ShareInbox.ClearAsync();
            }
        }

        // W03：磁贴 / ssh:// 激活交接（App 写入，这里取走一次）。
        public void ConsumeLaunchRequest()
        {
            LaunchRequest request = LaunchRequests.Take();
            if (request == null)
            {
                return;
            }
            // W04：断线通知 → 回到该会话（会话已关闭时 TerminalPage 自行回退）。
            if (!string.IsNullOrEmpty(request.SessionId))
            {
                NavigationService nav;
                if (ServiceRegistry.TryGet(out nav))
                {
                    nav.Navigate<TerminalPage>(new TerminalArgs { SessionId = request.SessionId });
                }
                return;
            }
            if (ViewModel.Hosts == null)
            {
                return;
            }
            if (request.QuickConnectText != null)
            {
                MainPivot.SelectedIndex = 0; // 快速连接在主机页签
            }
            ViewModel.Hosts.ApplyLaunchRequestAsync(request.HostId, request.QuickConnectText)
                .Forget("MainPage.ApplyLaunchRequest", AppLog.Logger);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.UnregisterBackHandler(this);
            }
            // O03：本页每次导航回来都重建（见 EnsureWorkspace 注释），而 Sessions
            // 与仓库/同步协调器都是应用级单例。不在这里拆，单例的订阅表就会按
            // 访问次数累积死页面与死 VM。Workspace 由控件自己的 Unloaded 拆。
            SessionsPivotCtl.Detach();
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.Detach();
            base.OnNavigatedFrom(e);
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null || string.IsNullOrEmpty(e.PropertyName)
                || string.Equals(e.PropertyName, "IsSyncSpinning", StringComparison.Ordinal))
            {
                UpdateSyncSpin();
            }
        }

        // S14：syncing 相位持续旋转，其他相位停在 0°。Storyboard 操作一律吞异常：
        // 页面卸载竞态下不值得为一个装饰动画崩溃。
        private void UpdateSyncSpin()
        {
            try
            {
                if (ViewModel != null && ViewModel.IsSyncSpinning)
                {
                    SyncSpinStory.Begin();
                }
                else
                {
                    SyncSpinStory.Stop();
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("MainPage", "同步旋转动画控制失败", ex);
            }
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
                // R03 (C-05)：退出确认是 fire-and-forget，经 Forget 统一观察。
                ConfirmExitAsync().Forget("MainPage.ConfirmExit", AppLog.Logger);
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
            LoadWarningBanner.Title = Loader.GetString("Main_LoadWarningTitle");
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

        // V02：底栏「快速连接」切换 HostsPivot 的展开区（窄屏 HostsPane / 宽屏 HostsPaneWide
        // 共用同一 ViewModel，展开态一致）。
        private void OnQuickConnectClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.ToggleQuickConnectCommand.CanExecute(null))
            {
                ViewModel.ToggleQuickConnectCommand.Execute(null);
            }
        }

        // U12：宽屏主机列表折叠（§5.7 SplitView Inline）。
        private void OnPaneToggleClick(object sender, RoutedEventArgs e)
        {
            WideSplit.IsPaneOpen = !WideSplit.IsPaneOpen;
        }

        private void OnSyncClick(object sender, RoutedEventArgs e)
        {
            ViewModel.SyncCommand.Execute(null);
        }

        private void OnToolsClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ToolsCommand.Execute(null);
        }

        private void OnAboutClick(object sender, RoutedEventArgs e)
        {
            ViewModel.AboutCommand.Execute(null);
        }

        private void OnSignInClick(object sender, RoutedEventArgs e)
        {
            ViewModel.SignInCommand.Execute(null);
        }

        private void OnDevToolsClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(DevToolsPage));
        }
    }
}
