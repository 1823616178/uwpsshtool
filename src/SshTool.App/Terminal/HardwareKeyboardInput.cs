using System;
using SshTool.Core.Terminal;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace SshTool.App.Terminal
{
    // T11：物理键盘。焦点在哨兵时不走 CoreWindow（§7.5 第 1 条）；
    // AcceleratorKey 的 Character 事件 VirtualKey 不可信（SP05）。
    public sealed class HardwareKeyboardInput : IDisposable
    {
        // VK_OEM_PLUS / VK_OEM_MINUS（VirtualKey 枚举在 15063 不含这两项）
        private const int OemPlusVirtualKey = 187;
        private const int OemMinusVirtualKey = 189;

        private CoreWindow _window;
        private CoreDispatcher _dispatcher;
        private bool _disposed;

        public HardwareKeyboardInput()
        {
            Shortcuts = ShortcutMap.Default;
            Sticky = new StickyModifiers();
            Modes = new TerminalModes();
        }

        public ShortcutMap Shortcuts { get; set; }

        public StickyModifiers Sticky { get; set; }

        public TerminalModes Modes { get; set; }

        public bool BackspaceAsBs { get; set; }

        public Func<bool> SoftInputHasFocus { get; set; }

        public event EventHandler<TerminalInputEventArgs> Input;

        public event EventHandler<ShortcutActionEventArgs> Shortcut;

        public event EventHandler HardwareActivity;

        public void Attach()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(HardwareKeyboardInput));
            }
            Detach();
            Window current = Window.Current;
            _window = current != null ? current.CoreWindow : null;
            _dispatcher = current != null ? current.Dispatcher : null;
            if (_window != null)
            {
                _window.KeyDown += OnKeyDown;
                _window.CharacterReceived += OnCharacterReceived;
            }
            if (_dispatcher != null)
            {
                _dispatcher.AcceleratorKeyActivated += OnAcceleratorKey;
            }
        }

        public void Detach()
        {
            if (_window != null)
            {
                _window.KeyDown -= OnKeyDown;
                _window.CharacterReceived -= OnCharacterReceived;
                _window = null;
            }
            if (_dispatcher != null)
            {
                _dispatcher.AcceleratorKeyActivated -= OnAcceleratorKey;
                _dispatcher = null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Detach();
            Input = null;
            Shortcut = null;
            HardwareActivity = null;
        }

        private bool SentinelOwnsPrintable()
        {
            Func<bool> probe = SoftInputHasFocus;
            return probe != null && probe();
        }

        private void OnKeyDown(CoreWindow sender, KeyEventArgs args)
        {
            if (args == null || args.Handled)
            {
                return;
            }
            VirtualKey vk = args.VirtualKey;
            if (vk == VirtualKey.Control || vk == VirtualKey.Shift || vk == VirtualKey.Menu
                || vk == VirtualKey.LeftControl || vk == VirtualKey.RightControl
                || vk == VirtualKey.LeftShift || vk == VirtualKey.RightShift
                || vk == VirtualKey.LeftMenu || vk == VirtualKey.RightMenu)
            {
                return;
            }

            bool ctrl = IsDown(VirtualKey.Control);
            bool alt = IsDown(VirtualKey.Menu);
            bool shift = IsDown(VirtualKey.Shift);
            TerminalKey key;
            char character;
            if (!TryMapVirtualKey(vk, shift, out key, out character))
            {
                return;
            }

            if (alt)
            {
                return;
            }

            if (SentinelOwnsPrintable())
            {
                if (!ctrl && (key == TerminalKey.Char
                    || key == TerminalKey.Enter || key == TerminalKey.Backspace))
                {
                    return;
                }
            }

            ShortcutAction action;
            if (Shortcuts != null && Shortcuts.TryMatch(ctrl, alt, shift, key, character, out action))
            {
                args.Handled = true;
                RaiseHardwareActivity();
                EventHandler<ShortcutActionEventArgs> handler = Shortcut;
                if (handler != null)
                {
                    handler(this, new ShortcutActionEventArgs(action));
                }
                return;
            }

            if (key == TerminalKey.Char && !ctrl && !alt)
            {
                return;
            }

            byte[] data = MapKey(key, character, ctrl, alt, shift);
            if (data == null || data.Length == 0)
            {
                return;
            }
            args.Handled = true;
            RaiseHardwareActivity();
            Emit(data);
        }

        private void OnCharacterReceived(CoreWindow sender, CharacterReceivedEventArgs args)
        {
            if (args == null || args.Handled || SentinelOwnsPrintable())
            {
                return;
            }
            uint code = args.KeyCode;
            if (code < 32 || code == 127)
            {
                return;
            }
            if (IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu))
            {
                return;
            }
            string text = char.ConvertFromUtf32((int)code);
            RaiseHardwareActivity();
            Emit(System.Text.Encoding.UTF8.GetBytes(text));
            args.Handled = true;
        }

        private void OnAcceleratorKey(CoreDispatcher sender, AcceleratorKeyEventArgs args)
        {
            if (args == null || args.Handled)
            {
                return;
            }
            if (args.EventType != CoreAcceleratorKeyEventType.SystemKeyDown
                && args.EventType != CoreAcceleratorKeyEventType.KeyDown)
            {
                return;
            }
            // Character 事件里 VirtualKey 是字符码，不能用来判键（SP05）。
            if (args.EventType == CoreAcceleratorKeyEventType.KeyDown
                && args.VirtualKey != VirtualKey.F10
                && !args.KeyStatus.IsMenuKeyDown)
            {
                return;
            }

            bool ctrl = IsDown(VirtualKey.Control);
            bool alt = args.KeyStatus.IsMenuKeyDown || IsDown(VirtualKey.Menu);
            bool shift = IsDown(VirtualKey.Shift);
            TerminalKey key;
            char character;
            if (!TryMapVirtualKey(args.VirtualKey, shift, out key, out character))
            {
                return;
            }
            if (SentinelOwnsPrintable() && !ctrl && !alt
                && (key == TerminalKey.Char || key == TerminalKey.Enter || key == TerminalKey.Backspace))
            {
                return;
            }

            ShortcutAction action;
            if (Shortcuts != null && Shortcuts.TryMatch(ctrl, alt, shift, key, character, out action))
            {
                args.Handled = true;
                RaiseHardwareActivity();
                EventHandler<ShortcutActionEventArgs> handler = Shortcut;
                if (handler != null)
                {
                    handler(this, new ShortcutActionEventArgs(action));
                }
                return;
            }

            if (!alt && args.VirtualKey != VirtualKey.F10)
            {
                return;
            }
            byte[] data = MapKey(key, character, ctrl, alt, shift);
            if (data == null || data.Length == 0)
            {
                return;
            }
            args.Handled = true;
            RaiseHardwareActivity();
            Emit(data);
        }

        private byte[] MapKey(TerminalKey key, char character, bool ctrl, bool alt, bool shift)
        {
            bool stickyCtrl = Sticky != null && Sticky.Ctrl != StickyState.Off;
            bool stickyAlt = Sticky != null && Sticky.Alt != StickyState.Off;
            bool stickyShift = Sticky != null && Sticky.Shift != StickyState.Off;
            var chord = new KeyChord(key, character,
                ctrl || stickyCtrl, alt || stickyAlt, shift || stickyShift);
            if (Sticky != null)
            {
                Sticky.Wrap(key, character);
            }
            byte[] data = KeyMap.Map(chord, Modes ?? new TerminalModes(), BackspaceAsBs);
            if (data == null && key == TerminalKey.Char && character != '\0' && !chord.Ctrl)
            {
                data = System.Text.Encoding.UTF8.GetBytes(new string(character, 1));
            }
            return data;
        }

        private void Emit(byte[] data)
        {
            EventHandler<TerminalInputEventArgs> handler = Input;
            if (handler != null)
            {
                handler(this, new TerminalInputEventArgs(data));
            }
        }

        private void RaiseHardwareActivity()
        {
            EventHandler handler = HardwareActivity;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private bool IsDown(VirtualKey key)
        {
            if (_window == null)
            {
                return false;
            }
            return (_window.GetKeyState(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        }

        internal static bool TryMapVirtualKey(VirtualKey vk, bool shift, out TerminalKey key, out char character)
        {
            key = TerminalKey.None;
            character = '\0';
            if (vk >= VirtualKey.A && vk <= VirtualKey.Z)
            {
                key = TerminalKey.Char;
                character = (char)('a' + (vk - VirtualKey.A));
                return true;
            }
            if (vk >= VirtualKey.Number0 && vk <= VirtualKey.Number9)
            {
                key = TerminalKey.Char;
                character = (char)('0' + (vk - VirtualKey.Number0));
                return true;
            }
            switch (vk)
            {
                case VirtualKey.Up: key = TerminalKey.Up; return true;
                case VirtualKey.Down: key = TerminalKey.Down; return true;
                case VirtualKey.Left: key = TerminalKey.Left; return true;
                case VirtualKey.Right: key = TerminalKey.Right; return true;
                case VirtualKey.Tab: key = TerminalKey.Tab; return true;
                case VirtualKey.Enter: key = TerminalKey.Enter; return true;
                case VirtualKey.Escape: key = TerminalKey.Escape; return true;
                case VirtualKey.Back: key = TerminalKey.Backspace; return true;
                case VirtualKey.Home: key = TerminalKey.Home; return true;
                case VirtualKey.End: key = TerminalKey.End; return true;
                case VirtualKey.PageUp: key = TerminalKey.PageUp; return true;
                case VirtualKey.PageDown: key = TerminalKey.PageDown; return true;
                case VirtualKey.Insert: key = TerminalKey.Insert; return true;
                case VirtualKey.Delete: key = TerminalKey.Delete; return true;
                case VirtualKey.F1: key = TerminalKey.F1; return true;
                case VirtualKey.F2: key = TerminalKey.F2; return true;
                case VirtualKey.F3: key = TerminalKey.F3; return true;
                case VirtualKey.F4: key = TerminalKey.F4; return true;
                case VirtualKey.F5: key = TerminalKey.F5; return true;
                case VirtualKey.F6: key = TerminalKey.F6; return true;
                case VirtualKey.F7: key = TerminalKey.F7; return true;
                case VirtualKey.F8: key = TerminalKey.F8; return true;
                case VirtualKey.F9: key = TerminalKey.F9; return true;
                case VirtualKey.F10: key = TerminalKey.F10; return true;
                case VirtualKey.F11: key = TerminalKey.F11; return true;
                case VirtualKey.F12: key = TerminalKey.F12; return true;
                case VirtualKey.Subtract:
                    key = TerminalKey.Char; character = '-'; return true;
                case VirtualKey.Add:
                    key = TerminalKey.Char; character = '='; return true;
                default:
                    int code = (int)vk;
                    if (code == OemPlusVirtualKey)
                    {
                        key = TerminalKey.Char;
                        character = '=';
                        return true;
                    }
                    if (code == OemMinusVirtualKey)
                    {
                        key = TerminalKey.Char;
                        character = '-';
                        return true;
                    }
                    return false;
            }
        }

        private static bool HardwareKeyboardPresent()
        {
            try
            {
                return new Windows.Devices.Input.KeyboardCapabilities().KeyboardPresent != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool IsHardwareKeyboardPresent
        {
            get { return HardwareKeyboardPresent(); }
        }
    }
}
