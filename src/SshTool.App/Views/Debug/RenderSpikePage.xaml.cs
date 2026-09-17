using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
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

        private enum LoadMode { FullRedraw = 0, DirtyRows = 1, Idle = 2, FullRedrawMerged = 3 }

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
        private bool _realisticContent = true;
        private bool _matrixRunning;
        private string _lastReport;

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

        // 两种内容分布：
        //   随机每格 —— 每格独立随机 16 色，平均 run 长度≈1，是 run 合并的最坏情况；
        //   真实分布 —— 约 85% 的格用默认前景/背景，其余成段着色，贴近真实终端输出。
        // 初版只有前者，导致 run-merged 与 per-cell 几乎同样慢（没东西可合并），
        // 量不出 §7.3 的收益（2026-09-18 改正）。
        private const byte DefaultFg = 7;
        private const byte DefaultBg = 0;

        private void FillRow(int r)
        {
            if (!_realisticContent)
            {
                for (int c = 0; c < _cols; c++)
                {
                    _chars[r, c] = (char)('!' + _rnd.Next(0, 94));
                    _fg[r, c] = (byte)_rnd.Next(0, Ansi16.Length);
                    _bg[r, c] = (byte)_rnd.Next(0, Ansi16.Length);
                }
                return;
            }

            int col = 0;
            while (col < _cols)
            {
                bool plain = _rnd.Next(100) < 85;
                int len = plain ? _rnd.Next(8, 41) : _rnd.Next(3, 13);
                byte fg = plain ? DefaultFg : (byte)_rnd.Next(1, Ansi16.Length);
                byte bg = plain ? DefaultBg : (byte)(_rnd.Next(100) < 25 ? _rnd.Next(1, Ansi16.Length) : DefaultBg);
                int end = Math.Min(_cols, col + len);
                for (; col < end; col++)
                {
                    // 真实终端里空格占比不低
                    _chars[r, col] = _rnd.Next(100) < 18 ? ' ' : (char)('!' + _rnd.Next(0, 94));
                    _fg[r, col] = fg;
                    _bg[r, col] = bg;
                }
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
            if (_matrixRunning) { return; }   // 矩阵跑的时候计数归测量窗口所有
            double now = _clock.Elapsed.TotalMilliseconds;
            double span = now - _lastReportMs;
            if (span < 500) { return; }

            // tick/s 是合成帧回调频率；CanvasControl 跟不上时会合并 Invalidate，
            // 所以必须同时报「实绘/s」（Draw 真正被调用的次数），否则读数偏乐观。
            double tickPerSec = _frames * 1000.0 / span;
            double drawPerSec = _drawSamples * 1000.0 / span;
            double avgDraw = _drawSamples > 0 ? _drawMsSum / _drawSamples : 0;
            StatsText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0}×{1} | {2} | {3}\ntick {4:F1}/s | 实绘 {5:F1}/s | 平均绘制 {6:F2} ms | 格 {7:F1}×{8:F1} px\n{9}",
                _cols, _rows,
                ((ComboBoxItem)LoadBox.SelectedItem).Content,
                DualSwitch.IsOn ? "双实例" : "单实例",
                tickPerSec, drawPerSec, avgDraw, _cellW, _cellH, _fontReport);

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
                painter.DrawCached(sender, args.DrawingSession, this, true);   // 脏行按 §7.3：单张 RT + run 合并
            }
            else if (Mode == LoadMode.FullRedrawMerged)
            {
                for (int r = 0; r < _rows; r++)
                {
                    DrawRowMerged(args.DrawingSession, r, 0f, r * _cellH);
                }
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

        // §7.3 规定的画法：一行内把连续同属性的格并成 run，背景一次 FillRectangle、
        // 文本一次 DrawText。逐格画是最坏情况，这里量的才是真实渲染器的成本。
        internal void DrawRowMerged(CanvasDrawingSession ds, int r, float x0, float y0)
        {
            int c = 0;
            while (c < _cols)                       // 背景 run
            {
                int start = c;
                byte bg = _bg[r, c];
                while (c < _cols && _bg[r, c] == bg) { c++; }
                ds.FillRectangle(x0 + start * _cellW, y0, (c - start) * _cellW, _cellH, Ansi16[bg]);
            }

            c = 0;
            var run = new StringBuilder(_cols);
            while (c < _cols)                       // 文本 run
            {
                int start = c;
                byte fg = _fg[r, c];
                run.Length = 0;
                while (c < _cols && _fg[r, c] == fg)
                {
                    run.Append(_chars[r, c]);
                    c++;
                }
                ds.DrawText(run.ToString(), x0 + start * _cellW, y0, Ansi16[fg], _format);
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

        private void OnContentToggled(object sender, RoutedEventArgs e)
        {
            if (ContentSwitch == null || _matrixRunning) { return; }
            _realisticContent = ContentSwitch.IsOn;
            BuildModel();
            _painter1.Invalidate();
            _painter2.Invalidate();
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


        // ---------- 一键全矩阵 + 导出 ----------

        private const int WarmupMs = 900;
        private const int MeasureMs = 2000;
        private static readonly string[] LoadTags = { "per-cell", "dirty3", "idle", "run-merged" };

        private async void OnRunMatrixClick(object sender, RoutedEventArgs e)
        {
            if (_matrixRunning) { return; }
            _matrixRunning = true;
            MatrixButton.IsEnabled = false;
            ReportBox.Visibility = Visibility.Collapsed;
            try
            {
                await RunMatrixAsync();
            }
            catch (Exception ex)
            {
                StatsText.Text = "矩阵中断：" + ex.Message;
            }
            finally
            {
                _matrixRunning = false;
                MatrixButton.IsEnabled = true;
            }
        }

        private async Task RunMatrixAsync()
        {
            if (!RunSwitch.IsOn) { RunSwitch.IsOn = true; }

            var sb = new StringBuilder();
            sb.AppendLine(DebugReport.EnvironmentHeader());
            sb.AppendLine("SP04 Win2D 渲染压测 | 预热 " + WarmupMs + " ms，测量 " + MeasureMs + " ms");
            sb.AppendLine("load: per-cell=全屏逐格 run-merged=全屏行内run合并 dirty3=每帧3行脏行(单张RT+run合并，§7.3 画法) idle=静止");
            sb.AppendLine("content: real=约85%默认色+成段着色（贴近真实终端） rand=每格独立随机16色（run 合并最坏情况）");
            sb.AppendLine("tick/s=CompositionTarget.Rendering 回调频率；draw/s=Draw 实际被调用次数（双实例时是两个画布之和）");
            sb.AppendLine();
            sb.AppendLine("load        content grid   inst   tick/s   draw/s   avg-draw-ms");

            int[] grids = { 0, 1 };
            int[] drawLoads = { 0, 3, 1 };           // 逐格 → run 合并 → 脏行
            bool[] duals = { false, true };
            bool[] contents = { true, false };       // 真实分布 → 随机每格

            int total = grids.Length * (drawLoads.Length * duals.Length * contents.Length + duals.Length);
            int done = 0;

            foreach (int g in grids)
            {
                foreach (bool real in contents)
                {
                    foreach (int l in drawLoads)
                    {
                        foreach (bool dual in duals)
                        {
                            done++;
                            await MeasureAsync(sb, g, l, dual, real, done, total);
                        }
                    }
                }
                foreach (bool dual in duals)         // idle 与内容无关，只跑一遍
                {
                    done++;
                    await MeasureAsync(sb, g, 2, dual, true, done, total);
                }
            }

            sb.AppendLine();
            sb.AppendLine("cell: " + _cellW.ToString("F1", CultureInfo.InvariantCulture)
                + "x" + _cellH.ToString("F1", CultureInfo.InvariantCulture) + " px");
            sb.AppendLine(_fontReport);

            _lastReport = sb.ToString();
            ReportBox.Text = _lastReport;
            ReportBox.Visibility = Visibility.Visible;
            CopyButton.IsEnabled = true;
            ViewButton.IsEnabled = true;
            RunSwitch.IsOn = false;

            StatsText.Text = await DebugReport.PublishAsync("sp04-render", "SP04", _lastReport);
        }

        private async Task MeasureAsync(StringBuilder sb, int grid, int load, bool dual, bool real, int done, int total)
        {
            if (_realisticContent != real)
            {
                _realisticContent = real;
                BuildModel();
                _painter1.Invalidate();
                _painter2.Invalidate();
            }
            GridBox.SelectedIndex = grid;
            LoadBox.SelectedIndex = load;
            DualSwitch.IsOn = dual;

            StatsText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "矩阵 {0}/{1}：{2} {3} {4}×{5} {6}（预热中…）",
                done, total, LoadTags[load], real ? "real" : "rand",
                grid == 1 ? 88 : 48, grid == 1 ? 24 : 30,
                dual ? "双实例" : "单实例");

            await Task.Delay(WarmupMs);
            ResetCounters();
            await Task.Delay(MeasureMs);
            sb.AppendLine(SnapshotRow(LoadTags[load], real, dual));
        }

        private void ResetCounters()
        {
            _frames = 0;
            _drawMsSum = 0;
            _drawSamples = 0;
            _lastReportMs = _clock.Elapsed.TotalMilliseconds;
        }

        private string SnapshotRow(string loadTag, bool real, bool dual)
        {
            double span = _clock.Elapsed.TotalMilliseconds - _lastReportMs;
            if (span <= 0) { span = 1; }
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0,-11} {1,-7} {2,-6} {3,-6} {4,7:F1} {5,8:F1} {6,10:F2}",
                loadTag,
                real ? "real" : "rand",
                _cols + "x" + _rows,
                dual ? "dual" : "single",
                _frames * 1000.0 / span,
                _drawSamples * 1000.0 / span,
                _drawSamples > 0 ? _drawMsSum / _drawSamples : 0);
        }

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastReport)) { return; }
            StatsText.Text = DebugReport.CopyToClipboard(_lastReport) ? "已复制到剪贴板" : "复制失败";
        }

        private void OnToggleReportClick(object sender, RoutedEventArgs e)
        {
            ReportBox.Visibility = ReportBox.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack) { Frame.GoBack(); }
        }

        /// <summary>
        /// 脏行模式的行缓存：每行一张 CanvasRenderTarget，只有被标脏的行重画，
        /// 每帧把所有行 DrawImage 贴回来（01-DESIGN §7.3 的局部重绘思路）。
        /// </summary>
        /// <summary>
        /// §7.3 的画法：整个视图**一张** CanvasRenderTarget（cols×cellW, rows×cellH），
        /// 只把脏行重画进去，然后一次 DrawImage 整张贴出。
        /// （初版误写成每行一张 RT + 每帧 N 次 DrawImage，34 ms 里大头是那些贴图，
        ///   与设计不符也测不出真实成本，2026-09-18 改正。）
        /// </summary>
        private sealed class GridPainter : IDisposable
        {
            private CanvasRenderTarget _surface;
            private bool[] _dirty;
            private int _cols;
            private int _rows;

            public void MarkDirty(int row)
            {
                if (_dirty != null && row >= 0 && row < _dirty.Length) { _dirty[row] = true; }
            }

            public void Invalidate()
            {
                Dispose();
            }

            public void DrawCached(CanvasControl sender, CanvasDrawingSession ds, RenderSpikePage page, bool merged)
            {
                EnsureSurface(sender, page);

                bool any = false;
                for (int r = 0; r < _dirty.Length; r++)
                {
                    if (_dirty[r]) { any = true; break; }
                }
                if (any)
                {
                    using (var rt = _surface.CreateDrawingSession())
                    {
                        for (int r = 0; r < _dirty.Length; r++)
                        {
                            if (!_dirty[r]) { continue; }
                            float y = r * page.CellHeight;
                            // 只清这一行的矩形，其余像素保留（这正是行缓存的意义）
                            rt.FillRectangle(0f, y, _cols * page.CellWidth, page.CellHeight, Colors.Black);
                            if (merged) { page.DrawRowMerged(rt, r, 0f, y); }
                            else { page.DrawRow(rt, r, 0f, y); }
                            _dirty[r] = false;
                        }
                    }
                }
                ds.DrawImage(_surface, 0f, 0f);
            }

            private void EnsureSurface(CanvasControl sender, RenderSpikePage page)
            {
                if (_surface != null && _cols == page.ColCount && _rows == page.RowCount) { return; }
                Dispose();
                _cols = page.ColCount;
                _rows = page.RowCount;
                _surface = new CanvasRenderTarget(sender, _cols * page.CellWidth, _rows * page.CellHeight);
                _dirty = new bool[_rows];
                for (int r = 0; r < _rows; r++) { _dirty[r] = true; }
            }

            public void Dispose()
            {
                if (_surface != null) { _surface.Dispose(); _surface = null; }
                _dirty = null;
                _cols = 0;
                _rows = 0;
            }
        }
    }
}
