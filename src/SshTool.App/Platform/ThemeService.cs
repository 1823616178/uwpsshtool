using System;
using Windows.Foundation.Metadata;
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

    // 02-UI-DESIGN.md §1、§5.15：应用主题与系统强调色（A05）。
    // 主题切换 = 根 Frame 的 RequestedTheme（ThemeResource 即时重算，已打开页面无需重建，
    // 无需重启；终端内容由外观决定，见 TerminalView.ApplyAppearance，此处不碰）；
    // UseSystemAccent 时用 UISettings 的强调色覆盖 AppAccentBrush / KeyBarKeyActiveBrush
    //（应用级资源覆盖主题字典回退值），关闭时写回当前主题的回退色；
    // 状态栏底色/前景随应用主题刷新（StatusBarService.RefreshTheme，只改颜色不显隐）。
    public static class ThemeService
    {
        private static readonly UISettings UiSettings = new UISettings();
        private static bool _initialized;
        private static bool _useSystemAccent = true;

        // 默认与设置表 themeMode 默认值 "dark" 一致；启动时 Apply(Settings.ThemeMode) 会纠正。
        private static AppThemeMode _currentMode = AppThemeMode.Dark;

        public static AppThemeMode CurrentMode
        {
            get { return _currentMode; }
        }

        public static bool UseSystemAccent
        {
            get { return _useSystemAccent; }
            set
            {
                _useSystemAccent = value;
                ApplyAccentOnUiThread();
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
            if (!Infrastructure.DispatcherHelper.HasThreadAccess)
            {
                Infrastructure.DispatcherHelper.Post(() => Apply(mode));
                return;
            }
            Initialize();
            _currentMode = mode;
            var window = Window.Current;                        // 仅 UI 线程调用
            var root = window != null ? window.Content as FrameworkElement : null;
            if (root != null)
            {
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
            ApplyAccent();
            StatusBarService.RefreshTheme();
        }

        // 注意：ColorValuesChanged 在后台线程触发，而 Window.Current 是线程静态的（后台线程恒为 null），
        // 这里必须用 DispatcherHelper 缓存的 UI 线程 CoreDispatcher 封送，否则改系统强调色即崩。
        private static void OnColorValuesChanged(UISettings sender, object args)
        {
            Infrastructure.DispatcherHelper.Post(ApplyAccent);
        }

        private static void ApplyAccentOnUiThread()
        {
            if (Infrastructure.DispatcherHelper.HasThreadAccess)
            {
                ApplyAccent();
            }
            else
            {
                Infrastructure.DispatcherHelper.Post(ApplyAccent);
            }
        }

        private static void ApplyAccent()
        {
            if (Application.Current == null)
            {
                return;
            }
            try
            {
                if (!_useSystemAccent)
                {
                    // 关闭系统强调色：写回当前主题字典的回退色（以字典为准，C# 不硬编码颜色）。
                    // Apply 每次主题切换都调这里，所以深浅切换后回退色不会串。
                    string theme = EffectiveThemeKey();
                    SolidColorBrush accentFallback = FindThemedBrush("AppAccentBrush", theme);
                    SolidColorBrush keyFallback = FindThemedBrush("KeyBarKeyActiveBrush", theme);
                    if (accentFallback != null)
                    {
                        Application.Current.Resources["AppAccentBrush"] =
                            new SolidColorBrush(accentFallback.Color);
                    }
                    if (keyFallback != null)
                    {
                        Application.Current.Resources["KeyBarKeyActiveBrush"] =
                            new SolidColorBrush(keyFallback.Color);
                    }
                    return;
                }
                if (!ApiInformation.IsTypePresent("Windows.UI.ViewManagement.UISettings"))
                {
                    return;
                }
                var accent = UiSettings.GetColorValue(UIColorType.Accent);
                Application.Current.Resources["AppAccentBrush"] = new SolidColorBrush(accent);
                Application.Current.Resources["KeyBarKeyActiveBrush"] = new SolidColorBrush(accent);
            }
            catch (Exception)
            {
            }
        }

        // 有效主题键：跟随系统时取 Application.RequestedTheme（本应用从不覆盖它，即系统主题）。
        internal static string EffectiveThemeKey()
        {
            if (_currentMode == AppThemeMode.Light)
            {
                return "Light";
            }
            if (_currentMode == AppThemeMode.Dark)
            {
                return "Dark";
            }
            try
            {
                if (Application.Current != null
                    && Application.Current.RequestedTheme == ApplicationTheme.Light)
                {
                    return "Light";
                }
            }
            catch (Exception)
            {
            }
            return "Dark";
        }

        // 从主题字典（ThemeDictionaries，深/浅同名键）取画笔；只读 Color，不改字典。
        internal static SolidColorBrush FindThemedBrush(string key, string theme)
        {
            try
            {
                var app = Application.Current;
                if (app == null)
                {
                    return null;
                }
                foreach (var merged in app.Resources.MergedDictionaries)
                {
                    SolidColorBrush found = FindThemedIn(merged, key, theme);
                    if (found != null)
                    {
                        return found;
                    }
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        private static SolidColorBrush FindThemedIn(ResourceDictionary dict, string key, string theme)
        {
            try
            {
                if (dict == null)
                {
                    return null;
                }
                object themedObj;
                if (dict.ThemeDictionaries != null
                    && dict.ThemeDictionaries.TryGetValue(theme, out themedObj))
                {
                    var themed = themedObj as ResourceDictionary;
                    if (themed != null)
                    {
                        object value;
                        if (themed.TryGetValue(key, out value))
                        {
                            var brush = value as SolidColorBrush;
                            if (brush != null)
                            {
                                return brush;
                            }
                        }
                    }
                }
                if (dict.MergedDictionaries != null)
                {
                    foreach (var inner in dict.MergedDictionaries)
                    {
                        SolidColorBrush found = FindThemedIn(inner, key, theme);
                        if (found != null)
                        {
                            return found;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return null;
        }
    }
}
