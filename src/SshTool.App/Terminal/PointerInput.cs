using System;
using SshTool.Core.Terminal;
using Windows.Devices.Input;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;

namespace SshTool.App.Terminal
{
    // T14：触摸平移 / 惯性 / 捏合。选择拖动期间不抢手势。
    public sealed class PointerInput : IDisposable
    {
        // ManipulationInertiaStarting 的减速度，单位 DIPs/ms²。
        public const double InertiaDeceleration = 0.0005;
        public const int WheelDeltaPerNotch = 120;
        public const int DoubleClickMilliseconds = 400;

        private readonly ScrollLineAccumulator _accumulator = new ScrollLineAccumulator();
        private UIElement _element;
        private bool _pinching;
        private float _pinchStartSize;
        private bool _disposed;
        private bool _mouseLeftDown;
        private bool _reportingDrag;
        private int _clickCount;
        private int _clickRow;
        private int _clickCol;
        private long _lastClickTicks;

        public PointerInput()
        {
            Scroll = new ScrollController();
            AltScreenScroll = "arrows";
        }

        public ScrollController Scroll { get; private set; }

        public string AltScreenScroll { get; set; }

        public bool AltScreen { get; set; }

        public int MouseMode { get; set; }

        public bool MouseSgr { get; set; }

        public bool ApplicationCursorKeys { get; set; }

        public double CellHeight { get; set; }

        public double CellWidth { get; set; }

        public double Padding { get; set; }

        public bool SelectionActive { get; set; }

        public Action SyncHost { get; set; }

        public float FontSize { get; set; }

        public int Cols { get; set; }

        public int Rows { get; set; }

        public event EventHandler ScrollChanged;

        public event EventHandler<TerminalInputEventArgs> Input;

        public event EventHandler FontSizeChanging;

        public event EventHandler FontSizeCommitted;

        public event EventHandler<CellHitEventArgs> SelectWord;

        public event EventHandler<CellHitEventArgs> SelectLine;

        public event EventHandler<CellHitEventArgs> SelectBegin;

        public event EventHandler<CellHitEventArgs> SelectExtend;

        public event EventHandler<Windows.Foundation.Point> ContextMenu;

