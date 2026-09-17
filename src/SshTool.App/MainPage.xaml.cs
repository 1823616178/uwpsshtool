using SshTool.Core;
using SshTool.Native;
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
#if DEBUG_PAGES   // Debug 默认开；Release 需 -p:EnableDebugPages=true（见 csproj 注释）
            SpikeButton.Visibility = Windows.UI.Xaml.Visibility.Visible;
            TokenGalleryButton.Visibility = Windows.UI.Xaml.Visibility.Visible;
            RenderSpikeButton.Visibility = Windows.UI.Xaml.Visibility.Visible;
#endif
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
    }
}
