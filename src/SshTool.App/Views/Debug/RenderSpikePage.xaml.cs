using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views.Debug
{
    /// <summary>
    /// SP04：Win2D 终端渲染帧率压测（01-DESIGN.md §7.3、D5、R3）。
    /// 三种负载 × 两种网格 × 单/双实例；帧由 CompositionTarget.Rendering 驱动，
    /// 统计合成帧率与每帧绘制耗时。真机数字回填 doc/ENV.md。
    /// </summary>
    public sealed partial class RenderSpikePage : Page
    {
        private const string FontFamilyUri = "ms-appx:///Assets/Fonts/JetBrainsMono-Regular.ttf#JetBrains Mono";
        private const float CellFontSize = 14f;
        private static readonly string[] CjkCandidates = { "Microsoft YaHei UI", "DengXian", "SimSun" };

        // 终端 16 色（xterm 默认近似值）——调试页，配色不走 Token
        private static readonly Color[] Ansi16 =
        {
            Color.FromArgb(255, 0x00, 0x00, 0x00), Color.FromArgb(255, 0xCD, 0x31, 0x31),
            Color.FromArgb(255, 0x0D, 0xBC, 0x79), Color.FromArgb(255, 0xE5, 0xE5, 0x10),
            Color.FromArgb(255, 0x24, 0x72, 0xC8), Color.FromArgb(255, 0xBC, 0x3F, 0xBC),
            Color.FromArgb(255, 0x11, 0xA8, 0xCD), Color.FromArgb(255, 0xE5, 0xE5, 0xE5),
            Color.FromArgb(255, 0x66, 0x66, 0x66), Color.FromArgb(255, 0xF1, 0x4C, 0x4C),
            Color.FromArgb(255, 0x23, 0xD1, 0x8B), Color.FromArgb(255, 0xF5, 0xF5, 0x43),
            Color.FromArgb(255, 0x3B, 0x8E, 0xEA), Color.FromArgb(255, 0xD6, 0x70, 0xD6),
            Color.FromArgb(255, 0x29, 0xB8, 0xDB), Color.FromArgb(255, 0xFF, 0xFF, 0xFF)
        };

        private enum LoadMode { FullRedraw = 0, DirtyRows = 1, Idle = 2 }

        private readonly Random _rnd = new Random(20260918);
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly GridPainter _painter1 = new GridPainter();
        private readonly GridPainter _painter2 = new GridPainter();

        private int _cols = 48;
        private int _rows = 30;
        private char[,] _chars;
        private byte[,] _fg;
        private byte[,] _bg;

        private CanvasTextFormat _format;
        private float _cellW;
        private float _cellH;
        private bool _metricsReady;

        private int _frames;
        private double _drawMsSum;
        private int _drawSamples;
        private double _lastReportMs;
        private string _fontReport = "字体：未检测";

        public RenderSpikePage()
        {
            this.InitializeComponent();
            BuildModel();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            // Win2D 要求页面离开时显式回收，否则 CanvasControl 持有设备资源不释放
            StopLoop();
            _painter1.Dispose();
            _painter2.Dispose();
            if (Canvas1 != null) { Canvas1.RemoveFromVisualTree(); }
            if (Canvas2 != null) { Canvas2.RemoveFromVisualTree(); }
            base.OnNavigatedFrom(e);
        }

        // ---------- 模型 ----------

        private void BuildModel()
        {
            _chars = new char[_rows, _cols];
            _fg = new byte[_rows, _cols];
            _bg = new byte[_rows, _cols];
            for (int r = 0; r < _rows; r++)
            {
                FillRow(r);
            }
        }

        private void FillRow(int r)
        {
            for (int c = 0; c < _cols; c++)
            {
                _chars[r, c] = (char)('!' + _rnd.Next(0, 94));   // 可见 ASCII
                _fg[r, c] = (byte)_rnd.Next(0, Ansi16.Length);
                _bg[r, c] = (byte)_rnd.Next(0, Ansi16.Length);
            }
        }

        private LoadMode Mode
        {
            get { return (LoadMode)Math.Max(0, LoadBox.SelectedIndex); }
        }

        // ---------- 帧循环 ----------

        private void OnRunToggled(object sender, RoutedEventArgs e)
        {
            if (RunSwitch.IsOn) { StartLoop(); } else { StopLoop(); }
        }

        private void StartLoop()
        {
            _frames = 0;
            _drawMsSum = 0;
            _drawSamples = 0;
            _lastReportMs = _clock.Elapsed.TotalMilliseconds;
            CompositionTarget.Rendering -= OnRendering;
            CompositionTarget.Rendering += OnRendering;
        }

        private void StopLoop()
        {
            CompositionTarget.Rendering -= OnRendering;
        }

        private void OnRendering(object sender, object e)
        {
            _frames++;
            if (Mode == LoadMode.DirtyRows)
            {
                for (int i = 0; i < 3; i++)
                {
                    int r = _rnd.Next(0, _rows);
                    FillRow(r);
                    _painter1.MarkDirty(r);
                    _painter2.MarkDirty(r);
                }
            }

            // 静止负载不请求重绘：测的是「什么都不画时合成链的开销」
            if (Mode != LoadMode.Idle)
            {
                Canvas1.Invalidate();
                if (Canvas2.Visibility == Visibility.Visible) { Canvas2.Invalidate(); }
            }
            Report();
        }

        private void Report()
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            double span = now - _lastReportMs;
            if (span < 500) { return; }

            double fps = _frames * 1000.0 / span;
            double avgDraw = _drawSamples > 0 ? _drawMsSum / _drawSamples : 0;
            StatsText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0}×{1} | {2} | {3} | FPS {4:F1} | 平均绘制 {5:F2} ms | 格 {6:F1}×{7:F1} px\n{8}",
                _cols, _rows,
                ((ComboBoxItem)LoadBox.SelectedItem).Content,
                DualSwitch.IsOn ? "双实例" : "单实例",
                fps, avgDraw, _cellW, _cellH, _fontReport);

            _frames = 0;
            _drawMsSum = 0;
            _drawSamples = 0;
            _lastReportMs = now;
        }

        // ---------- 绘制 ----------

        private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
        {
            _format = new CanvasTextFormat
            {
                FontFamily = FontFamilyUri,
                FontSize = CellFontSize,
                WordWrapping = CanvasWordWrapping.NoWrap
            };
            using (var layout = new CanvasTextLayout(sender, "M", _format, 0, 0))
            {
                _cellW = (float)Math.Ceiling(layout.LayoutBounds.Width);
                _cellH = (float)Math.Ceiling(layout.LayoutBounds.Height);
            }
            _metricsReady = _cellW > 0 && _cellH > 0;
            _fontReport = DescribeFonts();
        }

        private void OnDraw1(CanvasControl sender, CanvasDrawEventArgs args)
        {
            DrawOne(sender, args, _painter1);
        }

        private void OnDraw2(CanvasControl sender, CanvasDrawEventArgs args)
        {
            DrawOne(sender, args, _painter2);
        }

        private void DrawOne(CanvasControl sender, CanvasDrawEventArgs args, GridPainter painter)
        {
            if (!_metricsReady) { return; }
            var sw = Stopwatch.StartNew();
            if (Mode == LoadMode.DirtyRows)
            {
                painter.DrawCached(sender, args.DrawingSession, this);
            }
            else
            {
                DrawGridDirect(args.DrawingSession, 0f, 0f);
            }
            sw.Stop();
            _drawMsSum += sw.Elapsed.TotalMilliseconds;
            _drawSamples++;
        }

        // 全屏直绘：逐格填背景 + 画字符（最坏情况，不做行合并）
        private void DrawGridDirect(CanvasDrawingSession ds, float originX, float originY)
        {
            for (int r = 0; r < _rows; r++)
            {
                DrawRow(ds, r, originX, originY + r * _cellH);
            }
        }

        internal void DrawRow(CanvasDrawingSession ds, int r, float x0, float y0)
        {
            for (int c = 0; c < _cols; c++)
            {
                float x = x0 + c * _cellW;
                ds.FillRectangle(x, y0, _cellW, _cellH, Ansi16[_bg[r, c]]);
                ds.DrawText(_chars[r, c].ToString(), x, y0, Ansi16[_fg[r, c]], _format);
            }
        }

        internal int RowCount { get { return _rows; } }
        internal float CellWidth { get { return _cellW; } }
        internal float CellHeight { get { return _cellH; } }
        internal int ColCount { get { return _cols; } }

        private string DescribeFonts()
        {
            var sb = new StringBuilder();
            sb.Append("字体：JetBrains Mono（包内 ttf）");
            try
            {
                var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var set = CanvasFontSet.GetSystemFontSet();
                foreach (var face in set.Fonts)
                {
                    foreach (var kv in face.FamilyNames)
                    {
                        families.Add(kv.Value);
                    }
                }
                string hit = null;
                foreach (var candidate in CjkCandidates)
                {
                    if (families.Contains(candidate)) { hit = candidate; break; }
                }
                sb.Append(" | 中文回退：").Append(hit ?? "候选均不可用（" + string.Join("/", CjkCandidates) + "）");
            }
            catch (Exception ex)
            {
                sb.Append(" | 中文回退：枚举失败 ").Append(ex.GetType().Name);
            }
            return sb.ToString();
        }

        // ---------- 配置切换 ----------

        // XAML 上三个控件的事件签名不同，统一汇到 ApplyConfig
        private void OnGridOrLoadChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyConfig();
        }

        private void OnDualToggled(object sender, RoutedEventArgs e)
        {
            ApplyConfig();
        }

        private void ApplyConfig()
        {
            if (GridBox == null || LoadBox == null || DualSwitch == null) { return; }

            int cols = GridBox.SelectedIndex == 1 ? 88 : 48;
            int rows = GridBox.SelectedIndex == 1 ? 24 : 30;
            if (cols != _cols || rows != _rows)
            {
                _cols = cols;
                _rows = rows;
                BuildModel();
                _painter1.Invalidate();
                _painter2.Invalidate();
            }
            Canvas2.Visibility = DualSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
            if (Canvas1 != null) { Canvas1.Invalidate(); }
            if (Canvas2 != null && Canvas2.Visibility == Visibility.Visible) { Canvas2.Invalidate(); }
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack) { Frame.GoBack(); }
        }

        /// <summary>
        /// 脏行模式的行缓存：每行一张 CanvasRenderTarget，只有被标脏的行重画，
        /// 每帧把所有行 DrawImage 贴回来（01-DESIGN §7.3 的局部重绘思路）。
        /// </summary>
        private sealed class GridPainter : IDisposable
        {
            private CanvasRenderTarget[] _rows;
            private bool[] _dirty;

            public void MarkDirty(int row)
            {
                if (_dirty != null && row >= 0 && row < _dirty.Length) { _dirty[row] = true; }
            }

            public void Invalidate()
            {
                Dispose();
            }

            public void DrawCached(CanvasControl sender, CanvasDrawingSession ds, RenderSpikePage page)
            {
                EnsureRows(sender, page);
                for (int r = 0; r < _rows.Length; r++)
                {
                    if (_dirty[r])
                    {
                        using (var rowDs = _rows[r].CreateDrawingSession())
                        {
                            rowDs.Clear(Colors.Black);
                            page.DrawRow(rowDs, r, 0f, 0f);
                        }
                        _dirty[r] = false;
                    }
                    ds.DrawImage(_rows[r], 0f, r * page.CellHeight);
                }
            }

            private void EnsureRows(CanvasControl sender, RenderSpikePage page)
            {
                if (_rows != null && _rows.Length == page.RowCount) { return; }
                Dispose();
                _rows = new CanvasRenderTarget[page.RowCount];
                _dirty = new bool[page.RowCount];
                float w = page.ColCount * page.CellWidth;
                for (int r = 0; r < _rows.Length; r++)
                {
                    _rows[r] = new CanvasRenderTarget(sender, w, page.CellHeight);
                    _dirty[r] = true;
                }
            }

            public void Dispose()
            {
                if (_rows == null) { return; }
                foreach (var rt in _rows)
                {
                    if (rt != null) { rt.Dispose(); }
                }
                _rows = null;
                _dirty = null;
            }
        }
    }
}
