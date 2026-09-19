using System;
using System.Collections.Generic;
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
        private string _layout = KeyBarLayout.DefaultString;
        private StickyModifiers _sticky = new StickyModifiers();
        private KeyBarKey _repeatKey;
        private KeyBarKey _heldModifier;
        private bool _modifierLocked;
        private bool _repeatStarted;

        public KeyBar()
        {
            this.InitializeComponent();
            _repeatTimer.Tick += OnRepeatTick;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        public event EventHandler<TerminalInputEventArgs> Input;

        public event EventHandler<KeyBarActionEventArgs> Action;

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
            _modifierChrome.Clear();
            KeyBarKey hideKey;
            if (HideKeyboardButton != null && KeyBarLayout.TryGet("hidekb", out hideKey))
            {
                HideKeyboardButton.Content = hideKey.Label;
            }
            IReadOnlyList<KeyBarKey> keys = KeyBarLayout.Parse(_layout);
            bool first = true;
            for (int i = 0; i < keys.Count; i++)
            {
                KeyBarKey key = keys[i];
                if (key.Action == KeyBarAction.HideKeyboard)
                {
                    continue;
                }
                FrameworkElement element = CreateKeyElement(key, !first);
                KeysPanel.Children.Add(element);
                first = false;
            }
            RefreshModifierVisuals();
        }

        private FrameworkElement CreateKeyElement(KeyBarKey key, bool spaced)
        {
            var label = new TextBlock
            {
                Text = key.Label,
                Style = (Style)Application.Current.Resources["CaptionTextStyle"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush("AppTextBrush")
            };
            var chrome = new Border
            {
                Child = label,
                Background = Brush("KeyBarKeyBrush"),
                MinWidth = TokenDouble("KeyBarKeyMinWidth"),
                Height = TokenDouble("KeyBarHeight"),
                Padding = TokenThickness("PadNone"),
                BorderBrush = Brush("AppTextBrush"),
                BorderThickness = TokenThickness("BorderNone"),
                CornerRadius = TokenCorner("RadiusSm")
            };
            if (spaced)
            {
                chrome.Margin = TokenThickness("GapSmLeft");
            }
            chrome.Tag = key;
            chrome.IsHitTestVisible = true;
            chrome.PointerPressed += OnKeyPointerPressed;
            chrome.PointerReleased += OnKeyPointerReleased;
            chrome.PointerCanceled += OnKeyPointerReleased;
            chrome.PointerCaptureLost += OnKeyPointerReleased;
            if (key.Kind == KeyBarKeyKind.Modifier)
            {
                _modifierChrome[key.Id] = chrome;
            }
            return chrome;
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
            var label = chrome.Child as TextBlock;
            if (state == StickyState.Off)
            {
                chrome.Background = Brush("KeyBarKeyBrush");
                chrome.BorderThickness = TokenThickness("BorderNone");
                if (label != null)
                {
                    label.Foreground = Brush("AppTextBrush");
                }
                return;
            }
            if (state == StickyState.Locked)
            {
                chrome.Background = Brush("KeyBarKeyLockedBrush");
                chrome.BorderThickness = TokenThickness("KeyBarLockedBorder");
            }
            else
            {
                chrome.Background = Brush("KeyBarKeyActiveBrush");
                chrome.BorderThickness = TokenThickness("BorderNone");
            }
            if (label != null)
            {
                label.Foreground = Brush("AppOnAccentBrush");
            }
        }

        private static Brush Brush(string key)
        {
            return Application.Current.Resources[key] as Brush;
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
