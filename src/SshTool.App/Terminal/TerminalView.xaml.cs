using System;
using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using SshTool.App.Controls;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.Core.Models;
using SshTool.Core.Sessions;
using SshTool.Core.Terminal;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.ApplicationModel.Resources;
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
        private readonly PointerInput _pointer = new PointerInput();
        private bool _draggingSelection;
        private int _pulledOffset = int.MinValue;
        private bool _ignoreNextTap;
        private CoreCursor _savedCursor;
        private ISshSession _session;
        private ISshSession _contentDirtySource;
        private ITerminalScreen _screen;
        private byte[] _cells = new byte[0];
        private byte[] _dirty = new byte[0];
        private long _lastRevision = -1;
        private bool _lastBlink = true;
        private bool _fullRedraw = true;
        private bool _registered;
        private bool _loaded;
        // U12：工作区不可见窗格（非活动标签/窄屏非聚焦叶）置 false：OnTick 直接返回，
        // 不拉取不绘制；FrameScheduler 视其为不可见，空闲 30 帧后退订。
        private bool _renderingActive = true;
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
            this.PointerEntered += OnPointerEntered;
            this.PointerExited += OnPointerExited;
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
            _pointer.SyncHost = SyncPointerHost;
            _pointer.Scroll.OffsetChanged += OnScrollChanged;
            _pointer.ScrollChanged += OnScrollChanged;
            _pointer.Input += OnPointerBytes;
            _pointer.FontSizeChanging += OnFontSizeChanging;
            _pointer.FontSizeCommitted += OnFontSizeCommitted;
            _pointer.SelectWord += OnMouseSelectWord;
            _pointer.SelectLine += OnMouseSelectLine;
            _pointer.SelectBegin += OnMouseSelectBegin;
            _pointer.SelectExtend += OnMouseSelectExtend;
            _pointer.ContextMenu += OnMouseContextMenu;
        }

        public event EventHandler<TerminalInputEventArgs> Input;

        public event EventHandler FontSizeCommitted;

        public event EventHandler<ShortcutActionEventArgs> Shortcut;

        // U13：右键菜单「发送片段」由宿主页面接管（页面持有 SessionInfo，才能填充
        // ${host}/${user}/${port}/${name} 内置变量；本视图只有 ISshSession）。
        public event EventHandler SnippetRequested;

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

        public bool PasteConfirmMultiline { get; set; } = true;

        public string AltScreenScroll
        {
            get { return _pointer.AltScreenScroll; }
            set { _pointer.AltScreenScroll = string.IsNullOrEmpty(value) ? "arrows" : value; }
        }

        public ScrollController Scroll
        {
            get { return _pointer.Scroll; }
        }

        public int CurrentFontSize
        {
            get { return (int)_renderer.FontSize; }
        }

        public void PasteFromClipboard()
        {
            PasteClipboard();
        }

        // U12：宽屏复制快捷键（Ctrl+Shift+C）入口；复用选择工具条同一份拷贝逻辑。
        public void CopySelectionToClipboard()
        {
            if (!DispatcherHelper.HasThreadAccess)
            {
                DispatcherHelper.Post(CopySelectionToClipboard);
                return;
            }
            if (!_selection.IsActive)
            {
                return;
            }
            CopySelection();
        }

        // U12：不可见窗格不绘制/退订 FrameScheduler（TerminalWorkspace 调用）。
        public void SetRenderingActive(bool active)
        {
            if (!DispatcherHelper.HasThreadAccess)
            {
                DispatcherHelper.Post(() => SetRenderingActive(active));
                return;
            }
            if (_renderingActive == active)
            {
                return;
            }
            _renderingActive = active;
            FrameScheduler.Instance.SetVisible(_schedulerId, active);
            if (active)
            {
                _fullRedraw = true;
                FrameScheduler.Instance.Wake();
            }
        }

        public bool FocusInput()
        {
            return _softKeyboard.Focus();
        }

        // C-03（§7.5）：SIP 当前是否弹出（InputPane 遮挡高度 > 0）。
        // 宿主在浮出层打开时采样，关闭后据以决定是否恢复哨兵焦点。
        public bool IsInputPaneVisible
        {
            get { return _softKeyboard.IsInputPaneVisible; }
        }

        // C-03（§7.5）：浮出层关闭后把焦点还给输入哨兵（SIP 随之重新弹出）。
        // 调用方须先确认浮出层打开前 SIP 是弹出的，否则会把已收起的软键盘反复拉起。
        // 物理键盘在场时不抢：硬件输入走 CoreWindow 与焦点无关，抢焦点反而唤起 SIP。
        public void RestoreInputFocus()
        {
            if (HardwareKeyboardInput.IsHardwareKeyboardPresent)
            {
                return;
            }
            if (!_softKeyboard.HasFocus)
            {
                FocusInput();
            }
        }

        // A03：把外观应用到本视图（调色板 + 字体度量 + 光标 + 选区色），不碰
        // 会话、不重连。字号/行高变化经 FontMetrics 重测并防抖 Resize（§7.4）。
        public void ApplyAppearance(AppearanceProfile profile)
        {
            if (profile == null)
            {
                return;
            }
            if (!DispatcherHelper.HasThreadAccess)
            {
                AppearanceProfile captured = profile;
                DispatcherHelper.Post(() => ApplyAppearance(captured));
                return;
            }
            try
            {
                _renderer.DefaultFgArgb = TerminalPalette.HexToArgb(profile.Foreground);
            }
            catch (Exception)
            {
            }
            try
            {
                _renderer.DefaultBgArgb = TerminalPalette.HexToArgb(profile.Background);
            }
            catch (Exception)
            {
            }
            try
            {
                _renderer.PaletteArgb = TerminalPalette.PaletteToArgb(profile.Palette);
            }
            catch (Exception)
            {
            }
            try
            {
                _renderer.CursorColor = ToColor(TerminalPalette.HexToArgb(profile.Cursor));
            }
            catch (Exception)
            {
            }
            _renderer.BoldAsBright = profile.BoldAsBright;
            _renderer.FontWeightBold = profile.FontWeightBold;
            _renderer.CursorStyle = profile.CursorStyle;
            _renderer.CursorBlink = profile.CursorBlink;
            _renderer.FontSize = ClampFontSize(profile.FontSize);
            _renderer.LineHeightFactor = ClampLineHeight((float)profile.LineHeight);
            if (profile.Padding >= 0 && profile.Padding <= 16)
            {
                TerminalPadding = profile.Padding;
            }
            if (Selection != null)
            {
                try
                {
                    Selection.HighlightBrush = new SolidColorBrush(ToColor(TerminalPalette.HexToArgb(profile.Selection)));
                }
                catch (Exception)
                {
                    Selection.HighlightBrush = null;
                }
            }
            _fullRedraw = true;
            if (Canvas != null)
            {
                Canvas.Invalidate();
            }
            FrameScheduler.Instance.Wake();
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
                // WRONG_THREAD：AttachNative 的 NativeSession 变更通知可能仍在后台线程
                // 到达（SessionInfo 已封送，但防御性兜底）；DispatcherTimer/Canvas 必须 UI 线程。
                if (!DispatcherHelper.HasThreadAccess)
                {
                    ISshSession captured = value;
                    DispatcherHelper.Post(() => { Session = captured; });
                    return;
                }
                if (ReferenceEquals(_session, value))
                {
                    return;
                }
                // O01：ContentDirty 是空闲后的重绘唤醒信号（01-DESIGN §4.2 时序
                // 「若本帧未通知：ContentDirty 事件 → FrameScheduler.Wake()」）。
                // 没有它，终端失焦后帧调度器约 500 ms 就退订（FrameSchedulerCore
                // 的 30 帧空闲阈值先于 530 ms 闪烁到期），新到的远端输出就再没有
                // 任何东西把它唤回来。订阅/退订与会话切换严格成对。
                UnsubscribeContentDirty();
                _session = value;
                if (_session != null)
                {
                    _session.ContentDirty += OnSessionContentDirty;
                    _contentDirtySource = _session;
                }
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
            _pointer.Attach(this);
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
            FrameScheduler.Instance.SetVisible(_schedulerId, _renderingActive);
            ScheduleResize();
            FrameScheduler.Instance.Wake();
        }

        // ISshSession 的生命周期长于本视图（SessionManager 持有），订阅必须解除。
        private void UnsubscribeContentDirty()
        {
            if (_contentDirtySource != null)
            {
                _contentDirtySource.ContentDirty -= OnSessionContentDirty;
                _contentDirtySource = null;
            }
        }

        // I/O 线程触发；Wake 内部经 DispatcherHelper.Post 封送，任意线程可调。
        private void OnSessionContentDirty(object sender, EventArgs e)
        {
            FrameScheduler.Instance.Wake();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _loaded = false;
            UnsubscribeContentDirty();
            _resizeTimer.Stop();
            _softKeyboard.Detach();
            _hardwareKeyboard.Detach();
            _pointer.Detach();
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
            if (!_renderingActive)
            {
                return false;
            }
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
                _pointer.Scroll.OnOutput(_screen.ScrollbackCount);
                pulled = Pull();
                _lastRevision = _screen.Revision;
            }
            else if (_pointer.Scroll.Offset != _pulledOffset)
            {
                pulled = Pull();
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

            int offset = _pointer.Scroll.Offset;
            _pointer.Scroll.SetScrollbackCount(_screen.ScrollbackCount);
            _renderer.SuppressCursor = offset > 0;
            bool changed;
            if (offset == 0)
            {
                changed = _screen.CopyDirtyRows(_cells, _dirty);
                if (!changed)
                {
                    _screen.CopyViewport(0, _cells);
                    _fullRedraw = true;
                    changed = true;
                }
            }
            else
            {
                _screen.CopyViewport(offset, _cells);
                _fullRedraw = true;
                changed = true;
            }
            _pulledOffset = offset;

            if (_renderer.CellWidth > 0)
            {
                Canvas.Width = cols * _renderer.CellWidth;
                Canvas.Height = rows * _renderer.CellHeight;
            }
            return changed;
        }

        private void OnTapped(object sender, TappedRoutedEventArgs e)
        {
            if (_ignoreNextTap)
            {
                _ignoreNextTap = false;
                return;
            }
            if (_screen != null && _screen.MouseMode != 0)
            {
                return;
            }
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
                CopiedToast.Show(ResourceLoader.GetForCurrentView().GetString("Terminal_CopiedToast"));
            }
        }

        private async void PasteClipboard()
        {
            string text = await ClipboardService.GetTextAsync();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            string normalized = PasteProcessor.NormalizeNewlines(text);
            if (PasteProcessor.NeedsMultilineConfirm(normalized, PasteConfirmMultiline))
            {
                try
                {
                    var confirm = await PasteConfirmDialog.ShowAsync(normalized);
                    if (confirm == null || !confirm.Confirmed)
                    {
                        return;
                    }
                    if (confirm.DontAskAgain)
                    {
                        PasteConfirmMultiline = false;
                    }
                }
                catch (Exception)
                {
                    return;
                }
            }
            bool bracketed = _screen != null && _screen.BracketedPaste;
            var chunks = PasteProcessor.ChunkUtf8(PasteProcessor.WrapBracketed(normalized, bracketed));
            for (int i = 0; i < chunks.Count; i++)
            {
                SendInput(chunks[i]);
            }
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
            _pointer.Scroll.OnUserInput();
            ISshSession session = _session;
            if (session != null)
            {
                session.Write(e.Data);
            }
        }

        private void SyncPointerHost()
        {
            _pointer.AltScreen = _screen != null && _screen.AltScreen;
            _pointer.MouseMode = _screen != null ? _screen.MouseMode : 0;
            _pointer.MouseSgr = _screen != null && _screen.MouseSgr;
            _pointer.ApplicationCursorKeys = _screen != null && _screen.AppCursorKeys;
            _pointer.CellHeight = _renderer.CellHeight;
            _pointer.CellWidth = _renderer.CellWidth;
            _pointer.Padding = TerminalPadding;
            _pointer.SelectionActive = _selection.IsActive;
            _pointer.FontSize = _renderer.FontSize;
            _pointer.Cols = _screen != null ? _screen.Cols : 0;
            _pointer.Rows = _screen != null ? _screen.Rows : 0;
        }

        private void OnScrollChanged(object sender, EventArgs e)
        {
            // ScrollController.OffsetChanged 可能被后台线程的 OnUserInput 间接触发；
            // Pull 会碰 Canvas.Width/Height，必须回 UI 线程。
            if (!DispatcherHelper.HasThreadAccess)
            {
                DispatcherHelper.Post(() => OnScrollChanged(sender, e));
                return;
            }
            if (_screen == null)
            {
                return;
            }
            Pull();
            FrameScheduler.Instance.Wake();
        }

        private void OnPointerBytes(object sender, TerminalInputEventArgs e)
        {
            SendInput(e != null ? e.Data : null);
        }

        private void OnFontSizeChanging(object sender, EventArgs e)
        {
            _renderer.FontSize = _pointer.FontSize;
            if (FontSizeBubble != null && FontSizeBubbleText != null)
            {
                FontSizeBubbleText.Text = ((int)_renderer.FontSize).ToString(CultureInfo.InvariantCulture);
                FontSizeBubble.Visibility = Visibility.Visible;
            }
        }

        private void OnFontSizeCommitted(object sender, EventArgs e)
        {
            if (FontSizeBubble != null)
            {
                FontSizeBubble.Visibility = Visibility.Collapsed;
            }
            EventHandler handler = FontSizeCommitted;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void OnMouseSelectWord(object sender, CellHitEventArgs e)
        {
            ISelectionGrid grid = CurrentGrid();
            if (grid == null || e == null)
            {
                return;
            }
            _ignoreNextTap = true;
            _selection.BeginWord(e.Row, e.Col, grid);
            _draggingSelection = true;
            RefreshSelectionOverlay();
        }

        private void OnMouseSelectLine(object sender, CellHitEventArgs e)
        {
            ISelectionGrid grid = CurrentGrid();
            if (grid == null || e == null)
            {
                return;
            }
            _ignoreNextTap = true;
            _selection.BeginLine(e.Row, grid);
            _draggingSelection = true;
            RefreshSelectionOverlay();
        }

        private void OnMouseSelectBegin(object sender, CellHitEventArgs e)
        {
            ISelectionGrid grid = CurrentGrid();
            if (grid == null || e == null)
            {
                return;
            }
            _ignoreNextTap = true;
            _selection.BeginCell(e.Row, e.Col, grid);
            _draggingSelection = true;
            RefreshSelectionOverlay();
        }

        private void OnMouseSelectExtend(object sender, CellHitEventArgs e)
        {
            ISelectionGrid grid = CurrentGrid();
            if (grid == null || e == null || !_selection.IsActive)
            {
                return;
            }
            _selection.Extend(e.Row, e.Col, grid);
            RefreshSelectionOverlay();
        }

        private void OnMouseContextMenu(object sender, Point point)
        {
            _ignoreNextTap = true;
            // C-06：可见文案走 resw 双语（代码构造，走 ResourceLoader）。
            var loader = ResourceLoader.GetForCurrentView();
            var flyout = new MenuFlyout();
            flyout.Items.Add(MenuItem(loader.GetString("Terminal_MenuCopy"), (s, a) => CopySelection()));
            flyout.Items.Add(MenuItem(loader.GetString("Terminal_MenuPaste"), (s, a) => PasteFromClipboard()));
            flyout.Items.Add(MenuItem(loader.GetString("Terminal_MenuSelectAll"), (s, a) =>
            {
                ISelectionGrid grid = CurrentGrid();
                if (grid != null)
                {
                    _selection.SelectAll(grid);
                    RefreshSelectionOverlay();
                }
            }));
            flyout.Items.Add(MenuItem(loader.GetString("Terminal_MenuClearScreen"), (s, a) => SendInput(new byte[] { 0x1B, (byte)'[', (byte)'H', 0x1B, (byte)'[', (byte)'2', (byte)'J' })));
            flyout.Items.Add(MenuItem(loader.GetString("Terminal_MenuSendSnippet"), (s, a) =>
            {
                EventHandler handler = SnippetRequested;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }));
            try
            {
                flyout.ShowAt(this, point);
            }
            catch (Exception)
            {
                flyout.ShowAt(this);
            }
        }

        private static MenuFlyoutItem MenuItem(string text, RoutedEventHandler handler)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += handler;
            return item;
        }

        private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                Window window = Window.Current;
                CoreWindow core = window != null ? window.CoreWindow : null;
                if (core == null)
                {
                    return;
                }
                _savedCursor = core.PointerCursor;
                core.PointerCursor = new CoreCursor(CoreCursorType.IBeam, 1);
            }
            catch (Exception)
            {
            }
        }

        private void OnPointerExited(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                Window window = Window.Current;
                CoreWindow core = window != null ? window.CoreWindow : null;
                if (core == null)
                {
                    return;
                }
                if (_savedCursor != null)
                {
                    core.PointerCursor = _savedCursor;
                }
            }
            catch (Exception)
            {
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
            if (!DispatcherHelper.HasThreadAccess)
            {
                DispatcherHelper.Post(() => OnMetricsInvalidated(sender, e));
                return;
            }
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
            if (!DispatcherHelper.HasThreadAccess)
            {
                DispatcherHelper.Post(ScheduleResize);
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

        private static float ClampFontSize(int size)
        {
            if (size < TerminalRenderer.MinimumFontSize)
            {
                return TerminalRenderer.MinimumFontSize;
            }
            if (size > TerminalRenderer.MaximumFontSize)
            {
                return TerminalRenderer.MaximumFontSize;
            }
            return size;
        }

        private static float ClampLineHeight(float factor)
        {
            if (float.IsNaN(factor) || float.IsInfinity(factor))
            {
                return 1.2f;
            }
            if (factor < TerminalRenderer.MinimumLineHeightFactor)
            {
                return TerminalRenderer.MinimumLineHeightFactor;
            }
            if (factor > TerminalRenderer.MaximumLineHeightFactor)
            {
                return TerminalRenderer.MaximumLineHeightFactor;
            }
            return factor;
        }
    }
}