        public void Attach(UIElement element)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PointerInput));
            }
            Detach();
            _element = element;
            if (_element == null)
            {
                return;
            }
            _element.ManipulationMode = ManipulationModes.TranslateY
                | ManipulationModes.Scale
                | ManipulationModes.TranslateInertia;
            _element.ManipulationStarted += OnStarted;
            _element.ManipulationDelta += OnDelta;
            _element.ManipulationCompleted += OnCompleted;
            _element.ManipulationInertiaStarting += OnInertiaStarting;
            _element.PointerWheelChanged += OnWheel;
            _element.PointerPressed += OnPointerPressed;
            _element.PointerMoved += OnPointerMoved;
            _element.PointerReleased += OnPointerReleased;
        }

        public void Detach()
        {
            if (_element == null)
            {
                return;
            }
            _element.ManipulationStarted -= OnStarted;
            _element.ManipulationDelta -= OnDelta;
            _element.ManipulationCompleted -= OnCompleted;
            _element.ManipulationInertiaStarting -= OnInertiaStarting;
            _element.PointerWheelChanged -= OnWheel;
            _element.PointerPressed -= OnPointerPressed;
            _element.PointerMoved -= OnPointerMoved;
            _element.PointerReleased -= OnPointerReleased;
            _element.ManipulationMode = ManipulationModes.System;
            _element = null;
            _accumulator.Reset();
            _pinching = false;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Detach();
        }

        private void OnStarted(object sender, ManipulationStartedRoutedEventArgs e)
        {
            Sync();
            if (SelectionActive)
            {
                e.Complete();
                return;
            }
            _accumulator.Reset();
            _pinching = false;
            _pinchStartSize = FontSize;
        }

        private void OnDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            Sync();
            if (SelectionActive)
            {
                return;
            }
            if (!_pinching && Math.Abs(e.Cumulative.Scale - 1) > 0.02)
            {
                _pinching = true;
            }
            if (_pinching)
            {
                int size = ScrollController.ClampFontSize(_pinchStartSize * e.Cumulative.Scale);
                FontSize = size;
                EventHandler changing = FontSizeChanging;
                if (changing != null)
                {
                    changing(this, EventArgs.Empty);
                }
                return;
            }

            int lines = _accumulator.Push(e.Delta.Translation.Y, CellHeight);
            ApplyScrollLines(lines, e.Position.X, e.Position.Y);
        }

        private void OnCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            _accumulator.Reset();
            if (_pinching)
            {
                EventHandler committed = FontSizeCommitted;
                if (committed != null)
                {
                    committed(this, EventArgs.Empty);
                }
            }
            _pinching = false;
        }

        private void OnInertiaStarting(object sender, ManipulationInertiaStartingRoutedEventArgs e)
        {
            if (_pinching || SelectionActive)
            {
                return;
            }
            e.TranslationBehavior.DesiredDeceleration = InertiaDeceleration;
        }

        private void OnWheel(object sender, PointerRoutedEventArgs e)
        {
            Sync();
            if (SelectionActive)
            {
                return;
            }
            int delta = e.GetCurrentPoint(_element).Properties.MouseWheelDelta;
            if (delta == 0)
            {
                return;
            }
            int notches = delta / WheelDeltaPerNotch;
            if (notches == 0)
            {
                notches = delta > 0 ? 1 : -1;
            }
            Windows.Foundation.Point pos = e.GetCurrentPoint(_element).Position;
            int col1;
            int row1;
            HitCell1(pos, out col1, out row1);
            if (MouseMode != MouseEncoder.ModeOff
                && MouseEncoder.ShouldReport(MouseMode, MouseEventKind.Wheel, false))
            {
                bool up = notches > 0;
                int count = notches > 0 ? notches : -notches;
                EventHandler<TerminalInputEventArgs> input = Input;
                for (int i = 0; i < count; i++)
                {
                    byte[] seq = MouseEncoder.EncodeWheel(up, col1, row1, MouseSgr);
                    if (input != null)
                    {
                        input(this, new TerminalInputEventArgs(seq));
                    }
                }
                e.Handled = true;
                return;
            }
            ApplyScrollLines(notches * ScrollController.WheelLinesPerNotch, pos.X, pos.Y);
            e.Handled = true;
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            Sync();
            PointerPoint point = e.GetCurrentPoint(_element);
            if (point.PointerDevice == null)
            {
                return;
            }
            bool mouse = point.PointerDevice.PointerDeviceType == PointerDeviceType.Mouse;
            if (!mouse)
            {
                return;
            }
            int row;
            int col;
            if (!HitCell0(point.Position, out row, out col))
            {
                return;
            }
            if (point.Properties.IsRightButtonPressed)
            {
                EventHandler<Windows.Foundation.Point> menu = ContextMenu;
                if (menu != null)
                {
                    menu(this, point.Position);
                }
                e.Handled = true;
                return;
            }
            if (!point.Properties.IsLeftButtonPressed)
            {
                return;
            }
            _mouseLeftDown = true;
            bool shift = IsShiftDown();
            bool report = MouseMode != MouseEncoder.ModeOff && !shift
                && MouseEncoder.ShouldReport(MouseMode, MouseEventKind.Press, true);
            if (report)
            {
                _reportingDrag = true;
                EmitMouse(MouseEventKind.Press, MouseEncoder.LeftButton, col + 1, row + 1, shift);
                e.Handled = true;
                return;
            }
            _reportingDrag = false;
            int taps = UpdateClickCount(row, col);
            if (taps >= 3)
            {
                RaiseCell(SelectLine, row, col);
            }
            else if (taps == 2)
            {
                RaiseCell(SelectWord, row, col);
            }
            else
            {
                RaiseCell(SelectBegin, row, col);
            }
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_mouseLeftDown)
            {
                Sync();
                if (MouseMode == MouseEncoder.ModeAnyEvent && !_reportingDrag)
                {
                    PointerPoint hover = e.GetCurrentPoint(_element);
                    int hr;
                    int hc;
                    if (HitCell0(hover.Position, out hr, out hc)
                        && MouseEncoder.ShouldReport(MouseMode, MouseEventKind.Move, false))
                    {
                        EmitMouse(MouseEventKind.Move, MouseEncoder.LeftButton, hc + 1, hr + 1, IsShiftDown());
                    }
                }
                return;
            }
            Sync();
            PointerPoint point = e.GetCurrentPoint(_element);
            int row;
            int col;
            if (!HitCell0(point.Position, out row, out col))
            {
                return;
            }
            bool shift = IsShiftDown();
            if (_reportingDrag
                && MouseEncoder.ShouldReport(MouseMode, MouseEventKind.Move, true))
            {
                EmitMouse(MouseEventKind.Move, MouseEncoder.LeftButton, col + 1, row + 1, shift);
                e.Handled = true;
                return;
            }
            RaiseCell(SelectExtend, row, col);
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_mouseLeftDown)
            {
                return;
            }
            Sync();
            PointerPoint point = e.GetCurrentPoint(_element);
            int row;
            int col;
            HitCell0(point.Position, out row, out col);
            if (_reportingDrag
                && MouseEncoder.ShouldReport(MouseMode, MouseEventKind.Release, false))
            {
                EmitMouse(MouseEventKind.Release, MouseEncoder.LeftButton, col + 1, row + 1, IsShiftDown());
            }
            _mouseLeftDown = false;
            _reportingDrag = false;
        }

        private int UpdateClickCount(int row, int col)
        {
            long now = DateTime.UtcNow.Ticks;
            long window = TimeSpan.FromMilliseconds(DoubleClickMilliseconds).Ticks;
            if (row == _clickRow && col == _clickCol && now - _lastClickTicks <= window)
            {
                _clickCount++;
            }
            else
            {
                _clickCount = 1;
            }
            _clickRow = row;
            _clickCol = col;
            _lastClickTicks = now;
            return _clickCount;
        }

        private void EmitMouse(MouseEventKind kind, int button, int col1, int row1, bool shift)
        {
            byte[] seq = MouseEncoder.Encode(MouseMode, MouseSgr, kind, button, col1, row1,
                shift, false, false);
            EventHandler<TerminalInputEventArgs> input = Input;
            if (input != null)
            {
                input(this, new TerminalInputEventArgs(seq));
            }
        }

        private void RaiseCell(EventHandler<CellHitEventArgs> handler, int row, int col)
        {
            if (handler != null)
            {
                handler(this, new CellHitEventArgs(row, col));
            }
        }

        private bool HitCell0(Windows.Foundation.Point pos, out int row, out int col)
        {
            row = 0;
            col = 0;
            if (CellWidth <= 0 || CellHeight <= 0)
            {
                return false;
            }
            col = (int)((pos.X - Padding) / CellWidth);
            row = (int)((pos.Y - Padding) / CellHeight);
            if (col < 0) { col = 0; }
            if (row < 0) { row = 0; }
            if (Cols > 0 && col >= Cols) { col = Cols - 1; }
            if (Rows > 0 && row >= Rows) { row = Rows - 1; }
            return true;
        }

        private void HitCell1(Windows.Foundation.Point pos, out int col1, out int row1)
        {
            int row;
            int col;
            HitCell0(pos, out row, out col);
            col1 = col + 1;
            row1 = row + 1;
        }

        private static bool IsShiftDown()
        {
            try
            {
                Window window = Window.Current;
                CoreWindow core = window != null ? window.CoreWindow : null;
                if (core == null)
                {
                    return false;
                }
                return (core.GetKeyState(VirtualKey.Shift) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void Sync()
        {
            Action sync = SyncHost;
            if (sync != null)
            {
                sync();
            }
        }

        private void ApplyScrollLines(int lines, double x, double y)
        {
            if (lines == 0)
            {
                return;
            }
            if (AltScreen)
            {
                bool wheel = string.Equals(AltScreenScroll, "wheel", StringComparison.Ordinal);
                int col1 = CellWidth > 0 ? (int)(x / CellWidth) + 1 : 1;
                int row1 = CellHeight > 0 ? (int)(y / CellHeight) + 1 : 1;
                if (Cols > 0 && col1 > Cols) { col1 = Cols; }
                if (Rows > 0 && row1 > Rows) { row1 = Rows; }
                if (col1 < 1) { col1 = 1; }
                if (row1 < 1) { row1 = 1; }
                var modes = new TerminalModes { ApplicationCursorKeys = ApplicationCursorKeys };
                var sequences = MouseEncoder.EncodeAltScroll(
                    lines, wheel, MouseMode, MouseSgr, col1, row1, modes);
                EventHandler<TerminalInputEventArgs> input = Input;
                if (input != null)
                {
                    for (int i = 0; i < sequences.Count; i++)
                    {
                        input(this, new TerminalInputEventArgs(sequences[i]));
                    }
                }
                return;
            }

            Scroll.ApplyLines(lines);
            EventHandler scrolled = ScrollChanged;
            if (scrolled != null)
            {
                scrolled(this, EventArgs.Empty);
            }
        }
    }
}
