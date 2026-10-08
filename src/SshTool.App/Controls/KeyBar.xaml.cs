using System;
using System.Collections.Generic;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.Core.Terminal;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace SshTool.App.Controls
{
    // T10：功能键条。布局解析在 Core；本控件负责滚动、三态视觉、方向键连发与动作事件。
    public sealed partial class KeyBar : UserControl
    {
        private readonly DispatcherTimer _repeatTimer = new DispatcherTimer();
        private readonly Dictionary<string, Border> _modifierChrome = new Dictionary<string, Border>();
        private readonly Dictionary<string, Border> _accentBars = new Dictionary<string, Border>();
        private readonly Dictionary<string, TextBlock> _modifierLabels = new Dictionary<string, TextBlock>();
        private readonly Dictionary<string, FontIcon> _lockIcons = new Dictionary<string, FontIcon>();
        private string _layout = KeyBarLayout.DefaultString;
        private StickyModifiers _sticky = new StickyModifiers();
        private KeyBarKey _repeatKey;
        private KeyBarKey _heldModifier;
        private bool _modifierLocked;
        private bool _repeatStarted;

        public KeyBar()
        {
            this.InitializeComponent();
            // ui/fix-pass：代码赋值的主题画刷随 ThemeService.ThemeChanged 重算。
            ThemeRefreshHook.Attach(this, Rebuild);
            _repeatTimer.Tick += OnRepeatTick;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        public event EventHandler<TerminalInputEventArgs> Input;

        public event EventHandler<KeyBarActionEventArgs> Action;

        // §7.5：每次按下键条上的键都通知宿主，让它把焦点还给哨兵、别让 SIP 收起。
        // 必须独立于 Input：修饰键（Ctrl/Alt/Shift）只改粘滞状态、压根不产生 Input，
        // 而用户报的正是「点 Ctrl 键盘就关了」。Action 类（片段/收起键盘）不在此列——
        // 它们本就要开浮出层或主动收键盘。
        public event EventHandler Interacted;

        public StickyModifiers Sticky
        {
            get { return _sticky; }
            set
            {
                if (ReferenceEquals(_sticky, value))
                {
                    return;
                }
                UnsubscribeSticky();
                _sticky = value ?? new StickyModifiers();
                SubscribeSticky();
                RefreshModifierVisuals();
            }
        }

        public TerminalModes Modes { get; set; }

        public bool BackspaceAsBs { get; set; }

        public bool HapticsEnabled { get; set; } = true;

        // opt/full-pass 竖屏重设计：第二行键开关（TerminalPage TallState 用 VisualState Setter 打开）。
        public static readonly DependencyProperty ShowExtraRowProperty =
            DependencyProperty.Register(nameof(ShowExtraRow), typeof(bool), typeof(KeyBar),
                new PropertyMetadata(false, OnShowExtraRowChanged));

        public bool ShowExtraRow
        {
            get { return (bool)GetValue(ShowExtraRowProperty); }
            set { SetValue(ShowExtraRowProperty, value); }
        }

        private static void OnShowExtraRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var bar = d as KeyBar;
            if (bar != null)
            {
                bar.Rebuild();
            }
        }

        public string Layout
        {
            get { return _layout; }
            set
            {
                string next = string.IsNullOrWhiteSpace(value)
                    ? KeyBarLayout.DefaultString
                    : value;
                if (_layout == next)
                {
                    return;
                }
                _layout = next;
                Rebuild();
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            SubscribeSticky();
            Rebuild();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopRepeat();
            UnsubscribeSticky();
        }

        private void SubscribeSticky()
        {
            if (_sticky != null)
            {
                _sticky.Changed -= OnStickyChanged;
                _sticky.Changed += OnStickyChanged;
            }
        }

        private void UnsubscribeSticky()
        {
            if (_sticky != null)
            {
                _sticky.Changed -= OnStickyChanged;
            }
        }

        private void OnStickyChanged(object sender, EventArgs e)
        {
            RefreshModifierVisuals();
        }

        private void Rebuild()
        {
            if (KeysPanel == null)
            {
                return;
            }
            StopRepeat();
            KeysPanel.Children.Clear();
            if (KeysExtraPanel != null)
            {
                KeysExtraPanel.Children.Clear();
            }
            _modifierChrome.Clear();
            _accentBars.Clear();
            _modifierLabels.Clear();
            _lockIcons.Clear();
            // V01a：收起键盘按钮图标固定在 XAML（IconKeyboard 字形），不再从布局表取 ⌨ 文本。
            IReadOnlyList<KeyBarKey> keys = KeyBarLayout.Parse(_layout);
            bool first = true;
            for (int i = 0; i < keys.Count; i++)
            {
                KeyBarKey key = keys[i];
                if (key.Action == KeyBarAction.HideKeyboard)
                {
                    continue;
                }
                FrameworkElement element = CreateKeyElement(key, !first, false);
                KeysPanel.Children.Add(element);
                first = false;
            }
            RebuildExtraRow();
            RefreshModifierVisuals();
        }

        // 第二行：只在 ShowExtraRow 时生成（隐藏时不占元素）；与主行去重后为空则整行收起。
        private void RebuildExtraRow()
        {
            if (KeysExtraPanel == null || KeysExtraScroller == null)
            {
                return;
            }
            IReadOnlyList<KeyBarKey> extra = ShowExtraRow
                ? KeyBarLayout.ExtraRowFor(_layout)
                : (IReadOnlyList<KeyBarKey>)new KeyBarKey[0];
            bool first = true;
            for (int i = 0; i < extra.Count; i++)
            {
                if (extra[i].Action == KeyBarAction.HideKeyboard)
                {
                    continue;
                }
                KeysExtraPanel.Children.Add(CreateKeyElement(extra[i], !first, true));
                first = false;
            }
            KeysExtraScroller.Visibility = first ? Visibility.Collapsed : Visibility.Visible;
        }

        private FrameworkElement CreateKeyElement(KeyBarKey key, bool spaced, bool extraRow)
        {
            var label = new TextBlock
            {
                Text = key.Label,
                Style = (Style)Application.Current.Resources["CaptionTextStyle"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush("AppTextBrush")
            };
            // V03b（05 §6.2 第 4 点）：键 Border 内叠一层 Grid，底部放 accent 条
            //（Locked 态显示，其余态隐藏），与 Active/Off 明确区分。
            var accentBar = new Border
            {
                Height = TokenDouble("KeyBarAccentBarHeight"),
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = Brush("AppAccentBrush"),
                Visibility = Visibility.Collapsed
            };
            var grid = new Grid();
            FontIcon lockIcon = null;
            if (key.Kind == KeyBarKeyKind.Modifier)
            {
                lockIcon = new FontIcon
                {
                    Glyph = (string)Application.Current.Resources["IconLock"],
                    FontSize = TokenDouble("FontCaption"),
                    Margin = TokenThickness("GapXsLeft"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = Visibility.Collapsed
                };
                var contentPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                contentPanel.Children.Add(label);
                contentPanel.Children.Add(lockIcon);
                grid.Children.Add(contentPanel);
            }
            else
            {
                grid.Children.Add(label);
            }
            grid.Children.Add(accentBar);
            // opt/full-pass：键帽 = 纵向渐变 + 发丝描边 + RadiusMd；第二行键矮一档、窄一档。
            var chrome = new Border
            {
                Child = grid,
                Background = Brush(IdleKeyBrushKey),
                MinWidth = TokenDouble(extraRow ? "KeyBarExtraKeyMinWidth" : "KeyBarKeyMinWidth"),
                Height = TokenDouble(extraRow ? "KeyBarExtraRowHeight" : "KeyBarHeight"),
                Padding = TokenThickness("PadNone"),
                BorderBrush = Brush("KeyBarKeyStrokeBrush"),
                BorderThickness = TokenThickness("BorderThin"),
                CornerRadius = TokenCorner("RadiusMd")
            };
            if (spaced)
            {
                chrome.Margin = TokenThickness("GapSmLeft");
            }
            chrome.Tag = key;
            chrome.IsHitTestVisible = true;
            // Border 本身不可获焦，这里显式置否是为了「将来改成 Button 也不会破」——
            // §7.5 的规则是键条上所有可点元素一律 AllowFocusOnInteraction=False。
            chrome.AllowFocusOnInteraction = false;
            // Q05：键条按键设置可读名称；label 设为 Raw 避免重复朗读
            Windows.UI.Xaml.Automation.AutomationProperties.SetName(chrome, AccessibleNameOfKey(key));
            Windows.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(label,
                Windows.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            chrome.PointerPressed += OnKeyPointerPressed;
            chrome.PointerReleased += OnKeyPointerReleased;
            chrome.PointerCanceled += OnKeyPointerReleased;
            chrome.PointerCaptureLost += OnKeyPointerReleased;
            if (key.Kind == KeyBarKeyKind.Modifier)
            {
                _modifierChrome[key.Id] = chrome;
                _accentBars[key.Id] = accentBar;
                _modifierLabels[key.Id] = label;
                _lockIcons[key.Id] = lockIcon;
            }
            return chrome;
        }

        private static string AccessibleNameOfKey(KeyBarKey key)
        {
            if (key == null)
            {
                return string.Empty;
            }
            switch (key.Id)
            {
                case "esc":
                    return Infrastructure.Localized.Get("KeyBar_Key_Esc", "Esc 键");
                case "tab":
                    return Infrastructure.Localized.Get("KeyBar_Key_Tab", "Tab 键");
                case "ctrl":
                    return Infrastructure.Localized.Get("KeyBar_Key_Ctrl", "Ctrl 键");
                case "alt":
                    return Infrastructure.Localized.Get("KeyBar_Key_Alt", "Alt 键");
                case "shift":
                    return Infrastructure.Localized.Get("KeyBar_Key_Shift", "Shift 键");
                case "up":
                    return Infrastructure.Localized.Get("KeyBar_Key_Up", "向上方向键");
                case "down":
                    return Infrastructure.Localized.Get("KeyBar_Key_Down", "向下方向键");
                case "left":
                    return Infrastructure.Localized.Get("KeyBar_Key_Left", "向左方向键");
                case "right":
                    return Infrastructure.Localized.Get("KeyBar_Key_Right", "向右方向键");
                case "home":
                    return Infrastructure.Localized.Get("KeyBar_Key_Home", "Home 键");
                case "end":
                    return Infrastructure.Localized.Get("KeyBar_Key_End", "End 键");
                case "pgup":
                    return Infrastructure.Localized.Get("KeyBar_Key_PgUp", "上一页");
                case "pgdn":
                    return Infrastructure.Localized.Get("KeyBar_Key_PgDn", "下一页");
                case "ins":
                    return Infrastructure.Localized.Get("KeyBar_Key_Ins", "插入键");
                case "del":
                    return Infrastructure.Localized.Get("KeyBar_Key_Del", "删除键");
                case "enter":
                    return Infrastructure.Localized.Get("KeyBar_Key_Enter", "回车键");
                case "backspace":
                    return Infrastructure.Localized.Get("KeyBar_Key_Backspace", "退格键");
                case "paste":
                    return Infrastructure.Localized.Get("KeyBar_Key_Paste", "粘贴");
                case "copy":
                    return Infrastructure.Localized.Get("KeyBar_Key_Copy", "复制");
                case "snippets":
                    return Infrastructure.Localized.Get("KeyBar_Key_Snippets", "代码片段");
                case "pipe":
                    return Infrastructure.Localized.Get("KeyBar_Key_Pipe", "管道符");
                case "slash":
                    return Infrastructure.Localized.Get("KeyBar_Key_Slash", "正斜杠");
                case "backslash":
                    return Infrastructure.Localized.Get("KeyBar_Key_Backslash", "反斜杠");
                case "minus":
                    return Infrastructure.Localized.Get("KeyBar_Key_Minus", "减号");
                case "underscore":
                    return Infrastructure.Localized.Get("KeyBar_Key_Underscore", "下划线");
                case "tilde":
                    return Infrastructure.Localized.Get("KeyBar_Key_Tilde", "波浪号");
                default:
                    return string.IsNullOrEmpty(key.Label) ? key.Id : key.Label;
            }
        }


        private void OnKeyPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var chrome = sender as Border;
            var key = chrome != null ? chrome.Tag as KeyBarKey : null;
            if (key == null)
            {
                return;
            }
            chrome.CapturePointer(e.Pointer);
            Haptics.VibrateLight(HapticsEnabled);
            chrome.Background = Brush("AppPressedBrush");
            if (key.Kind != KeyBarKeyKind.Action)
            {
                EventHandler interacted = Interacted;
                if (interacted != null)
                {
                    interacted(this, EventArgs.Empty);
                }
            }
            if (key.Kind == KeyBarKeyKind.Modifier)
            {
                _heldModifier = key;
                _modifierLocked = false;
                _repeatStarted = false;
                _repeatKey = key;
                _repeatTimer.Interval = TimeSpan.FromMilliseconds(KeyBarLayout.RepeatInitialMilliseconds);
                _repeatTimer.Start();
                return;
            }
            if (key.IsArrow)
            {
                SendFunction(key);
                _repeatKey = key;
                _repeatStarted = false;
                _repeatTimer.Interval = TimeSpan.FromMilliseconds(KeyBarLayout.RepeatInitialMilliseconds);
                _repeatTimer.Start();
                return;
            }
            Activate(key);
        }

        private void OnKeyPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            var chrome = sender as Border;
            if (chrome != null)
            {
                try
                {
                    chrome.ReleasePointerCapture(e.Pointer);
                }
                catch (Exception)
                {
                }
            }
            KeyBarKey released = chrome != null ? chrome.Tag as KeyBarKey : null;
            if (released != null)
            {
                if (released.Kind == KeyBarKeyKind.Modifier)
                {
                    RefreshModifierVisuals();
                }
                else if (chrome != null)
                {
                    chrome.Background = Brush(IdleKeyBrushKey);
                }
            }
            else if (chrome != null)
            {
                chrome.Background = Brush(IdleKeyBrushKey);
            }

            // 按下与抬起各通知一次：框架把焦点挪走的时机（按下前 / 抬起后）无从预设，
            // 两头都补一次；宿主那边「已有焦点就不动」，多余的一次是空操作。
            if (released == null || released.Kind != KeyBarKeyKind.Action)
            {
                EventHandler interacted = Interacted;
                if (interacted != null)
                {
                    interacted(this, EventArgs.Empty);
                }
            }
            if (_heldModifier != null && !_modifierLocked)
            {
                Sticky.Tap(_heldModifier.Modifier);
            }
            _heldModifier = null;
            _modifierLocked = false;
            StopRepeat();
        }

        private void OnRepeatTick(object sender, object e)
        {
            if (_heldModifier != null && !_repeatStarted)
            {
                Sticky.LongPress(_heldModifier.Modifier);
                _modifierLocked = true;
                StopRepeat();
                return;
            }
            if (_repeatKey != null && _repeatKey.IsArrow)
            {
                if (!_repeatStarted)
                {
                    _repeatStarted = true;
                    _repeatTimer.Interval = TimeSpan.FromMilliseconds(KeyBarLayout.RepeatIntervalMilliseconds);
                }
                SendFunction(_repeatKey);
                return;
            }
            StopRepeat();
        }

        private void StopRepeat()
        {
            _repeatTimer.Stop();
            _repeatKey = null;
            _repeatStarted = false;
        }

        private void Activate(KeyBarKey key)
        {
            if (key.Kind == KeyBarKeyKind.Action)
            {
                EventHandler<KeyBarActionEventArgs> handler = Action;
                if (handler != null)
                {
                    handler(this, new KeyBarActionEventArgs(key.Action));
                }
                return;
            }
            if (key.Kind == KeyBarKeyKind.Character)
            {
                SendMapped(TerminalKey.Char, key.Character);
                return;
            }
            if (key.Kind == KeyBarKeyKind.Function)
            {
                SendFunction(key);
            }
        }

        private void SendFunction(KeyBarKey key)
        {
            SendMapped(key.TerminalKey, '\0');
        }

        private void SendMapped(TerminalKey terminalKey, char character)
        {
            KeyChord chord = Sticky.Wrap(terminalKey, character);
            byte[] data = KeyMap.Map(chord, Modes ?? new TerminalModes(), BackspaceAsBs);
            if (data == null && terminalKey == TerminalKey.Char && character != '\0' && !chord.Ctrl)
            {
                data = System.Text.Encoding.UTF8.GetBytes(new string(character, 1));
            }
            if (data == null || data.Length == 0)
            {
                return;
            }
            EventHandler<TerminalInputEventArgs> handler = Input;
            if (handler != null)
            {
                handler(this, new TerminalInputEventArgs(data));
            }
        }

        private void OnHideKeyboardClick(object sender, RoutedEventArgs e)
        {
            Haptics.VibrateLight(HapticsEnabled);
            EventHandler<KeyBarActionEventArgs> handler = Action;
            if (handler != null)
            {
                handler(this, new KeyBarActionEventArgs(KeyBarAction.HideKeyboard));
            }
        }

        private void RefreshModifierVisuals()
        {
            RefreshOne("ctrl", Sticky.Ctrl);
            RefreshOne("alt", Sticky.Alt);
            RefreshOne("shift", Sticky.Shift);
        }

        private void RefreshOne(string id, StickyState state)
        {
            Border chrome;
            if (!_modifierChrome.TryGetValue(id, out chrome))
            {
                return;
            }
            TextBlock label;
            _modifierLabels.TryGetValue(id, out label);
            Border accentBar;
            _accentBars.TryGetValue(id, out accentBar);
            FontIcon lockIcon;
            _lockIcons.TryGetValue(id, out lockIcon);

            if (state == StickyState.Off)
            {
                chrome.Background = Brush(IdleKeyBrushKey);
                chrome.BorderThickness = TokenThickness("BorderThin");
                if (accentBar != null)
                {
                    accentBar.Visibility = Visibility.Collapsed;
                }
                if (lockIcon != null)
                {
                    lockIcon.Visibility = Visibility.Collapsed;
                }
                if (label != null)
                {
                    label.Foreground = Brush("AppTextBrush");
                }
                return;
            }
            // ui/fix-pass：锁定态底色是亮色（琥珀），白字对比度不足 2:1——前景改用专用的
            // KeyBarKeyLockedForegroundBrush（#1A1A1A，PaletteContrastTests 保证 ≥4.5）。
            string foregroundKey = state == StickyState.Locked ? "KeyBarKeyLockedForegroundBrush" : "AppOnAccentBrush";
            if (state == StickyState.Locked)
            {
                chrome.Background = Brush("KeyBarKeyLockedBrush");
                chrome.BorderThickness = TokenThickness("BorderNone");
                if (accentBar != null)
                {
                    accentBar.Visibility = Visibility.Collapsed;
                }
                if (lockIcon != null)
                {
                    lockIcon.Visibility = Visibility.Visible;
                    lockIcon.Foreground = Brush(foregroundKey);
                }
            }
            else
            {
                // OneShot
                chrome.Background = Brush("KeyBarKeyActiveBrush");
                chrome.BorderThickness = TokenThickness("BorderNone");
                if (accentBar != null)
                {
                    accentBar.Visibility = Visibility.Collapsed;
                }
                if (lockIcon != null)
                {
                    lockIcon.Visibility = Visibility.Collapsed;
                }
            }
            if (label != null)
            {
                label.Foreground = Brush(foregroundKey);
            }
        }

        private const string IdleKeyBrushKey = "KeyBarKeyIdleBrush";

        private static Brush Brush(string key)
        {
            return ThemeService.ResolveBrush(key);
        }

        private static double TokenDouble(string key)
        {
            object value = Application.Current.Resources[key];
            return value is double ? (double)value : 0;
        }

        private static Thickness TokenThickness(string key)
        {
            object value = Application.Current.Resources[key];
            return value is Thickness ? (Thickness)value : new Thickness();
        }

        private static CornerRadius TokenCorner(string key)
        {
            object value = Application.Current.Resources[key];
            return value is CornerRadius ? (CornerRadius)value : new CornerRadius();
        }
    }
}
