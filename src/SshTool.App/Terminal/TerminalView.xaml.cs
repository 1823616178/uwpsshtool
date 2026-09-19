using System;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using SshTool.App.Infrastructure;
using SshTool.Core.Models;
using SshTool.Core.Sessions;
using SshTool.Core.Terminal;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace SshTool.App.Terminal
{
    // T06/T07/T09：ITerminalScreen → Win2D；尺寸/字体/DPI/SIP 变化后 100 ms 防抖 Resize。
    // CreateResources / DeviceLost 重建行缓存；属性管线在 TerminalRenderer。
    public sealed partial class TerminalView : UserControl
    {
        private readonly TerminalRenderer _renderer = new TerminalRenderer();
        private readonly SoftKeyboardInput _softKeyboard = new SoftKeyboardInput();
        private readonly StickyModifiers _sticky = new StickyModifiers();
        private readonly TerminalModes _modes = new TerminalModes();
        private readonly string _schedulerId = "tv-" + Guid.NewGuid().ToString("N");
        private readonly DispatcherTimer _resizeTimer = new DispatcherTimer();
        private ISshSession _session;
        private ITerminalScreen _screen;
        private byte[] _cells = new byte[0];
        private byte[] _dirty = new byte[0];
        private long _lastRevision = -1;
        private bool _lastBlink = true;
        private bool _fullRedraw = true;
        private bool _registered;
        private bool _loaded;
        private CanvasDevice _watchedDevice;
        private GridSize _gridSize;
        private int _lastResizeCols;
        private int _lastResizeRows;
        private double _terminalPadding = 4;
        private double _keyBarHeight;
        private bool _keyBarOverlays;

        public TerminalView()
        {
            this.InitializeComponent();
            this.Loaded += OnLoaded;
            this.Unloaded += OnUnloaded;
            this.GotFocus += OnGotFocus;
            this.LostFocus += OnLostFocus;
            this.Tapped += OnTapped;
            this.SizeChanged += OnSizeChanged;
            _renderer.MetricsInvalidated += OnMetricsInvalidated;
            _resizeTimer.Interval = TimeSpan.FromMilliseconds(100);
            _resizeTimer.Tick += OnResizeTimerTick;
            _softKeyboard.Sticky = _sticky;
            _softKeyboard.Modes = _modes;
            _softKeyboard.Input += OnSoftKeyboardInput;
            _softKeyboard.OcclusionChanged += OnOcclusionChanged;
        }

        public event EventHandler<TerminalInputEventArgs> Input;

        public StickyModifiers StickyModifiers
        {
            get { return _sticky; }
        }

        public TerminalModes TerminalModes
        {
            get { return _modes; }
        }

        public bool BackspaceAsBs
        {
            get { return _softKeyboard.BackspaceAsBs; }
            set { _softKeyboard.BackspaceAsBs = value; }
        }

        public bool FocusInput()
        {
            return _softKeyboard.Focus();
        }

        public void ToggleSoftKeyboard()
        {
            if (_softKeyboard.IsInputPaneVisible)
            {
                _softKeyboard.HidePane();
                return;
            }
            FocusInput();
        }

        public void SendInput(byte[] data)
        {
            OnSoftKeyboardInput(this, new TerminalInputEventArgs(data ?? new byte[0]));
        }

        public ISshSession Session
        {
            get { return _session; }
            set
            {
                if (ReferenceEquals(_session, value))
                {
                    return;
                }
                _session = value;
                _lastResizeCols = 0;
                _lastResizeRows = 0;
                Screen = value != null ? value.Screen : null;
                ScheduleResize();
            }
        }

        public ITerminalScreen Screen
        {
            get { return _screen; }
            set
            {
                _screen = value;
                _lastRevision = -1;
                _fullRedraw = true;
                FrameScheduler.Instance.Wake();
            }
        }

        public long LastRevision
        {
            get { return _lastRevision; }
        }

        public TerminalRenderer Renderer
        {
            get { return _renderer; }
        }

        public GridSize GridSize
        {
            get { return _gridSize; }
        }

        public double TerminalPadding
        {
            get { return _terminalPadding; }
            set
            {
                double next = NormalizeNonNegative(value);
                if (_terminalPadding != next)
                {
                    _terminalPadding = next;
                    ApplyCanvasPadding();
                    ScheduleResize();
                }
            }
        }

        public double KeyBarHeight
        {
            get { return _keyBarHeight; }
            set
            {
                double next = NormalizeNonNegative(value);
                if (_keyBarHeight != next)
                {
                    _keyBarHeight = next;
                    ScheduleResize();
                }
            }
        }

        public bool KeyBarOverlays
        {
            get { return _keyBarOverlays; }
            set
            {
                if (_keyBarOverlays != value)
                {
                    _keyBarOverlays = value;
                    ScheduleResize();
                }
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _loaded = true;
            ApplyTokenColors();
            ApplyCanvasPadding();
            if (Sentinel != null)
            {
                _softKeyboard.Attach(Sentinel);
            }
            if (!_registered)
            {
                FrameScheduler.Instance.Register(_schedulerId, OnTick);
                _registered = true;
            }
            FrameScheduler.Instance.SetVisible(_schedulerId, true);
            ScheduleResize();
            FrameScheduler.Instance.Wake();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _loaded = false;
            _resizeTimer.Stop();
            _softKeyboard.Detach();
            UnsubscribeDeviceLost();
            if (_registered)
            {
                FrameScheduler.Instance.SetVisible(_schedulerId, false);
                FrameScheduler.Instance.Unregister(_schedulerId);
                _registered = false;
            }
            _renderer.Dispose();
            if (Canvas != null)
            {
                Canvas.RemoveFromVisualTree();
            }
        }

        private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
        {
            ApplyTokenColors();
            _renderer.NotifyDeviceLost();
            if (_renderer.EnsureMetrics(sender))
            {
                ScheduleResize();
            }
            SubscribeDeviceLost(sender != null ? sender.Device : null);
            _fullRedraw = true;
        }

        private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            if (_screen == null)
            {
                args.DrawingSession.Clear(ToColor(_renderer.DefaultBgArgb));
                return;
            }
            _renderer.Paint(sender, args.DrawingSession, _screen, _cells, _dirty, _fullRedraw, _lastBlink);
            _fullRedraw = false;
        }

        private bool OnTick()
        {
            if (_screen == null || Canvas == null)
            {
                return false;
            }
            bool blink = FrameScheduler.Instance.Core.BlinkOn;
            bool blinkChanged = blink != _lastBlink;
            _lastBlink = blink;
            if (!_renderer.Focused)
            {
                blinkChanged = false;
            }

            bool pulled = false;
            if (_screen.Revision != _lastRevision)
            {
                pulled = Pull();
                _lastRevision = _screen.Revision;
            }
            if (pulled || blinkChanged || _fullRedraw)
            {
                Canvas.Invalidate();
                return true;
            }
            return false;
        }

        private bool Pull()
        {
            int cols = _screen.Cols;
            int rows = _screen.Rows;
            if (cols <= 0 || rows <= 0)
            {
                return false;
            }
            int cellBytes = rows * cols * TerminalCell.BytesPerCell;
            int dirtyBytes = (rows + 7) / 8;
            if (_cells.Length != cellBytes)
            {
                _cells = new byte[cellBytes];
                _fullRedraw = true;
            }
            if (_dirty.Length != dirtyBytes)
            {
                _dirty = new byte[dirtyBytes];
            }

            bool changed = _screen.CopyDirtyRows(_cells, _dirty);
            if (!changed)
            {
                _screen.CopyViewport(0, _cells);
                _fullRedraw = true;
                changed = true;
            }

            if (_renderer.CellWidth > 0)
            {
                Canvas.Width = cols * _renderer.CellWidth;
                Canvas.Height = rows * _renderer.CellHeight;
            }
            return changed;
        }

        private void OnTapped(object sender, TappedRoutedEventArgs e)
        {
            FocusInput();
        }

        private void OnSoftKeyboardInput(object sender, TerminalInputEventArgs e)
        {
            if (e == null || e.Data == null || e.Data.Length == 0)
            {
                return;
            }
            EventHandler<TerminalInputEventArgs> handler = Input;
            if (handler != null)
            {
                handler(this, e);
            }
            ISshSession session = _session;
            if (session != null)
            {
                session.Write(e.Data);
            }
        }

        private void OnOcclusionChanged(object sender, EventArgs e)
        {
            ScheduleResize();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ScheduleResize();
        }

        private void OnMetricsInvalidated(object sender, EventArgs e)
        {
            _fullRedraw = true;
            ScheduleResize();
            if (Canvas != null)
            {
                Canvas.Invalidate();
            }
        }

        private void ScheduleResize()
        {
            if (!_loaded)
            {
                return;
            }
            _resizeTimer.Stop();
            _resizeTimer.Start();
        }

        private void OnResizeTimerTick(object sender, object e)
        {
            _resizeTimer.Stop();
            UpdateGridAndResize();
        }

        private void UpdateGridAndResize()
        {
            if (Canvas == null || ActualWidth <= 0 || ActualHeight <= 0
                || !_renderer.EnsureMetrics(Canvas))
            {
                return;
            }

            GridSize size = GridSizeCalculator.Calculate(
                ActualWidth, ActualHeight,
                _renderer.CellWidth, _renderer.CellHeight,
                TerminalPadding, KeyBarHeight, KeyBarOverlays,
                OcclusionOverlapHeight());
            _gridSize = size;
            Canvas.Width = size.Cols * _renderer.CellWidth;
            Canvas.Height = size.Rows * _renderer.CellHeight;

            ISshSession session = _session;
            if (session != null
                && (size.Cols != _lastResizeCols || size.Rows != _lastResizeRows))
            {
                _lastResizeCols = size.Cols;
                _lastResizeRows = size.Rows;
                session.Resize(size.Cols, size.Rows);
            }
        }

        private void ApplyCanvasPadding()
        {
            if (Canvas != null)
            {
                Canvas.Margin = new Thickness(TerminalPadding);
            }
        }

        private void OnGotFocus(object sender, RoutedEventArgs e)
        {
            _renderer.Focused = true;
            FrameScheduler.Instance.Wake();
            if (Canvas != null)
            {
                Canvas.Invalidate();
            }
        }

        private void OnLostFocus(object sender, RoutedEventArgs e)
        {
            _renderer.Focused = false;
            FrameScheduler.Instance.Wake();
            if (Canvas != null)
            {
                Canvas.Invalidate();
            }
        }

        private void SubscribeDeviceLost(CanvasDevice device)
        {
            if (_watchedDevice == device)
            {
                return;
            }
            UnsubscribeDeviceLost();
            _watchedDevice = device;
            if (device != null)
            {
                device.DeviceLost += OnDeviceLost;
            }
        }

        private void UnsubscribeDeviceLost()
        {
            if (_watchedDevice != null)
            {
                _watchedDevice.DeviceLost -= OnDeviceLost;
                _watchedDevice = null;
            }
        }

        private void OnDeviceLost(CanvasDevice sender, object args)
        {
            DispatcherHelper.Post(() =>
            {
                _renderer.NotifyDeviceLost();
                _fullRedraw = true;
                if (Canvas != null)
                {
                    Canvas.Invalidate();
                }
                FrameScheduler.Instance.Wake();
            });
        }

        private void ApplyTokenColors()
        {
            _renderer.DefaultFgArgb = BrushArgb("AppTextBrush", TerminalRenderer.FallbackFgArgb);
            _renderer.DefaultBgArgb = BrushArgb("AppBgBrush", TerminalRenderer.FallbackBgArgb);
            _renderer.CursorColor = ToColor(BrushArgb("AppAccentBrush", 0xFF4C8DFF));
            try
            {
                AppearanceProfile appearance = Defaults.DefaultAppearance();
                _renderer.DefaultFgArgb = TerminalPalette.HexToArgb(appearance.Foreground);
                _renderer.DefaultBgArgb = TerminalPalette.HexToArgb(appearance.Background);
                _renderer.CursorColor = ToColor(TerminalPalette.HexToArgb(appearance.Cursor));
                _renderer.CursorStyle = appearance.CursorStyle;
                _renderer.CursorBlink = appearance.CursorBlink;
                _renderer.BoldAsBright = appearance.BoldAsBright;
                _renderer.FontWeightBold = appearance.FontWeightBold;
                _renderer.FontSize = appearance.FontSize;
                _renderer.LineHeightFactor = (float)appearance.LineHeight;
                TerminalPadding = appearance.Padding;
            }
            catch (Exception)
            {
                // Token 回退已写好；外观 hex 异常时保持 Token 色。
            }
        }

        private static uint BrushArgb(string key, uint fallback)
        {
            try
            {
                object resource = Application.Current.Resources[key];
                var brush = resource as SolidColorBrush;
                if (brush == null)
                {
                    return fallback;
                }
                Color c = brush.Color;
                return ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static Color ToColor(uint argb)
        {
            return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }

        private double OcclusionOverlapHeight()
        {
            Rect occluded = _softKeyboard.OccludedRect;
            if (occluded.Height <= 0 || occluded.Width <= 0 || ActualHeight <= 0)
            {
                return 0;
            }
            try
            {
                Window window = Window.Current;
                UIElement root = window != null ? window.Content : null;
                if (root == null)
                {
                    return occluded.Height;
                }
                GeneralTransform transform = TransformToVisual(root);
                Point origin = transform.TransformPoint(new Point(0, 0));
                double overlap = origin.Y + ActualHeight - occluded.Y;
                if (overlap <= 0)
                {
                    return 0;
                }
                if (overlap > ActualHeight)
                {
                    return ActualHeight;
                }
                return overlap;
            }
            catch (Exception)
            {
                return occluded.Height;
            }
        }

        private static double NormalizeNonNegative(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            {
                return 0;
            }
            return value;
        }
    }
}
