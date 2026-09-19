using System;
using System.Text;
using SshTool.Core.Terminal;
using Windows.Foundation;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace SshTool.App.Terminal
{
    // T09：软键盘通路（01-DESIGN.md §7.5，SP05 五条提交规则）。
    // 焦点在哨兵时只走 TextBox；CoreWindow 留给 T11。
    public sealed class SoftKeyboardInput : IDisposable
    {
        private TextBox _box;
        private InputPane _inputPane;
        private bool _resetting;
        private bool _composing;
        private bool _enterHeld;
        private bool _disposed;

        public SoftKeyboardInput()
        {
            Sticky = new StickyModifiers();
            Modes = new TerminalModes();
        }

        public StickyModifiers Sticky { get; set; }

        public TerminalModes Modes { get; set; }

        public bool BackspaceAsBs { get; set; }

        public bool IsComposing
        {
            get { return _composing; }
        }

        public bool HasFocus
        {
            get { return _box != null && _box.FocusState != FocusState.Unfocused; }
        }

        public Rect OccludedRect { get; private set; }

        public double OccludedHeight
        {
            get { return OccludedRect.Height; }
        }

        public event EventHandler<TerminalInputEventArgs> Input;

        public event EventHandler OcclusionChanged;

        public void Attach(TextBox box)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SoftKeyboardInput));
            }
            DetachBox();
            _box = box ?? throw new ArgumentNullException(nameof(box));
            _box.KeyDown += OnKeyDown;
            _box.KeyUp += OnKeyUp;
            _box.TextChanged += OnTextChanged;
            _box.TextCompositionStarted += OnCompositionStarted;
            _box.TextCompositionEnded += OnCompositionEnded;
            _box.GotFocus += OnGotFocus;
            _box.LostFocus += OnLostFocus;
            ResetSentinel();
            AttachInputPane();
        }

        public void Detach()
        {
            DetachBox();
            DetachInputPane();
            OccludedRect = new Rect();
            _composing = false;
            _enterHeld = false;
        }

        public bool Focus()
        {
            if (_box == null)
            {
                return false;
            }
            ResetSentinel();
            return _box.Focus(FocusState.Programmatic);
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
            OcclusionChanged = null;
        }

        private void AttachInputPane()
        {
            DetachInputPane();
            try
            {
                _inputPane = InputPane.GetForCurrentView();
            }
            catch (Exception)
            {
                _inputPane = null;
            }
            if (_inputPane != null)
            {
                _inputPane.Showing += OnInputPaneShowing;
                _inputPane.Hiding += OnInputPaneHiding;
            }
        }

        private void DetachInputPane()
        {
            if (_inputPane == null)
            {
                return;
            }
            _inputPane.Showing -= OnInputPaneShowing;
            _inputPane.Hiding -= OnInputPaneHiding;
            _inputPane = null;
        }

        private void DetachBox()
        {
            if (_box == null)
            {
                return;
            }
            _box.KeyDown -= OnKeyDown;
            _box.KeyUp -= OnKeyUp;
            _box.TextChanged -= OnTextChanged;
            _box.TextCompositionStarted -= OnCompositionStarted;
            _box.TextCompositionEnded -= OnCompositionEnded;
            _box.GotFocus -= OnGotFocus;
            _box.LostFocus -= OnLostFocus;
            _box = null;
        }

        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                if (_composing)
                {
                    // 组合中回车交给 IME 上屏，由 CompositionEnded 差分发送。
                    return;
                }
                if (_enterHeld)
                {
                    e.Handled = true;
                    return;
                }
                _enterHeld = true;
                e.Handled = true;
                EmitMapped(TerminalKey.Enter);
                return;
            }

            if (e.Key == Windows.System.VirtualKey.Back)
            {
                if (_composing)
                {
                    return;
                }
                e.Handled = true;
                EmitMapped(TerminalKey.Backspace);
            }
        }

        private void OnKeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                _enterHeld = false;
            }
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_resetting || _box == null || _composing)
            {
                return;
            }
            SentinelDiffResult diff = SentinelDiff.Compute(_box.Text);
            if (!diff.IsEmpty)
            {
                EmitDiff(diff, false);
                ResetSentinel();
                return;
            }
            if (!SentinelDiff.IsRestored(_box.Text))
            {
                ResetSentinel();
            }
        }

        private void OnCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
        {
            _composing = true;
        }

        private void OnCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
        {
            _composing = false;
            if (_resetting || sender == null)
            {
                return;
            }
            EmitDiff(SentinelDiff.Compute(sender.Text), true);
            ResetSentinel();
        }

        private void OnGotFocus(object sender, RoutedEventArgs e)
        {
            if (_box != null && !SentinelDiff.IsRestored(_box.Text) && !_composing)
            {
                ResetSentinel();
            }
        }

        private void OnLostFocus(object sender, RoutedEventArgs e)
        {
            _enterHeld = false;
        }

        private void OnInputPaneShowing(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            args.EnsuredFocusedElementInView = true;
            SetOcclusion(args.OccludedRect);
        }

        private void OnInputPaneHiding(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            args.EnsuredFocusedElementInView = true;
            SetOcclusion(new Rect());
        }

        private void SetOcclusion(Rect rect)
        {
            OccludedRect = rect;
            EventHandler handler = OcclusionChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void EmitDiff(SentinelDiffResult diff, bool sendDeletes)
        {
            if (diff.Inserted.Length > 0)
            {
                EmitText(diff.Inserted);
                return;
            }
            if (sendDeletes && diff.ShouldSendDeletes)
            {
                EmitBackspaces(diff.Deleted);
            }
        }

        private void EmitText(string text)
        {
            StickyModifiers sticky = Sticky;
            if (IsSingleCodePoint(text) && sticky != null
                && (sticky.Ctrl != StickyState.Off || sticky.Alt != StickyState.Off)
                && !char.IsSurrogate(text[0]))
            {
                byte[] mapped = KeyMap.Map(
                    sticky.Wrap(TerminalKey.Char, text[0]),
                    Modes ?? new TerminalModes(),
                    BackspaceAsBs);
                if (mapped != null)
                {
                    Emit(mapped);
                }
                return;
            }
            Emit(Encoding.UTF8.GetBytes(text));
        }

        private void EmitBackspaces(int count)
        {
            if (count <= 0)
            {
                return;
            }
            byte value = BackspaceAsBs ? (byte)0x08 : (byte)0x7F;
            var data = new byte[count];
            for (int i = 0; i < count; i++)
            {
                data[i] = value;
            }
            Emit(data);
        }

        private void EmitMapped(TerminalKey key)
        {
            StickyModifiers sticky = Sticky ?? new StickyModifiers();
            byte[] mapped = KeyMap.Map(sticky.Wrap(key), Modes ?? new TerminalModes(), BackspaceAsBs);
            Emit(mapped);
        }

        private void Emit(byte[] data)
        {
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

        private void ResetSentinel()
        {
            if (_box == null)
            {
                return;
            }
            _resetting = true;
            _box.Text = SentinelDiff.Restore();
            _box.SelectionStart = _box.Text.Length;
            _resetting = false;
        }

        private static bool IsSingleCodePoint(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            if (text.Length == 1)
            {
                return !char.IsSurrogate(text[0]);
            }
            return text.Length == 2 && char.IsHighSurrogate(text[0]) && char.IsLowSurrogate(text[1]);
        }
    }
}
