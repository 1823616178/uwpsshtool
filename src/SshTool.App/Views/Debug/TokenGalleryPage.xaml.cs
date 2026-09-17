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
    }
}
