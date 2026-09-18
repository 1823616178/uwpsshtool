using System;
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
        public App()
        {
            this.InitializeComponent();
            this.Suspending += OnSuspending;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            Frame rootFrame = Window.Current.Content as Frame;

            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }

            // 后台线程封送要用的 UI 线程 CoreDispatcher（Window.Current 是线程静态的，后台拿不到）
            Infrastructure.DispatcherHelper.Initialize(Window.Current.Dispatcher);
            Platform.ThemeService.Initialize();

            // X07：应用基础设施（D05 组合根在后续任务统一接管）。
            // OnLaunched 可能再次触发（应用在前台时从磁贴/协议重新激活），服务必须复用已注册实例，
            // 否则旧 NavigationService 仍挂在 BackRequested 上，一次返回被处理两次。
            Infrastructure.NavigationService navigation;
            if (!Infrastructure.ServiceRegistry.TryGet(out navigation))
            {
                Infrastructure.ServiceRegistry.Register<SshTool.Core.Common.ILogger>(Platform.FileLogger.Instance);
                Infrastructure.ServiceRegistry.Register(new Infrastructure.DialogService());
                navigation = new Infrastructure.NavigationService();
                Infrastructure.ServiceRegistry.Register(navigation);
            }
            navigation.Initialize(rootFrame);

            if (e.PrelaunchActivated == false)
            {
                if (rootFrame.Content == null)
                {
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                }
                Window.Current.Activate();
            }

            // 激活之后再读包内配置：UWP 对激活有超时，启动路径上不做阻塞 IO
            //（配置只影响日志级别与同步地址，晚一拍无影响）。
            var ignore = LoadConfigAsync();
            // SP03：发现 ssh-autotest.json 则跑无人值守 SSH 测试（读后即删，详见该类注释）
            var ignoreAutoTest = Views.Debug.SshAutoTest.RunIfSeedPresentAsync();
        }

        private static async Task LoadConfigAsync()
        {
            await Platform.AppConfig.LoadAsync();
            Platform.FileLogger.Instance.Log(SshTool.Core.Common.LogLevel.Info, "App", "应用启动");
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                // D05 组合根将在此追加仓库刷盘；当前至少保证日志队列落盘（真机靠 app.log 诊断）
                await Platform.FileLogger.Instance.FlushAsync();
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
