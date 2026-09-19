using SshTool.App.Controls;
using SshTool.App.Infrastructure;
using SshTool.Core;
using SshTool.Native;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App
{
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            this.InitializeComponent();
            CoreVersionText.Text = "Core: " + CoreInfo.Version;
            NativeVersionText.Text = "Native: " + NativeInfo.Version();
            OpenSslVersionText.Text = "OpenSSL: " + NativeInfo.OpenSslVersion();
            // 手机上分辨「装的是哪一次构建」：包版本 + 配置 + 工具链。
            // 所有构建都叫 0.1.0.0 时同版本号旁加载可能不替换旧包（SP04 为此返工过）。
            BuildText.Text = Views.Debug.DebugReport.BuildTag();
            ShowLoadWarnings();

            // 同时写进 app.log：这样真机上「native 是否加载、OpenSSL 是否链上」可以从日志自证，
            // 不必靠人盯着屏幕念版本号（SP02 的 📱 验收即据此结）。
            SshTool.Core.Common.ILogger logger;
            if (Infrastructure.ServiceRegistry.TryGet(out logger))
            {
                logger.Log(SshTool.Core.Common.LogLevel.Info, "MainPage",
                    BuildText.Text + " | " + CoreVersionText.Text + " | "
                    + NativeVersionText.Text + " | " + OpenSslVersionText.Text);
            }
#if DEBUG_PAGES   // Debug 默认开；Release 需 -p:EnableDebugPages=true（见 csproj 注释）
            SpikeButton.Visibility = Windows.UI.Xaml.Visibility.Visible;
            TokenGalleryButton.Visibility = Windows.UI.Xaml.Visibility.Visible;
            RenderSpikeButton.Visibility = Windows.UI.Xaml.Visibility.Visible;
            InputSpikeButton.Visibility = Windows.UI.Xaml.Visibility.Visible;
            PlatformSpikeButton.Visibility = Windows.UI.Xaml.Visibility.Visible;
            DebugConnectButton.Visibility = Windows.UI.Xaml.Visibility.Visible;
#endif
        }

        private void ShowLoadWarnings()
        {
            AppServices services = AppServices.Current;
            if (services == null || services.LoadWarnings.Count == 0 || LoadWarningBanner == null)
            {
                return;
            }
            LoadWarningBanner.Severity = BannerSeverity.Warning;
            LoadWarningBanner.Title = "数据文件有告警";
            LoadWarningBanner.Message = services.LoadWarnings[0];
            LoadWarningBanner.Visibility = Visibility.Visible;
        }

        private void OnSpikeClick(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            Frame.Navigate(typeof(Views.Debug.SpikePage));
        }

        private void OnTokenGalleryClick(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            Frame.Navigate(typeof(Views.Debug.TokenGalleryPage));
        }

        private void OnRenderSpikeClick(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            Frame.Navigate(typeof(Views.Debug.RenderSpikePage));
        }

        private void OnInputSpikeClick(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            Frame.Navigate(typeof(Views.Debug.InputSpikePage));
        }

        private void OnPlatformSpikeClick(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            Frame.Navigate(typeof(Views.Debug.PlatformSpikePage));
        }

        private void OnDebugConnectClick(object sender, Windows.UI.Xaml.RoutedEventArgs e)
        {
            Frame.Navigate(typeof(Views.Debug.DebugConnectPage));
        }
    }
}
