using System;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using SshTool.App.Infrastructure;
using SshTool.Core.Models;
using SshTool.Core.Terminal;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace SshTool.App.Terminal
{
    // T06：ITerminalScreen → Win2D。CreateResources / DeviceLost 重建行缓存；
    // 失焦空心光标；属性管线在 TerminalRenderer。
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
        private CanvasDevice _watchedDevice;

        public TerminalView()
        {
            this.InitializeComponent();
            this.Loaded += OnLoaded;
            this.Unloaded += OnUnloaded;
            this.GotFocus += OnGotFocus;
            this.LostFocus += OnLostFocus;
            this.Tapped += OnTapped;
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
            object size = Application.Current.Resources["FontCaption"];
            if (size is double)
            {
                _renderer.FontSize = (float)(double)size;
            }
            _renderer.NotifyDeviceLost();
            _renderer.EnsureMetrics(sender);
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
            this.Focus(FocusState.Pointer);
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
    }
}
