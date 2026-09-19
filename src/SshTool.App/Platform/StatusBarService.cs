using System;
using Windows.Foundation.Metadata;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace SshTool.App.Platform
{
    public static class StatusBarService
    {
        public static void Hide()
        {
            if (!ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar"))
            {
                return;
            }
            try
            {
                var ignore = StatusBar.GetForCurrentView().HideAsync();
            }
            catch (Exception)
            {
            }
        }

        public static void ShowThemed()
        {
            if (!ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar"))
            {
                return;
            }
            try
            {
                StatusBar bar = StatusBar.GetForCurrentView();
                var ignore = bar.ShowAsync();
                bar.BackgroundOpacity = 1;
                var bg = Application.Current.Resources["AppBgBrush"] as SolidColorBrush;
                var fg = Application.Current.Resources["AppTextBrush"] as SolidColorBrush;
                if (bg != null)
                {
                    bar.BackgroundColor = bg.Color;
                }
                if (fg != null)
                {
                    bar.ForegroundColor = fg.Color;
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
