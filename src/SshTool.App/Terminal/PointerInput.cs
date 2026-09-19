using System;
using SshTool.Core.Terminal;
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

        private readonly ScrollLineAccumulator _accumulator = new ScrollLineAccumulator();
        private UIElement _element;
        private bool _pinching;
        private float _pinchStartSize;
        private bool _disposed;

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

        public bool SelectionActive { get; set; }

        public Action SyncHost { get; set; }

        public float FontSize { get; set; }

        public int Cols { get; set; }

        public int Rows { get; set; }

        public event EventHandler ScrollChanged;

        public event EventHandler<TerminalInputEventArgs> Input;

        public event EventHandler FontSizeChanging;

        public event EventHandler FontSizeCommitted;

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
            ApplyScrollLines(notches * ScrollController.WheelLinesPerNotch, pos.X, pos.Y);
            e.Handled = true;
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
