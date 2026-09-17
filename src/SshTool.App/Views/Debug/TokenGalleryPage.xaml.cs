using SshTool.App.Platform;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Views.Debug
{
    public sealed partial class TokenGalleryPage : Page
    {
        public TokenGalleryPage()
        {
            this.InitializeComponent();
        }

        private void OnThemeDark(object sender, RoutedEventArgs e)
        {
            ThemeService.Apply(AppThemeMode.Dark);
        }

        private void OnThemeLight(object sender, RoutedEventArgs e)
        {
            ThemeService.Apply(AppThemeMode.Light);
        }

        private void OnThemeSystem(object sender, RoutedEventArgs e)
        {
            ThemeService.Apply(AppThemeMode.System);
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private async void OnOverlayDemo(object sender, RoutedEventArgs e)
        {
            DemoOverlay.IsActive = true;
            await System.Threading.Tasks.Task.Delay(2000);
            DemoOverlay.IsActive = false;
        }

        private void OnToastDemo(object sender, RoutedEventArgs e)
        {
            DemoToast.Show("已复制到剪贴板");
        }
    }
}
