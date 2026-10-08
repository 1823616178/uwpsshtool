using System;
using System.Collections.Generic;
using SshTool.Core.Appearance;
using Windows.Foundation.Metadata;
using Windows.UI;
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
    //
    // ui/fix-pass 重写要点：
    //   1. 有效主题只有一个判定处（Core ThemeKeyResolver）：高对比度 > 应用设置 > 系统主题。
    //      「跟随系统」时也把根元素 RequestedTheme 设成具体的 Light/Dark（按 UISettings 背景色），
    //      而不是 Default：Application.RequestedTheme 启动后不再变化，Default 会让 XAML 与
    //      代码侧画刷解析各自为政（旧 Banner.ResolveThemedBrush 误判就是这么来的）。
    //   2. 代码侧画刷一律走 ResolveBrush(key)：按有效主题从 ThemeDictionaries 取，
    //      不走 Application.Current.Resources[key]（后者按启动时的系统主题解析）。
    //   3. 主题/强调色/高对比度变化后在 UI 线程广播 ThemeChanged（Version 自增）；
    //      代码赋值画刷的控件经 ThemeRefreshHook 订阅后重算。15063 无 ActualThemeChanged。
    //   4. 系统强调色：就地改 Dark/Light 两份字典里的画刷 Color（含页头光晕各段、强调色叠层），
    //      已解析的 ThemeResource 引用同一实例，无需替换对象；关闭时按记录的原色还原。
    //      强调色上的前景（AppOnAccentBrush）按亮度在深/白之间选，强调色文字按对比度调整。
    public static class ThemeService
    {
        private static readonly UISettings UiSettings = new UISettings();
        private static AccessibilitySettings _accessibility;
        private static bool _initialized;
        private static bool _useSystemAccent = true;
        private static int _version;

        // 默认与设置表 themeMode 默认值 "dark" 一致；启动时 Apply(Settings.ThemeMode) 会纠正。
        private static AppThemeMode _currentMode = AppThemeMode.Dark;

        // 主题字典里被强调色改写过的画刷原色：键 = 主题 + "|" + 资源键（渐变为 "|stopN"）。
        private static readonly Dictionary<string, Color> OriginalColors = new Dictionary<string, Color>(StringComparer.Ordinal);

        private static readonly string[] AccentThemes = { ThemeKeyResolver.Dark, ThemeKeyResolver.Light };

        // 在 UI 线程触发。订阅方必须在 Unloaded 时解除（scripts/check-subscriptions.ps1 受管）。
        public static event EventHandler ThemeChanged;

        public static AppThemeMode CurrentMode
        {
            get { return _currentMode; }
        }

        // 每次有效主题或强调色变化自增；控件重新 Loaded 时据此判断是否错过了广播。
        public static int Version
        {
            get { return _version; }
        }

        public static bool UseSystemAccent
        {
            get { return _useSystemAccent; }
            set
            {
                _useSystemAccent = value;
                RunOnUi(RefreshAll);
            }
        }

        public static void Initialize()
        {
            if (_initialized)
            {
                return;
            }
            _initialized = true;
            UiSettings.ColorValuesChanged += OnColorValuesChanged;
            try
            {
                _accessibility = new AccessibilitySettings();
                _accessibility.HighContrastChanged += OnHighContrastChanged;
            }
            catch (Exception)
            {
                _accessibility = null;
            }
            ApplyAccent();
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
            ApplyRootTheme();
            ApplyAccent();
            StatusBarService.RefreshTheme();
            RaiseThemeChanged();
        }

        // 有效主题键："Dark" / "Light" / "HighContrast"（对应 Tokens.xaml 的 ThemeDictionaries 键）。
        internal static string EffectiveThemeKey()
        {
            return ThemeKeyResolver.Resolve((ThemeModeSetting)(int)_currentMode, IsSystemLight(), IsHighContrast());
        }

        // 代码侧取主题画刷的唯一入口：按有效主题解析，取不到再退回应用级资源（非主题键）。
        public static Brush ResolveBrush(string key)
        {
            if (string.IsNullOrEmpty(key) || Application.Current == null)
            {
                return null;
            }
            try
            {
                object value = FindThemed(Application.Current.Resources, key, EffectiveThemeKey());
                var brush = value as Brush;
                if (brush != null)
                {
                    return brush;
                }
                object fallback;
                if (Application.Current.Resources.TryGetValue(key, out fallback))
                {
                    return fallback as Brush;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        // 从指定主题字典取纯色画刷（StatusBar 等需要 Color 的场合）。
        internal static SolidColorBrush FindThemedBrush(string key, string theme)
        {
            try
            {
                var app = Application.Current;
                return app == null ? null : FindThemed(app.Resources, key, theme) as SolidColorBrush;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // 递归：本字典 → 合并字典 → 本字典的 ThemeDictionaries[theme]。
        // 注意不直接 TryGetValue 应用根字典的主题键：根字典上不再放强调色覆盖（见第 4 点）。
        private static object FindThemed(ResourceDictionary dict, string key, string theme)
        {
            if (dict == null)
            {
                return null;
            }
            object value;
            if (dict.ThemeDictionaries != null && dict.ThemeDictionaries.TryGetValue(theme, out value))
            {
                var themed = value as ResourceDictionary;
                if (themed != null)
                {
                    object hit = FindThemed(themed, key, theme);
                    if (hit != null)
                    {
                        return hit;
                    }
                }
            }
            if (dict.MergedDictionaries != null)
            {
                foreach (ResourceDictionary merged in dict.MergedDictionaries)
                {
                    object hit = FindThemed(merged, key, theme);
                    if (hit != null)
                    {
                        return hit;
                    }
                }
            }
            if (dict.TryGetValue(key, out value))
            {
                return value;
            }
            return null;
        }

        private static void ApplyRootTheme()
        {
            var window = Window.Current;                        // 仅 UI 线程调用
            var root = window != null ? window.Content as FrameworkElement : null;
            if (root == null)
            {
                return;
            }
            // 高对比度下 XAML 自动选 HighContrast 字典，RequestedTheme 只影响系统控件的明暗底。
            ElementTheme wanted = EffectiveElementTheme();
            if (root.RequestedTheme != wanted)
            {
                root.RequestedTheme = wanted;
            }
        }

        // 视觉树外的内容（Popup 子树、锁屏层）不继承根元素 RequestedTheme，需显式套用这个值，
        // 否则文字按应用启动主题、背景按有效主题，会出现浅底白字。
        public static ElementTheme EffectiveElementTheme()
        {
            bool light = string.Equals(ResolveLightDark(), ThemeKeyResolver.Light, StringComparison.Ordinal);
            return light ? ElementTheme.Light : ElementTheme.Dark;
        }

        private static string ResolveLightDark()
        {
            return ThemeKeyResolver.Resolve((ThemeModeSetting)(int)_currentMode, IsSystemLight(), false);
        }

        private static bool IsSystemLight()
        {
            try
            {
                Color bg = UiSettings.GetColorValue(UIColorType.Background);
                return ThemeKeyResolver.IsLightSystemBackground(new Rgb(bg.R, bg.G, bg.B));
            }
            catch (Exception)
            {
            }
            try
            {
                return Application.Current != null && Application.Current.RequestedTheme == ApplicationTheme.Light;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsHighContrast()
        {
            try
            {
                return _accessibility != null && _accessibility.HighContrast;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // 注意：ColorValuesChanged 在后台线程触发，而 Window.Current 是线程静态的（后台线程恒为 null），
        // 这里必须用 DispatcherHelper 缓存的 UI 线程 CoreDispatcher 封送，否则改系统强调色即崩。
        // 系统深浅主题切换也走这个事件（Background 色变化）。
        private static void OnColorValuesChanged(UISettings sender, object args)
        {
            Infrastructure.DispatcherHelper.Post(RefreshAll);
        }

        private static void OnHighContrastChanged(AccessibilitySettings sender, object args)
        {
            Infrastructure.DispatcherHelper.Post(RefreshAll);
        }

        private static void RefreshAll()
        {
            ApplyRootTheme();
            ApplyAccent();
            StatusBarService.RefreshTheme();
            RaiseThemeChanged();
        }

        private static void RaiseThemeChanged()
        {
            _version++;
            EventHandler handler = ThemeChanged;
            if (handler != null)
            {
                handler(null, EventArgs.Empty);
            }
        }

        private static void RunOnUi(Action action)
        {
            if (Infrastructure.DispatcherHelper.HasThreadAccess)
            {
                action();
            }
            else
            {
                Infrastructure.DispatcherHelper.Post(action);
            }
        }

        // 随强调色填充一起变的键（都在 Dark/Light 字典里定义）。
        private static readonly string[] AccentFillKeys = new[]
        {
            "AppAccentBrush",
            "KeyBarKeyActiveBrush",
            "TextControlBorderBrushFocused",
            "ComboBoxBackgroundBorderBrushFocused",
            "ComboBoxItemBorderBrushSelected",
            "ComboBoxItemBorderBrushSelectedPointerOver",
            "ComboBoxItemBorderBrushSelectedPressed",
            "ComboBoxItemBorderBrushSelectedUnfocused",
            // G02: ToggleSwitch
            "ToggleSwitchFillOn",
            "ToggleSwitchFillOnPointerOver",
            "ToggleSwitchFillOnPressed",
            // G02: CheckBox
            "CheckBoxCheckBackgroundFillChecked",
            "CheckBoxCheckBackgroundStrokeChecked",
            "CheckBoxCheckBackgroundFillCheckedPointerOver",
            "CheckBoxCheckBackgroundStrokeCheckedPointerOver",
            "CheckBoxCheckBackgroundFillCheckedPressed",
            "CheckBoxCheckBackgroundStrokeCheckedPressed",
            "CheckBoxCheckBackgroundFillIndeterminate",
            "CheckBoxCheckBackgroundStrokeIndeterminate",
            "CheckBoxCheckBackgroundFillIndeterminatePointerOver",
            "CheckBoxCheckBackgroundStrokeIndeterminatePointerOver",
            "CheckBoxCheckBackgroundFillIndeterminatePressed",
            "CheckBoxCheckBackgroundStrokeIndeterminatePressed",
            // G02: RadioButton
            "RadioButtonOuterEllipseCheckedStroke",
            "RadioButtonOuterEllipseCheckedStrokePointerOver",
            "RadioButtonOuterEllipseCheckedStrokePressed",
            "RadioButtonCheckGlyphFill",
            "RadioButtonCheckGlyphFillPointerOver",
            "RadioButtonCheckGlyphFillPressed",
            // G02: Slider
            "SliderTrackValueFill",
            "SliderTrackValueFillPointerOver",
            "SliderTrackValueFillPressed",
            // G02: Pivot
            "PivotHeaderItemFocusPipeFill"
        };

        // 强调色上的前景（白/深按亮度选）。
        private static readonly string[] OnAccentKeys = new[]
        {
            "AppOnAccentBrush",
            "ToggleSwitchKnobFillOn",
            "ToggleSwitchKnobFillOnPointerOver",
            "ToggleSwitchKnobFillOnPressed",
            "CheckBoxCheckGlyphForegroundChecked",
            "CheckBoxCheckGlyphForegroundCheckedPointerOver",
            "CheckBoxCheckGlyphForegroundCheckedPressed",
            "CheckBoxCheckGlyphForegroundIndeterminate",
            "CheckBoxCheckGlyphForegroundIndeterminatePointerOver",
            "CheckBoxCheckGlyphForegroundIndeterminatePressed"
        };

        // 强调色作文字（需在 Surface 上 ≥ 4.5:1）。
        private static readonly string[] AccentTextKeys = new[]
        {
            "AppAccentTextBrush",
            "ComboBoxItemForegroundSelected",
            "ComboBoxItemForegroundSelectedPointerOver"
        };

        private static void ApplyAccent()
        {
            if (Application.Current == null)
            {
                return;
            }
            try
            {
                bool useSystem = _useSystemAccent && ApiInformation.IsTypePresent("Windows.UI.ViewManagement.UISettings");
                Color accent = useSystem ? UiSettings.GetColorValue(UIColorType.Accent) : default(Color);
                foreach (string theme in AccentThemes)
                {
                    if (useSystem)
                    {
                        ApplySystemAccent(theme, accent);
                    }
                    else
                    {
                        RestoreOriginals(theme);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static void ApplySystemAccent(string theme, Color accent)
        {
            var rgb = new Rgb(accent.R, accent.G, accent.B);
            Color opaque = Color.FromArgb(255, accent.R, accent.G, accent.B);
            foreach (string key in AccentFillKeys)
            {
                SetColor(theme, key, opaque);
            }

            Rgb fg = ContrastMath.ForegroundFor(rgb);
            Color onAccent = Color.FromArgb(255, fg.R, fg.G, fg.B);
            foreach (string key in OnAccentKeys)
            {
                SetColor(theme, key, onAccent);
            }

            // 强调色文字：对本主题最「难」的输入底（SurfaceAlt）达到 4.5:1。
            Rgb surface = OriginalRgb(theme, "AppSurfaceAltBrush", Rgb.Black);
            Rgb text = ContrastMath.EnsureContrast(rgb, surface, ContrastMath.TextMinimum);
            Color textColor = Color.FromArgb(255, text.R, text.G, text.B);
            foreach (string key in AccentTextKeys)
            {
                SetColor(theme, key, textColor);
            }

            // 叠层：保留字典里设计好的 alpha，只换 RGB。
            SetColorKeepAlpha(theme, "AppAccentSoftBrush", accent);
            SetGradientKeepAlpha(theme, "AppHeaderGlowBrush", accent);
        }

        private static Rgb OriginalRgb(string theme, string key, Rgb fallback)
        {
            Color c;
            if (OriginalColors.TryGetValue(theme + "|" + key, out c))
            {
                return new Rgb(c.R, c.G, c.B);
            }
            SolidColorBrush brush = FindThemedBrush(key, theme);
            return brush != null ? new Rgb(brush.Color.R, brush.Color.G, brush.Color.B) : fallback;
        }

        private static void SetColor(string theme, string key, Color color)
        {
            SolidColorBrush brush = FindThemedBrush(key, theme);
            if (brush == null)
            {
                return;
            }
            Remember(theme + "|" + key, brush.Color);
            if (brush.Color != color)
            {
                brush.Color = color;
            }
        }

        private static void SetColorKeepAlpha(string theme, string key, Color accent)
        {
            SolidColorBrush brush = FindThemedBrush(key, theme);
            if (brush == null)
            {
                return;
            }
            string id = theme + "|" + key;
            Remember(id, brush.Color);
            byte alpha = OriginalColors[id].A;
            brush.Color = Color.FromArgb(alpha, accent.R, accent.G, accent.B);
        }

        private static void SetGradientKeepAlpha(string theme, string key, Color accent)
        {
            var app = Application.Current;
            var brush = app == null ? null : FindThemed(app.Resources, key, theme) as GradientBrush;
            if (brush == null || brush.GradientStops == null)
            {
                return;
            }
            for (int i = 0; i < brush.GradientStops.Count; i++)
            {
                GradientStop stop = brush.GradientStops[i];
                string id = theme + "|" + key + "|stop" + i;
                Remember(id, stop.Color);
                byte alpha = OriginalColors[id].A;
                // 全透明段（渐隐终点）不着色，避免在浅色主题下出现彩边。
                if (alpha == 0)
                {
                    continue;
                }
                stop.Color = Color.FromArgb(alpha, accent.R, accent.G, accent.B);
            }
        }

        private static void Remember(string id, Color color)
        {
            if (!OriginalColors.ContainsKey(id))
            {
                OriginalColors[id] = color;
            }
        }

        private static void RestoreOriginals(string theme)
        {
            var app = Application.Current;
            string prefix = theme + "|";
            foreach (KeyValuePair<string, Color> pair in OriginalColors)
            {
                if (!pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }
                string rest = pair.Key.Substring(prefix.Length);
                int stopAt = rest.IndexOf("|stop", StringComparison.Ordinal);
                if (stopAt < 0)
                {
                    SolidColorBrush brush = FindThemedBrush(rest, theme);
                    if (brush != null && brush.Color != pair.Value)
                    {
                        brush.Color = pair.Value;
                    }
                    continue;
                }
                var gradient = app == null ? null : FindThemed(app.Resources, rest.Substring(0, stopAt), theme) as GradientBrush;
                int index;
                if (gradient != null
                    && int.TryParse(rest.Substring(stopAt + "|stop".Length), out index)
                    && index < gradient.GradientStops.Count)
                {
                    gradient.GradientStops[index].Color = pair.Value;
                }
            }
        }
    }
}
