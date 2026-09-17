using System;
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

        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            Frame rootFrame = Window.Current.Content as Frame;

            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }

            Platform.ThemeService.Initialize();
            await Platform.AppConfig.LoadAsync();
            Platform.FileLogger.Instance.Log(SshTool.Core.Common.LogLevel.Info, "App", "应用启动");

            // X07：应用基础设施（D05 组合根在后续任务统一接管）
            Infrastructure.ServiceRegistry.Register<SshTool.Core.Common.ILogger>(Platform.FileLogger.Instance);
            Infrastructure.ServiceRegistry.Register(new Infrastructure.DialogService());
            Infrastructure.ServiceRegistry.Register(new Infrastructure.NavigationService());
            Infrastructure.ServiceRegistry.Get<Infrastructure.NavigationService>().Initialize(rootFrame);

            if (e.PrelaunchActivated == false)
            {
                if (rootFrame.Content == null)
                {
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                }
                Window.Current.Activate();
            }
        }

        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            // X02 骨架：暂无挂起时要保存的状态；D05 组合根将在此刷盘。
            deferral.Complete();
        }
    }
}
