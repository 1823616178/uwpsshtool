using System;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace SshTool.App.Platform
{
    public enum AppThemeMode
    {
        System = 0,
        Light = 1,
        Dark = 2
    }

    // 02-UI-DESIGN.md §2.1：应用主题与系统强调色。
    // 主题切换 = 根 Frame 的 RequestedTheme；UseSystemAccent 时用 UISettings 的
    // 强调色覆盖 AppAccentBrush / KeyBarKeyActiveBrush（应用级资源覆盖主题字典回退值）。
    public static class ThemeService
    {
        private static readonly UISettings UiSettings = new UISettings();
        private static bool _initialized;
        private static bool _useSystemAccent = true;

        public static bool UseSystemAccent
        {
            get { return _useSystemAccent; }
            set
            {
                _useSystemAccent = value;
                ApplyAccent();
            }
        }

        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }
            _initialized = true;
            ApplyAccent();
            UiSettings.ColorValuesChanged += OnColorValuesChanged;
        }

        public static void Apply(AppThemeMode mode)
        {
            Initialize();
            var root = Window.Current.Content as FrameworkElement;
            if (root == null)
            {
                return;
            }
            switch (mode)
            {
                case AppThemeMode.Light:
                    root.RequestedTheme = ElementTheme.Light;
                    break;
                case AppThemeMode.Dark:
                    root.RequestedTheme = ElementTheme.Dark;
                    break;
                default:
                    root.RequestedTheme = ElementTheme.Default;
                    break;
            }
        }

        private static async void OnColorValuesChanged(UISettings sender, object args)
        {
            var dispatcher = Window.Current.Dispatcher;
            if (dispatcher == null)
            {
                return;
            }
            await dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, ApplyAccent);
        }

        private static void ApplyAccent()
        {
            if (!_useSystemAccent)
            {
                return;
            }
            var accent = UiSettings.GetColorValue(UIColorType.Accent);
            Application.Current.Resources["AppAccentBrush"] = new SolidColorBrush(accent);
            Application.Current.Resources["KeyBarKeyActiveBrush"] = new SolidColorBrush(accent);
        }
    }
}
