using System;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.Core.Hosts;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App
{
    /// <summary>
    /// 提供特定于应用程序的行为，以补充默认的应用程序类。
    /// </summary>
    sealed partial class App : Application
    {
        private Platform.DisconnectNotifier _disconnectNotifier;

        public App()
        {
            this.InitializeComponent();
            this.Suspending += OnSuspending;
#if DEBUG_PAGES
            // SshAutoTest 会自动接受主机密钥，只允许在调试构建里存在（opt/full-pass）。
            this.Resuming += OnResumingCheckSeed;
#endif
            this.UnhandledException += OnUnhandledException;
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            // W03：从主机磁贴启动时 Arguments = "host:<id>"。
            string hostId;
            if (HostLaunchLinks.TryParseTileArguments(e.Arguments, out hostId))
            {
                Infrastructure.LaunchRequests.RequestHost(hostId);
            }
            Frame rootFrame = await EnsureStartedAsync();

            if (e.PrelaunchActivated == false)
            {
                ShowMainPage(rootFrame, hostId != null);
                Window.Current.Activate();
                Platform.AppLockService.Instance.OnColdStart();
            }

#if DEBUG_PAGES
            Views.Debug.SshAutoTest.RunIfSeedPresentAsync().Forget("App.RunIfSeedPresent", AppLog.Logger);
#endif
        }

        // W03：ssh:// 协议激活；W04：点断线通知。冷启动或运行中都会走这里。
        protected override async void OnActivated(IActivatedEventArgs args)
        {
            bool handled = false;
            var protocol = args as ProtocolActivatedEventArgs;
            string quick;
            if (protocol != null && protocol.Uri != null
                && HostLaunchLinks.TryParseSshUri(protocol.Uri.OriginalString, out quick))
            {
                Infrastructure.LaunchRequests.RequestQuickConnect(quick);
                handled = true;
            }
            var toast = args as ToastNotificationActivatedEventArgs;
            if (toast != null && toast.Argument != null
                && toast.Argument.StartsWith(Platform.DisconnectNotifier.LaunchPrefix, StringComparison.Ordinal))
            {
                Infrastructure.LaunchRequests.RequestSession(
                    toast.Argument.Substring(Platform.DisconnectNotifier.LaunchPrefix.Length));
                handled = true;
            }
            Frame rootFrame = await EnsureStartedAsync();
            ShowMainPage(rootFrame, handled);
            Window.Current.Activate();
            Platform.AppLockService.Instance.OnColdStart();
        }

        // W05：分享目标在独立视图里激活；这里只收文件进收件箱，不启动应用服务
        // （会话、调度器都绑主视图，在分享视图里起服务会让它们随分享窗口一起失效）。
        protected override void OnShareTargetActivated(ShareTargetActivatedEventArgs args)
        {
            var frame = new Frame();
            frame.Navigate(typeof(Views.ShareTargetPage), args.ShareOperation);
            Window.Current.Content = frame;
            Window.Current.Activate();
        }

        // 首次激活时建 Frame、注册服务并启动；后续激活直接返回已有 Frame。
        private async Task<Frame> EnsureStartedAsync()
        {
            Frame rootFrame = Window.Current.Content as Frame;

            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }

            Infrastructure.DispatcherHelper.Initialize(Window.Current.Dispatcher);

            Infrastructure.NavigationService navigation;
            if (!Infrastructure.ServiceRegistry.TryGet(out navigation))
            {
                Infrastructure.ServiceRegistry.Register(new Infrastructure.DialogService());
                navigation = new Infrastructure.NavigationService();
                Infrastructure.ServiceRegistry.Register(navigation);
                var services = Infrastructure.AppServices.Initialize();
                try
                {
                    await services.StartAsync();
                    // P01：服务就绪后再接生命周期事件（Start 内部订阅
                    // EnteredBackground/LeavingBackground/Suspending/Resuming）。
                    if (services.Lifecycle != null)
                    {
                        services.Lifecycle.Start();
                    }
                    // W04：后台断线通知（与应用同寿命）。应用锁见下方 finally。
                    Platform.LifecycleService lifecycle = services.Lifecycle;
                    _disconnectNotifier = new Platform.DisconnectNotifier(
                        services.Sessions, services.Settings,
                        () => lifecycle != null && lifecycle.IsInBackground);
                    _disconnectNotifier.Start();
                    // W05：回收「用其他应用打开」缓存与已取走的分享批次。
                    Platform.SftpOpenCache.CleanupAsync().Forget("App.SftpOpenCacheCleanup", AppLog.Logger);
                    Platform.ShareInbox.CleanupAsync().Forget("App.ShareInboxCleanup", AppLog.Logger);
                }
                catch (Exception ex)
                {
                    Platform.FileLogger.Instance.Log(SshTool.Core.Common.LogLevel.Error, "App",
                        "启动失败 " + ex.GetType().Name);
                }
                finally
                {
                    // fix/functional-pass（P2-5）：应用锁必须与启动成败无关——此前放在 try 里，
                    // StartAsync 中途抛错（如某个数据文件读失败）就不会挂上应用锁，
                    // 开了「应用锁」的用户冷启动可直接进入。设置仓库在 Initialize 里已就绪。
                    try
                    {
                        Platform.AppLockService.Instance.Start(services.Settings);
                    }
                    catch (Exception lockEx)
                    {
                        Platform.FileLogger.Instance.Log(SshTool.Core.Common.LogLevel.Error, "App",
                            "app lock start failed " + lockEx.GetType().Name);
                    }
                }
            }
            navigation.Initialize(rootFrame);
            return rootFrame;
        }

        // 冷启动进主页；已在运行且有待办激活请求时重新进入主页，由 MainPage.OnNavigatedTo 取走请求。
        private static void ShowMainPage(Frame rootFrame, bool hasLaunchRequest)
        {
            if (rootFrame.Content == null || (hasLaunchRequest && !(rootFrame.Content is Views.MainPage)))
            {
                rootFrame.Navigate(typeof(Views.MainPage));
            }
            else if (hasLaunchRequest)
            {
                // 已在主页：直接交给当前主页处理。
                ((Views.MainPage)rootFrame.Content).ConsumeLaunchRequest();
            }
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                SshTool.Core.Common.ILogger logger;
                if (Infrastructure.ServiceRegistry.TryGet(out logger))
                {
                    // 脱敏惯例同 AppLog：不记 ex.Message（可能含主机名/路径）；
                    // HResult 与调用栈只含方法名，是真机上定位崩溃的唯一线索。
                    Exception ex = e.Exception;
                    string detail = ex == null
                        ? string.Empty
                        : ex.GetType().Name + " hr=0x" + ex.HResult.ToString("X8")
                          + (ex.StackTrace != null ? "\n" + ex.StackTrace : string.Empty);
                    logger.Log(SshTool.Core.Common.LogLevel.Error, "App", "未处理异常 " + detail);
                }
            }
            catch
            {
            }
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

#if DEBUG_PAGES
        private void OnResumingCheckSeed(object sender, object e)
        {
            Views.Debug.SshAutoTest.RunIfSeedPresentAsync().Forget("App.RunIfSeedPresent", AppLog.Logger);
        }
#endif

        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                // U09：先写会话快照，再刷仓库与日志队列（真机靠 app.log 诊断）
                if (Infrastructure.AppServices.Current != null)
                {
                    await Infrastructure.AppServices.Current.SaveSessionSnapshotAsync();
                    await Infrastructure.AppServices.Current.FlushAsync();
                }
                else
                {
                    await Platform.FileLogger.Instance.FlushAsync();
                }
            }
            catch
            {
                // 挂起路径不抛异常，否则进程被判定为挂起失败
            }
            finally
            {
                deferral.Complete();
            }
        }
    }
}
