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
            this.Resuming += OnResumingCheckSeed;
            this.UnhandledException += OnUnhandledException;
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs e)
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
                }
                catch (Exception ex)
                {
                    Platform.FileLogger.Instance.Log(SshTool.Core.Common.LogLevel.Error, "App",
                        "启动失败 " + ex.GetType().Name);
                }
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

            var ignoreAutoTest = Views.Debug.SshAutoTest.RunIfSeedPresentAsync();
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                SshTool.Core.Common.ILogger logger;
                if (Infrastructure.ServiceRegistry.TryGet(out logger))
                {
                    logger.Log(SshTool.Core.Common.LogLevel.Error, "App",
                        "未处理异常 " + e.Exception.GetType().Name);
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

        private void OnResumingCheckSeed(object sender, object e)
        {
            var ignore = Views.Debug.SshAutoTest.RunIfSeedPresentAsync();
        }

        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                // D05 组合根将在此追加仓库刷盘；当前至少保证日志队列落盘（真机靠 app.log 诊断）
                if (Infrastructure.AppServices.Current != null)
                {
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
