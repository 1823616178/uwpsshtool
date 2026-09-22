using System;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
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
                (StatusBar.GetForCurrentView().HideAsync()).AsTask().Forget("StatusBarService.Hide", AppLog.Logger);
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
                (bar.ShowAsync()).AsTask().Forget("StatusBarService.Show", AppLog.Logger);
            }
            catch (Exception)
            {
            }
            RefreshTheme();
        }

        // A05：只刷新底色/前景（跟随 ThemeService.CurrentMode 的有效主题），不显隐。
        // 主题切换时由 ThemeService.Apply 调用；各页回到前台时也可调用。
        // 必须在 UI 线程调用（调用方保证：Apply / OnNavigatedTo 均在 UI 线程）。
        public static void RefreshTheme()
        {
            if (!ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar"))
            {
                return;
            }
            try
            {
                if (Application.Current == null)
                {
                    return;
                }
                string theme = ThemeService.EffectiveThemeKey();
                StatusBar bar = StatusBar.GetForCurrentView();
                if (bar == null)
                {
                    return;
                }
                bar.BackgroundOpacity = 1;
                SolidColorBrush bg = ThemeService.FindThemedBrush("AppBgBrush", theme);
                SolidColorBrush fg = ThemeService.FindThemedBrush("AppTextBrush", theme);
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
