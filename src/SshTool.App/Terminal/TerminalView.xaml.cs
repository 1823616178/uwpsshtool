using System;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using SshTool.App.Controls;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.Core.Models;
using SshTool.Core.Sessions;
using SshTool.Core.Terminal;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Input;
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
        private readonly HardwareKeyboardInput _hardwareKeyboard = new HardwareKeyboardInput();
        private readonly StickyModifiers _sticky = new StickyModifiers();
        private readonly TerminalModes _modes = new TerminalModes();
        private readonly string _schedulerId = "tv-" + Guid.NewGuid().ToString("N");
        private readonly DispatcherTimer _resizeTimer = new DispatcherTimer();
        private readonly SelectionModel _selection = new SelectionModel();
        private bool _draggingSelection;
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
            this.Holding += OnHolding;
            this.PointerMoved += OnPointerMoved;
            this.PointerReleased += OnPointerReleased;
            this.SizeChanged += OnSizeChanged;
            _renderer.MetricsInvalidated += OnMetricsInvalidated;
            _resizeTimer.Interval = TimeSpan.FromMilliseconds(100);
            _resizeTimer.Tick += OnResizeTimerTick;
            _softKeyboard.Sticky = _sticky;
            _softKeyboard.Modes = _modes;
            _softKeyboard.Input += OnSoftKeyboardInput;
            _softKeyboard.OcclusionChanged += OnOcclusionChanged;
            _hardwareKeyboard.Sticky = _sticky;
            _hardwareKeyboard.Modes = _modes;
            _hardwareKeyboard.SoftInputHasFocus = () => _softKeyboard.HasFocus;
            _hardwareKeyboard.Input += OnHardwareInput;
            _hardwareKeyboard.Shortcut += OnHardwareShortcut;
            _hardwareKeyboard.HardwareActivity += OnHardwareActivity;
        }

        public event EventHandler<TerminalInputEventArgs> Input;

        public event EventHandler<ShortcutActionEventArgs> Shortcut;

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
            set
            {
                _softKeyboard.BackspaceAsBs = value;
                _hardwareKeyboard.BackspaceAsBs = value;
            }
        }

        public ShortcutMap Shortcuts
        {
            get { return _hardwareKeyboard.Shortcuts; }
            set { _hardwareKeyboard.Shortcuts = value ?? ShortcutMap.Default; }
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
            _hardwareKeyboard.Attach();
            if (Selection != null)
            {
                Selection.ToolbarAction += OnSelectionToolbar;
                Selection.HandleDrag += OnSelectionHandleDrag;
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
            _hardwareKeyboard.Detach();
            if (Selection != null)
            {
                Selection.ToolbarAction -= OnSelectionToolbar;
                Selection.HandleDrag -= OnSelectionHandleDrag;
            }
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
            if (_selection.IsActive)
            {
                _selection.Cancel();
                RefreshSelectionOverlay();
                return;
            }
            if (HardwareKeyboardInput.IsHardwareKeyboardPresent)
            {
                this.IsTabStop = true;
                _softKeyboard.HidePane();
                this.Focus(FocusState.Programmatic);
                return;
            }
            FocusInput();
        }

        private void OnHolding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != HoldingState.Started)
            {
                return;
            }
            int row;
            int col;
            if (!TryHitCell(e.GetPosition(this), out row, out col))
            {
                return;
            }
            ISelectionGrid grid = CurrentGrid();
            if (grid == null)
            {
                return;
            }
            _selection.BeginWord(row, col, grid);
            _draggingSelection = true;
            RefreshSelectionOverlay();
            e.Handled = true;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_draggingSelection || !_selection.IsActive || !e.Pointer.IsInContact)
            {
                return;
            }
            int row;
            int col;
            if (!TryHitCell(e.GetCurrentPoint(this).Position, out row, out col))
            {
                return;
            }
            ISelectionGrid grid = CurrentGrid();
            if (grid == null)
            {
                return;
            }
            _selection.Extend(row, col, grid);
            RefreshSelectionOverlay();
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _draggingSelection = false;
        }

        private void OnSelectionToolbar(object sender, SelectionToolbarEventArgs e)
        {
            if (e == null)
            {
                return;
            }
            switch (e.Action)
            {
                case SelectionToolbarAction.Copy:
                    CopySelection();
                    break;
                case SelectionToolbarAction.Paste:
                    PasteClipboard();
                    break;
                case SelectionToolbarAction.SelectAll:
                    ISelectionGrid grid = CurrentGrid();
                    if (grid != null)
                    {
                        _selection.SelectAll(grid);
                        RefreshSelectionOverlay();
                    }
                    break;
                case SelectionToolbarAction.Share:
                    CopySelection();
                    try
                    {
                        Windows.ApplicationModel.DataTransfer.DataTransferManager.ShowShareUI();
                    }
                    catch (Exception)
                    {
                    }
                    break;
                default:
                    _selection.Cancel();
                    RefreshSelectionOverlay();
                    break;
            }
        }

        private void OnSelectionHandleDrag(object sender, HandleDragEventArgs e)
        {
            if (e == null || !_selection.IsActive)
            {
                return;
            }
            int row;
            int col;
            if (!TryHitCell(new Point(e.X, e.Y), out row, out col))
            {
                return;
            }
            ISelectionGrid grid = CurrentGrid();
            if (grid == null)
            {
                return;
            }
            if (e.Handle == SelectionLayer.HandleStart)
            {
                _selection.MoveStart(row, col, grid);
            }
            else
            {
                _selection.MoveEnd(row, col, grid);
            }
            RefreshSelectionOverlay();
        }

        private void CopySelection()
        {
            ISelectionGrid grid = CurrentGrid();
            string text = _selection.ExtractText(grid);
            ClipboardService.SetText(text);
            Haptics.VibrateLight(true);
            if (CopiedToast != null)
            {
                CopiedToast.Show("已复制");
            }
        }

        private async void PasteClipboard()
        {
            string text = await ClipboardService.GetTextAsync();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            SendInput(System.Text.Encoding.UTF8.GetBytes(text.Replace("\r\n", "\r").Replace('\n', '\r')));
        }

        private void RefreshSelectionOverlay()
        {
            if (Selection == null)
            {
                return;
            }
            if (!_selection.IsActive || _screen == null || _renderer.CellWidth <= 0)
            {
                Selection.Hide();
                return;
            }
            Selection.Show(_selection.Normalized, _renderer.CellWidth, _renderer.CellHeight,
                TerminalPadding, _screen.Rows, _screen.Cols);
        }

        private ISelectionGrid CurrentGrid()
        {
            if (_screen == null || _cells == null || _cells.Length == 0)
            {
                return null;
            }
            return new BufferSelectionGrid(_cells, _screen.Rows, _screen.Cols);
        }

        private bool TryHitCell(Point point, out int row, out int col)
        {
            row = 0;
            col = 0;
            double cellWidth = _renderer.CellWidth;
            double cellHeight = _renderer.CellHeight;
            if (cellWidth <= 0 || cellHeight <= 0 || _screen == null)
            {
                return false;
            }
            col = (int)Math.Floor((point.X - TerminalPadding) / cellWidth);
            row = (int)Math.Floor((point.Y - TerminalPadding) / cellHeight);
            if (col < 0 || row < 0 || col >= _screen.Cols || row >= _screen.Rows)
            {
                return false;
            }
            return true;
        }

        private void OnHardwareInput(object sender, TerminalInputEventArgs e)
        {
            SendInput(e != null ? e.Data : null);
        }

        private void OnHardwareShortcut(object sender, ShortcutActionEventArgs e)
        {
            EventHandler<ShortcutActionEventArgs> handler = Shortcut;
            if (handler != null)
            {
                handler(this, e);
            }
        }

        private void OnHardwareActivity(object sender, EventArgs e)
        {
            _softKeyboard.HidePane();
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
