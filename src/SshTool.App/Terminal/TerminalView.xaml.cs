using System;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using SshTool.Core.Terminal;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace SshTool.App.Terminal
{
    // T05：把 ITerminalScreen 接到 Win2D。帧调度器按 revision 拉取脏行。
    public sealed partial class TerminalView : UserControl
    {
        private readonly TerminalRenderer _renderer = new TerminalRenderer();
        private readonly string _schedulerId = "tv-" + Guid.NewGuid().ToString("N");
        private ITerminalScreen _screen;
        private byte[] _cells = new byte[0];
        private byte[] _dirty = new byte[0];
        private long _lastRevision = -1;
        private bool _lastBlink = true;
        private bool _fullRedraw = true;
        private bool _registered;

        public TerminalView()
        {
            this.InitializeComponent();
            this.Loaded += OnLoaded;
            this.Unloaded += OnUnloaded;
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

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplyTokenColors();
            if (!_registered)
            {
                FrameScheduler.Instance.Register(_schedulerId, OnTick);
                _registered = true;
            }
            FrameScheduler.Instance.SetVisible(_schedulerId, true);
            FrameScheduler.Instance.Wake();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
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
            object size = Application.Current.Resources["FontCaption"];
            if (size is double)
            {
                _renderer.FontSize = (float)(double)size;
            }
            _renderer.InvalidateSurface();
            _renderer.EnsureMetrics(sender);
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

        private void ApplyTokenColors()
        {
            _renderer.DefaultFgArgb = BrushArgb("AppTextBrush", TerminalRenderer.FallbackFgArgb);
            _renderer.DefaultBgArgb = BrushArgb("AppBgBrush", TerminalRenderer.FallbackBgArgb);
            _renderer.CursorColor = ToColor(BrushArgb("AppAccentBrush", 0xFF4C8DFF));
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
    }
}
