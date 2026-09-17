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
#if DEBUG
            SpikeButton.Visibility = Windows.UI.Xaml.Visibility.Visible;
            TokenGalleryButton.Visibility = Windows.UI.Xaml.Visibility.Visible;
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
    }
}
