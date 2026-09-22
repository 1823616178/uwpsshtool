using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Newtonsoft.Json.Linq;
using SshTool.App.Infrastructure;
using SshTool.App.Terminal;
using SshTool.App.ViewModels;
using SshTool.Core.Mvvm;
using SshTool.Core.Storage;
using SshTool.Core.Terminal;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    public sealed class ShortcutRow : ObservableObject
    {
        private string _chordText = string.Empty;

        public ShortcutAction Action { get; set; }

        public string DisplayName { get; set; }

        public string ChordText
        {
            get { return _chordText; }
            set { SetProperty(ref _chordText, value); }
        }
    }

    public sealed partial class ShortcutEditorPage : Page
    {
        private static readonly string[] ActionNames =
        {
            "newTab", "closePane", "nextTab", "prevTab",
            "splitRight", "splitDown",
            "focusLeft", "focusUp", "focusRight", "focusDown",
            "copy", "paste",
            "fontIncrease", "fontDecrease", "fontReset", "find"
        };

        private readonly SettingsRepository _settings;
        private readonly Dictionary<ShortcutAction, ShortcutChord> _chords =
            new Dictionary<ShortcutAction, ShortcutChord>();
        private ShortcutRow _listening;

        public ShortcutEditorPage()
        {
            _settings = AppServices.Current.Settings;
            Rows = new ObservableCollection<ShortcutRow>();
            this.InitializeComponent();
            ShortcutList.ItemsSource = Rows;
            Reload();
        }

        public ObservableCollection<ShortcutRow> Rows { get; private set; }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            CancelListening();
            base.OnNavigatedFrom(e);
        }

        private void Reload()
        {
            CancelListening();
            ShortcutMap map;
            try
            {
                map = ShortcutMap.Parse(_settings.Shortcuts);
            }
            catch (Exception)
            {
                map = ShortcutMap.Default;
            }
            _chords.Clear();
            for (int i = 0; i < map.Bindings.Count; i++)
            {
                _chords[map.Bindings[i].Action] = map.Bindings[i].Chord;
            }
            Rows.Clear();
            for (int i = 0; i < map.Bindings.Count; i++)
            {
                ShortcutBinding binding = map.Bindings[i];
                Rows.Add(new ShortcutRow
                {
                    Action = binding.Action,
                    DisplayName = SettingsViewModel.ShortcutDisplayName(binding.Action),
                    ChordText = binding.Chord.ToDisplay()
                });
            }
            UpdateConflictWarning(map);
        }

        private void UpdateConflictWarning(ShortcutMap map)
        {
            if (map.Conflicts.Count == 0)
            {
                ConflictWarning.Visibility = Visibility.Collapsed;
                return;
            }
            ShortcutConflict first = map.Conflicts[0];
            ConflictWarning.Text = Localized.Format("ShortcutEditor_Conflict", "冲突：{0} 被两个动作使用", first.Chord.ToDisplay())
                + (map.Conflicts.Count > 1 ? "（等 " + map.Conflicts.Count + " 处）" : "")
                + "，后匹配到的优先生效，请重新录制";
            ConflictWarning.Visibility = Visibility.Visible;
        }

        private void Save()
        {
            var obj = new JObject();
            for (int i = 0; i < ActionNames.Length; i++)
            {
                var action = (ShortcutAction)i;
                ShortcutChord chord;
                if (_chords.TryGetValue(action, out chord))
                {
                    obj[ActionNames[i]] = chord.ToDisplay();
                }
            }
            try
            {
                _settings.Shortcuts = obj.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch (Exception)
            {
            }
            UpdateConflictWarning(ShortcutMap.Parse(_settings.Shortcuts));
        }

        private void OnRecordClick(object sender, RoutedEventArgs e)
        {
            var row = (sender as FrameworkElement).DataContext as ShortcutRow;
            if (row == null)
            {
                return;
            }
            if (_listening == row)
            {
                CancelListening();
                return;
            }
            CancelListening();
            _listening = row;
            row.ChordText = "请按键…（Esc 取消）";
            AttachCapture();
        }

        private void OnResetClick(object sender, RoutedEventArgs e)
        {
            try
            {
                _settings.Shortcuts = "{}";
            }
            catch (Exception)
            {
            }
            Reload();
            Toast.Show("已恢复默认快捷键");
        }

        private void AttachCapture()
        {
            try
            {
                CoreWindow window = Window.Current != null ? Window.Current.CoreWindow : null;
                if (window != null)
                {
                    window.KeyDown += OnCaptureKeyDown;
                }
                CoreDispatcher dispatcher =
                    Window.Current != null ? Window.Current.Dispatcher : null;
                if (dispatcher != null)
                {
                    dispatcher.AcceleratorKeyActivated += OnCaptureAcceleratorKey;
                }
            }
            catch (Exception)
            {
            }
        }

        private void DetachCapture()
        {
            try
            {
                CoreWindow window = Window.Current != null ? Window.Current.CoreWindow : null;
                if (window != null)
                {
                    window.KeyDown -= OnCaptureKeyDown;
                }
                CoreDispatcher dispatcher =
                    Window.Current != null ? Window.Current.Dispatcher : null;
                if (dispatcher != null)
                {
                    dispatcher.AcceleratorKeyActivated -= OnCaptureAcceleratorKey;
                }
            }
            catch (Exception)
            {
            }
        }

        private void CancelListening()
        {
            if (_listening == null)
            {
                return;
            }
            DetachCapture();
            ShortcutRow row = _listening;
            _listening = null;
            RefreshRowText(row);
        }

        private void RefreshRowText(ShortcutRow row)
        {
            ShortcutChord chord;
            if (_chords.TryGetValue(row.Action, out chord))
            {
                row.ChordText = chord.ToDisplay();
            }
        }

        private void OnCaptureKeyDown(CoreWindow sender, KeyEventArgs args)
        {
            if (_listening == null || args == null || args.Handled)
            {
                return;
            }
            if (IsModifier(args.VirtualKey))
            {
                return;
            }
            bool ctrl = IsDown(VirtualKey.Control);
            bool alt = IsDown(VirtualKey.Menu);
            bool shift = IsDown(VirtualKey.Shift);
            if (TryCommit(args.VirtualKey, shift, ctrl, alt, args))
            {
                args.Handled = true;
            }
        }

        private void OnCaptureAcceleratorKey(CoreDispatcher sender, AcceleratorKeyEventArgs args)
        {
            if (_listening == null || args == null || args.Handled)
            {
                return;
            }
            if (args.EventType != CoreAcceleratorKeyEventType.SystemKeyDown
                && args.EventType != CoreAcceleratorKeyEventType.KeyDown)
            {
                return;
            }
            if (IsModifier(args.VirtualKey))
            {
                return;
            }
            bool ctrl = IsDown(VirtualKey.Control);
            bool alt = args.KeyStatus.IsMenuKeyDown || IsDown(VirtualKey.Menu);
            bool shift = IsDown(VirtualKey.Shift);
            if (TryCommit(args.VirtualKey, shift, ctrl, alt, null))
            {
                args.Handled = true;
            }
        }

        // 返回 true 表示已消费（调用方置 Handled），无论成功录制还是取消。
        private bool TryCommit(VirtualKey vk, bool shift, bool ctrl, bool alt, KeyEventArgs keyArgs)
        {
            // Esc 单独按下 = 取消录制（快捷键极少用裸 Esc）。
            if (vk == VirtualKey.Escape && !ctrl && !alt && !shift)
            {
                CancelListening();
                Toast.Show("已取消");
                return true;
            }
            TerminalKey key;
            char character;
            if (!HardwareKeyboardInput.TryMapVirtualKey(vk, shift, out key, out character))
            {
                return false;
            }
            var chord = new ShortcutChord(ctrl, alt, shift, key, character);
            ShortcutRow row = _listening;
            DetachCapture();
            _listening = null;
            _chords[row.Action] = chord;
            row.ChordText = chord.ToDisplay();
            Save();
            RefreshRowText(row);
            if (!ctrl && !alt && !shift)
            {
                Toast.Show("已保存（无修饰键会抢占终端输入，请谨慎）");
            }
            else
            {
                Toast.Show("已保存 " + chord.ToDisplay());
            }
            return true;
        }

        private bool IsDown(VirtualKey key)
        {
            try
            {
                CoreWindow window = Window.Current != null ? Window.Current.CoreWindow : null;
                if (window == null)
                {
                    return false;
                }
                return (window.GetKeyState(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsModifier(VirtualKey vk)
        {
            return vk == VirtualKey.Control || vk == VirtualKey.Shift || vk == VirtualKey.Menu
                || vk == VirtualKey.LeftControl || vk == VirtualKey.RightControl
                || vk == VirtualKey.LeftShift || vk == VirtualKey.RightShift
                || vk == VirtualKey.LeftMenu || vk == VirtualKey.RightMenu;
        }
    }
}
