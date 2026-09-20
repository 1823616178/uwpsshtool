using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.UI.Xaml;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.App.Terminal;
using SshTool.App.ViewModels;
using SshTool.Core.Hosts;
using SshTool.Core.Terminal;
using Windows.Foundation.Metadata;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using NativeBridge = SshTool.Native.Bridge;

namespace SshTool.App.Views.Debug
{
    /// <summary>
    /// Q01：性能基准页（01-DESIGN.md §15）。所有场景本地化、不依赖真服务器：
    /// 预生成字节流经 Bridge.TerminalScreen.FeedBytes 直喂 native vterm
    /// （与真会话同一条 解析→网格→回滚 管线），TerminalView 照常渲染；
    /// FPS 复用 SP04 双口径（tick/s + 实绘 draw/s）叠加在终端上；
    /// 内存读 MemoryManager.AppMemoryUsage（ApiInformation 守卫，15063 缺失时报不可用）。
    /// 结果整份经 DebugReport 落盘 LocalState\spike-reports\perf-*.txt（phone-portal 自取）。
    /// §15 中无法自动化的指标（冷启动/连接/按键回显/Argon2）见 doc/PERF-REPORT.md 占位。
    /// </summary>
    public sealed partial class PerfPage : Page
    {
        // -------- 场景参数（场景定义来自 04-TASKS Q01 / 01-DESIGN §15） --------
        private const int Base64RawBytes = 1 << 20;   // head -c 1048576 /dev/urandom
        private const int Base64WrapCols = 76;        // GNU base64 默认列宽
        private const int YesLines = 200000;          // yes | head -n 200000
        private const int VimPages = 600;             // vim 大文件翻页的整页重绘次数
        private const int Base64ChunkBytes = 4096;
        private const int YesChunkBytes = 2048;
        private const int VimChunkBytes = 2048;
        private const int FeedIntervalMs = 16;        // 每块间隔 ≈ 一帧，按帧节奏持续喂入
        private const int TrailingMs = 1000;          // 喂完后再观察一秒，收尾帧
        private const int IdleWindowMs = 2500;
        private const int ScrollSessions = 4;         // ⑤ 4 会话
        private const int ScrollbackLines = 5000;     // 回滚环形缓冲默认容量
        private const int ScrollLineMargin = 200;     // 多喂一些，确保推满 5000
        private const int ScrollBurstBytes = 65536;   // ⑤ 不经渲染，大块喂
        private const int SettleMs = 500;
        private const int HostScrollMs = 8000;
        private const double HostScrollStepPx = 12;
        private const int TestHostCount = 100;
        private const int FallbackCols = 80;
        private const int FallbackRows = 24;
        private const int FpsWindowMs = 500;          // 叠加读数刷新周期（SP04 同款）
        private const int MaxSampledIntervalMs = 1000; // 更大的间隔视为页面中断，不进样本
        private const double Mb = 1048576.0;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Random _rnd = new Random(20260920);

        // 叠加读数（500 ms 窗口，随时在跑）
        private double _liveLastMs;
        private int _liveTicks;
        private int _liveDraws;
        private double _liveIntervalSum;
        private int _liveIntervalCount;
        private double _lastRenderMs = -1;

        // 场景窗口（ResetWindow → CaptureWindow）
        private double _winStartMs;
        private int _winTicks;
        private int _winDraws;
        private readonly List<double> _winIntervals = new List<double>();
        private double _feedMsSum;
        private int _feedSamples;

        // 终端直喂
        private NativeBridge.TerminalScreen _nativeScreen;
        private NativeTerminalScreen _managedScreen;
        private string _gridText = FallbackCols + "x" + FallbackRows;

        // ④ 列表滚动
        private HostListViewModel _hostsVm;
        private ScrollViewer _scrollViewer;
        private double _scrollOffset;
        private int _scrollDir = 1;
        private bool _scrollActive;
        private double _scrollStartMs;
        private int _scrollViews;
        private TaskCompletionSource<bool> _scrollDone;

        private StringBuilder _lastAllReport;
        private bool _running;
        private int _generation;

        public PerfPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _liveLastMs = _clock.Elapsed.TotalMilliseconds;
            _winStartMs = _liveLastMs;
            CompositionTarget.Rendering += OnRendering;
            HookTermCanvas();
            if (AppServices.Current != null)
            {
                _hostsVm = new HostListViewModel(AppServices.Current);
                HostPane.Attach(_hostsVm);
            }
        }

        // draw/s 口径需要数 TerminalView 内部 CanvasControl 的实绘次数；
        // 生成字段不可跨类访问（CS0122），走可视树拿到实例再订阅。
        private CanvasControl _termCanvas;

        private void HookTermCanvas()
        {
            if (_termCanvas != null)
            {
                return;
            }
            _termCanvas = FindDescendant<CanvasControl>(Term);
            if (_termCanvas != null)
            {
                _termCanvas.Draw += OnTermDraw;
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            // 先作废所有在跑的循环（UI 线程上不会与 FeedBytes 交错），再回收资源
            _generation++;
            _scrollActive = false;
            _scrollViewer = null;
            CompositionTarget.Rendering -= OnRendering;
            Term.Screen = null;
            DisposeScreen();
            DisposeScrollScreens();
            base.OnNavigatedFrom(e);
        }

        // ------------------------------------------------------------ FPS/帧耗时叠加

        private void OnRendering(object sender, object e)
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            if (_lastRenderMs >= 0)
            {
                double gap = now - _lastRenderMs;
                if (gap < MaxSampledIntervalMs)
                {
                    _liveIntervalSum += gap;
                    _liveIntervalCount++;
                    _winIntervals.Add(gap);
                }
            }
            _lastRenderMs = now;
            _liveTicks++;
            _winTicks++;
            if (_scrollActive)
            {
                DriveScroll(now);
            }
            LiveReport(now);
        }

        private void OnTermDraw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            _liveDraws++;
            _winDraws++;
        }

        private void LiveReport(double now)
        {
            if (now - _liveLastMs < FpsWindowMs)
            {
                return;
            }
            double span = Math.Max(1, now - _liveLastMs);
            double tick = _liveTicks * 1000.0 / span;
            double draw = _liveDraws * 1000.0 / span;
            double avgMs = _liveIntervalCount > 0 ? _liveIntervalSum / _liveIntervalCount : 0;
            double feedMs = _feedSamples > 0 ? _feedMsSum / _feedSamples : 0;
            string memText;
            ulong mem;
            memText = TryMemory(out mem, out _) ? FormatMb(mem) + "MB" : "不可用";
            string text = string.Format(
                CultureInfo.InvariantCulture,
                "tick {0:F1}/s | 实绘 {1:F1}/s\n帧 {2:F1} ms | 喂 {3:F2} ms/帧\n内存 {4}",
                tick, draw, avgMs, feedMs, memText);
            StatsText.Text = _gridText + " | " + text.Replace("\n", " | ");
            OverlayText.Text = text;
            _liveTicks = 0;
            _liveDraws = 0;
            _liveIntervalSum = 0;
            _liveIntervalCount = 0;
            _liveLastMs = now;
        }

        private void ResetWindow()
        {
            _winStartMs = _clock.Elapsed.TotalMilliseconds;
            _winTicks = 0;
            _winDraws = 0;
            _winIntervals.Clear();
            _feedMsSum = 0;
            _feedSamples = 0;
        }

        private WindowSnapshot CaptureWindow()
        {
            double span = _clock.Elapsed.TotalMilliseconds - _winStartMs;
            if (span <= 0) { span = 1; }
            var snap = new WindowSnapshot();
            snap.Seconds = span / 1000.0;
            snap.TicksPerSec = _winTicks * 1000.0 / span;
            snap.DrawsPerSec = _winDraws * 1000.0 / span;
            if (_winIntervals.Count > 0)
            {
                double sum = 0;
                double max = 0;
                for (int i = 0; i < _winIntervals.Count; i++)
                {
                    sum += _winIntervals[i];
                    if (_winIntervals[i] > max) { max = _winIntervals[i]; }
                }
                snap.AvgIntervalMs = sum / _winIntervals.Count;
                snap.MaxIntervalMs = max;
                snap.IntervalSamples = _winIntervals.Count;
            }
            snap.FeedAvgMs = _feedSamples > 0 ? _feedMsSum / _feedSamples : 0;
            snap.FeedSamples = _feedSamples;
            return snap;
        }

        private sealed class WindowSnapshot
        {
            public double Seconds;
            public double TicksPerSec;
            public double DrawsPerSec;
            public double AvgIntervalMs;
            public double MaxIntervalMs;
            public int IntervalSamples;
            public double FeedAvgMs;
            public int FeedSamples;
        }

        // ------------------------------------------------------------ 内存（ApiInformation 守卫）

        private static bool TryMemory(out ulong bytes, out string level)
        {
            bytes = 0;
            level = "n/a";
            // Windows.System.MemoryManager 高于 15063 契约的成员一律守卫（硬性约束）
            if (!ApiInformation.IsPropertyPresent("Windows.System.MemoryManager", "AppMemoryUsage"))
            {
                return false;
            }
            bytes = MemoryManager.AppMemoryUsage;
            level = ApiInformation.IsPropertyPresent("Windows.System.MemoryManager", "AppMemoryUsageLevel")
                ? MemoryManager.AppMemoryUsageLevel.ToString()
                : "n/a";
            return true;
        }

        private static string FormatMb(ulong bytes)
        {
            return (bytes / Mb).ToString("F1", CultureInfo.InvariantCulture);
        }

        private static string MemorySpan(ulong before, ulong after)
        {
            return FormatMb(before) + "→" + FormatMb(after) + "MB";
        }

        // ------------------------------------------------------------ 报告组装

        private StringBuilder NewReport(string title)
        {
            var sb = new StringBuilder();
            sb.AppendLine(DebugReport.EnvironmentHeader());
            sb.AppendLine(title);
            sb.AppendLine("口径：tick/s=CompositionTarget.Rendering 回调频率（本页叠加自身订阅，恒有底噪）；" +
                "draw/s=终端 Canvas 实绘次数（静止应为 0）；帧 avg/max=相邻 Rendering 回调间隔（≥1s 视为中断不采样）；" +
                "喂=每块字节流灌入耗时（含 native vterm 解析，与渲染并行）；内存=MemoryManager.AppMemoryUsage。");
            DescribeGrid();
            sb.AppendLine("网格=" + _gridText);
            ulong mem;
            string level;
            if (TryMemory(out mem, out level))
            {
                sb.AppendLine("基线内存=" + FormatMb(mem) + "MB（level=" + level + "）");
            }
            else
            {
                sb.AppendLine("基线内存=MemoryManager 不可用（低于契约，内存行将缺失）");
            }
            sb.AppendLine();
            return sb;
        }

        private void AppendWindowRow(StringBuilder sb, string tag, WindowSnapshot snap, string extra, ulong memBefore)
        {
            var line = new StringBuilder();
            line.Append(tag);
            if (snap != null)
            {
                line.AppendFormat(CultureInfo.InvariantCulture,
                    " dur={0:F1}s tick={1:F1}/s draw={2:F1}/s 帧avg={3:F1}ms 帧max={4:F1}ms",
                    snap.Seconds, snap.TicksPerSec, snap.DrawsPerSec, snap.AvgIntervalMs, snap.MaxIntervalMs);
                if (snap.FeedSamples > 0)
                {
                    line.AppendFormat(CultureInfo.InvariantCulture, " 喂={0:F2}ms/块({1}块)", snap.FeedAvgMs, snap.FeedSamples);
                }
            }
            if (!string.IsNullOrEmpty(extra))
            {
                line.Append(' ').Append(extra);
            }
            if (memBefore > 0)
            {
                ulong mem;
                string level;
                if (TryMemory(out mem, out level))
                {
                    line.Append(" 内存").Append(MemorySpan(memBefore, mem)).Append("（" + level + "）");
                }
            }
            sb.AppendLine(line.ToString());
        }

        private void ShowReport(string report)
        {
            _lastAllReport = new StringBuilder(report);
            ReportBox.Text = report;
            ReportBox.Visibility = Visibility.Visible;
            CopyButton.IsEnabled = true;
            ViewButton.IsEnabled = true;
        }

        // ------------------------------------------------------------ 终端直喂基建

        private void EnsureTerminal()
        {
            if (_nativeScreen == null)
            {
                _nativeScreen = new NativeBridge.TerminalScreen();
                _managedScreen = new NativeTerminalScreen(_nativeScreen);
                Term.Screen = _managedScreen;
            }
            SyncGrid();
            FrameScheduler.Instance.Wake();
        }

        private void SyncGrid()
        {
            if (_nativeScreen == null)
            {
                return;
            }
            int cols = Term.GridSize.Cols;
            int rows = Term.GridSize.Rows;
            if (cols <= 0) { cols = FallbackCols; }
            if (rows <= 0) { rows = FallbackRows; }
            _gridText = cols + "x" + rows;
            if (_nativeScreen.Cols != cols || _nativeScreen.Rows != rows)
            {
                _nativeScreen.ResizeGrid(cols, rows);
                FrameScheduler.Instance.Wake();
            }
        }

        private void DisposeScreen()
        {
            if (_managedScreen != null)
            {
                Term.Screen = null;
                _managedScreen = null;
            }
            if (_nativeScreen != null)
            {
                ((IDisposable)_nativeScreen).Dispose();
                _nativeScreen = null;
            }
        }

        private void DisposeScrollScreens()
        {
            if (_scrollScreens == null)
            {
                return;
            }
            for (int i = 0; i < _scrollScreens.Count; i++)
            {
                ((IDisposable)_scrollScreens[i]).Dispose();
            }
            _scrollScreens.Clear();
            _scrollScreens = null;
        }

        private List<NativeBridge.TerminalScreen> _scrollScreens;

        // ------------------------------------------------------------ 场景生成器（预生成字节流）

        private byte[] BuildBase64Stream()
        {
            // head -c 1048576 /dev/urandom | base64：1 MiB 随机字节 → base64，76 列折行
            var raw = new byte[Base64RawBytes];
            lock (_rnd)
            {
                _rnd.NextBytes(raw);
            }
            string b64 = Convert.ToBase64String(raw);
            var sb = new StringBuilder(b64.Length + (b64.Length / Base64WrapCols + 2) * 2);
            for (int i = 0; i < b64.Length; i += Base64WrapCols)
            {
                int len = Math.Min(Base64WrapCols, b64.Length - i);
                sb.Append(b64, i, len);
                sb.Append("\r\n");
            }
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        private static byte[] BuildYesStream()
        {
            // yes | head -n 200000：每行「y\n」，最简单的整屏滚动负载
            var bytes = new byte[YesLines * 2];
            for (int i = 0; i < YesLines; i++)
            {
                bytes[2 * i] = (byte)'y';
                bytes[2 * i + 1] = (byte)'\n';
            }
            return bytes;
        }

        private static byte[] BuildVimStream(int cols, int rows)
        {
            // vim 大文件翻页的等价字节流：进 alt screen，每页逐行光标定位 + 整行重绘
            //（行数取当前网格行数），页页全屏脏，末尾回主屏。内容行不越界自动换行。
            int width = Math.Max(20, cols - 1);
            var sb = new StringBuilder(16 * 1024);
            sb.Append("\u001B[?1049h");
            for (int p = 0; p < VimPages; p++)
            {
                for (int r = 0; r < rows; r++)
                {
                    sb.Append("\u001B[").Append(r + 1).Append(";1H");
                    string head = "vim page " + p.ToString("D4", CultureInfo.InvariantCulture)
                        + " row " + r.ToString("D2", CultureInfo.InvariantCulture) + " ";
                    if (head.Length > width)
                    {
                        head = head.Substring(0, width);
                    }
                    sb.Append(head);
                    for (int c = head.Length; c < width; c++)
                    {
                        sb.Append(c % 7 == 6 ? ' ' : '.');
                    }
                }
            }
            sb.Append("\u001B[?1049l");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        private static byte[] BuildScrollbackStream(int sessionIndex, int cols)
        {
            // ⑤：每会话 5000+ 行普通文本行，把 5000 行环形回滚推满
            int width = Math.Max(20, Math.Min(cols - 1, 72));
            var sb = new StringBuilder(64 * 1024);
            var line = new StringBuilder(width + 2);
            int total = ScrollbackLines + ScrollLineMargin;
            for (int i = 1; i <= total; i++)
            {
                line.Length = 0;
                line.Append('s').Append(sessionIndex)
                    .Append(" L").Append(i.ToString("D5", CultureInfo.InvariantCulture))
                    .Append(" |");
                while (line.Length < width)
                {
                    line.Append('-');
                }
                line.Append("\r\n");
                sb.Append(line);
            }
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        /// <summary>按帧节奏把整份字节流喂进 screen；逐块计喂入耗时；页面离开即中止。</summary>
        private async Task<bool> FeedPacedAsync(int gen, NativeBridge.TerminalScreen screen,
            byte[] stream, int chunk, StringBuilder sb, string tag)
        {
            long total = stream.Length;
            int chunks = (int)((total + chunk - 1) / chunk);
            long pos = 0;
            var sw = Stopwatch.StartNew();
            while (pos < total)
            {
                if (_generation != gen)
                {
                    sb.AppendLine(tag + "：页面已离开，中止");
                    return false;
                }
                int len = (int)Math.Min(chunk, total - pos);
                var slice = new byte[len];
                Buffer.BlockCopy(stream, (int)pos, slice, 0, len);
                var one = Stopwatch.StartNew();
                screen.FeedBytes(slice);
                one.Stop();
                _feedMsSum += one.Elapsed.TotalMilliseconds;
                _feedSamples++;
                pos += len;
                FrameScheduler.Instance.Wake();
                await Task.Delay(FeedIntervalMs).ConfigureAwait(true);
            }
            sw.Stop();
            sb.AppendLine(tag + "：喂入完成 " + total + " B / " + chunks + " 块 / " +
                sw.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + " s（含块间间隔）");
            return true;
        }

        // ------------------------------------------------------------ 各场景

        private async Task RunIdleAsync(StringBuilder sb)
        {
            EnsureTerminal();
            // 先给一次修订触发一帧实绘，随后完全静止：验证 §15「静止终端无帧回调订阅」
            _nativeScreen.FeedBytes(Encoding.UTF8.GetBytes("idle-probe\r\n"));
            FrameScheduler.Instance.Wake();
            await Task.Delay(TrailingMs).ConfigureAwait(true);
            ResetWindow();
            await Task.Delay(IdleWindowMs).ConfigureAwait(true);
            WindowSnapshot snap = CaptureWindow();
            bool running = FrameScheduler.Instance.Core.IsRunning;
            AppendWindowRow(sb, "⓪ 静止", snap,
                "FrameScheduler.IsRunning=" + running + "（应为 False：空闲退订）", 0);
        }

        private async Task RunBase64Async(StringBuilder sb)
        {
            EnsureTerminal();
            int gen = _generation;
            byte[] stream = BuildBase64Stream();
            ulong before = 0;
            TryMemory(out before, out _);
            ResetWindow();
            if (!await FeedPacedAsync(gen, _nativeScreen, stream, Base64ChunkBytes, sb, "① base64").ConfigureAwait(true))
            {
                return;
            }
            stream = null;
            await Task.Delay(TrailingMs).ConfigureAwait(true);
            WindowSnapshot snap = CaptureWindow();
            AppendWindowRow(sb, "① base64-1MiB", snap,
                "回滚=" + _nativeScreen.ScrollbackCount + "行（环形上限 5000）", before);
        }

        private async Task RunYesAsync(StringBuilder sb)
        {
            EnsureTerminal();
            int gen = _generation;
            byte[] stream = BuildYesStream();
            ulong before = 0;
            TryMemory(out before, out _);
            ResetWindow();
            if (!await FeedPacedAsync(gen, _nativeScreen, stream, YesChunkBytes, sb, "② yes").ConfigureAwait(true))
            {
                return;
            }
            stream = null;
            await Task.Delay(TrailingMs).ConfigureAwait(true);
            WindowSnapshot snap = CaptureWindow();
            AppendWindowRow(sb, "② yes-20万行", snap, "回滚=" + _nativeScreen.ScrollbackCount + "行", before);
        }

        private async Task RunVimAsync(StringBuilder sb)
        {
            EnsureTerminal();
            int gen = _generation;
            GridSize grid = CurrentGridSize();
            byte[] stream = BuildVimStream(grid.Cols, grid.Rows);
            ulong before = 0;
            TryMemory(out before, out _);
            ResetWindow();
            if (!await FeedPacedAsync(gen, _nativeScreen, stream, VimChunkBytes, sb, "③ vim").ConfigureAwait(true))
            {
                return;
            }
            stream = null;
            await Task.Delay(TrailingMs).ConfigureAwait(true);
            WindowSnapshot snap = CaptureWindow();
            AppendWindowRow(sb, "③ vim-" + VimPages + "页", snap,
                "网格=" + grid.Cols + "x" + grid.Rows + " 回滚=" + _nativeScreen.ScrollbackCount + "行（alt 屏不入回滚）", before);
        }

        private async Task RunScrollbackAsync(StringBuilder sb)
        {
            // ⑤ 不渲染：4 块原生屏幕各喂 5000+ 行，量托管内存增量（§15：4 会话 + 各 5000 行 < 250MB）
            GridSize grid = CurrentGridSize();
            ulong before = 0;
            TryMemory(out before, out _);
            DisposeScrollScreens();
            _scrollScreens = new List<NativeBridge.TerminalScreen>(ScrollSessions);
            int gen = _generation;
            byte[][] streams = new byte[ScrollSessions][];
            for (int i = 0; i < ScrollSessions; i++)
            {
                _scrollScreens.Add(new NativeBridge.TerminalScreen());
                _scrollScreens[i].ResizeGrid(grid.Cols, grid.Rows);
                streams[i] = BuildScrollbackStream(i + 1, grid.Cols);
            }
            long[] pos = new long[ScrollSessions];
            bool remaining = true;
            while (remaining)
            {
                if (_generation != gen)
                {
                    sb.AppendLine("⑤ 回滚：页面已离开，中止");
                    DisposeScrollScreens();
                    return;
                }
                remaining = false;
                for (int i = 0; i < ScrollSessions; i++)
                {
                    if (pos[i] >= streams[i].Length)
                    {
                        continue;
                    }
                    int len = (int)Math.Min(ScrollBurstBytes, streams[i].Length - pos[i]);
                    var slice = new byte[len];
                    Buffer.BlockCopy(streams[i], (int)pos[i], slice, 0, len);
                    _scrollScreens[i].FeedBytes(slice);
                    pos[i] += len;
                    if (pos[i] < streams[i].Length)
                    {
                        remaining = true;
                    }
                }
                await Task.Yield();
            }
            streams = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(SettleMs).ConfigureAwait(true);
            var counts = new StringBuilder();
            for (int i = 0; i < ScrollSessions; i++)
            {
                if (i > 0)
                {
                    counts.Append('/');
                }
                counts.Append(_scrollScreens[i].ScrollbackCount);
            }
            ulong after;
            string level;
            if (TryMemory(out after, out level))
            {
                double deltaMb = (after - before) / Mb;
                AppendWindowRow(sb, "⑤ 4×5000回滚", null,
                    "会话=" + ScrollSessions + " 网格=" + grid.Cols + "x" + grid.Rows +
                    " 回滚=" + counts + " 内存" + MemorySpan(before, after) +
                    " Δ=" + deltaMb.ToString("F1", CultureInfo.InvariantCulture) + "MB（§15 <250MB）", 0);
            }
            else
            {
                AppendWindowRow(sb, "⑤ 4×5000回滚", null,
                    "会话=" + ScrollSessions + " 回滚=" + counts + " 内存读数不可用", 0);
            }
            DisposeScrollScreens();
        }

        private async Task RunHostListAsync(StringBuilder sb)
        {
            SwitchPanel(false);
            await Task.Delay(400).ConfigureAwait(true);   // 等布局展开
            int rows = CountHostRows();
            ScrollViewer scroll = FindScrollViewer(ListHost);
            sb.AppendLine("④ 主机行数=" + rows + " 内层 ScrollViewer=" + (scroll != null ? "已找到" : "未找到"));
            if (scroll == null)
            {
                sb.AppendLine("④ 列表滚动：找不到 ScrollViewer，中止");
                SwitchPanel(true);
                return;
            }
            if (rows < TestHostCount)
            {
                sb.AppendLine("④ 主机不足 " + TestHostCount + " 台（可点「生成100台」），按现状滚动");
            }
            ResetWindow();
            _scrollViewer = scroll;
            _scrollOffset = 0;
            _scrollDir = 1;
            _scrollViews = 0;
            _scrollStartMs = _clock.Elapsed.TotalMilliseconds;
            _scrollDone = new TaskCompletionSource<bool>();
            _scrollActive = true;
            await _scrollDone.Task.ConfigureAwait(true);
            WindowSnapshot snap = CaptureWindow();
            AppendWindowRow(sb, "④ 主机列表", snap,
                "滚动=" + _scrollViews + " 次 ChangeView / " + _scrollViews * HostScrollStepPx + " px", 0);
            await Task.Delay(TrailingMs).ConfigureAwait(true);
            SwitchPanel(true);
        }

        private GridSize CurrentGridSize()
        {
            int cols = Term.GridSize.Cols;
            int rows = Term.GridSize.Rows;
            if (cols <= 0) { cols = FallbackCols; }
            if (rows <= 0) { rows = FallbackRows; }
            return new GridSize(cols, rows);
        }

        private void DescribeGrid()
        {
            int cols = Term.GridSize.Cols;
            int rows = Term.GridSize.Rows;
            if (cols > 0 && rows > 0)
            {
                _gridText = cols + "x" + rows;
            }
        }

        // ------------------------------------------------------------ ④ 滚动驱动（OnRendering 里推进）

        private void DriveScroll(double nowMs)
        {
            ScrollViewer scroll = _scrollViewer;
            if (scroll == null)
            {
                StopScroll();
                return;
            }
            if (nowMs - _scrollStartMs >= HostScrollMs)
            {
                StopScroll();
                return;
            }
            double max = Math.Max(0, scroll.ExtentHeight - scroll.ViewportHeight);
            double next = _scrollOffset + _scrollDir * HostScrollStepPx;
            if (next >= max)
            {
                next = max;
                _scrollDir = -1;
            }
            else if (next <= 0)
            {
                next = 0;
                _scrollDir = 1;
            }
            _scrollOffset = next;
            scroll.ChangeView(null, next, null, true);
            _scrollViews++;
        }

        private void StopScroll()
        {
            _scrollActive = false;
            _scrollViewer = null;
            TaskCompletionSource<bool> done = _scrollDone;
            if (done != null)
            {
                done.TrySetResult(true);
            }
        }

        private void SwitchPanel(bool terminal)
        {
            TermHost.Visibility = terminal ? Visibility.Visible : Visibility.Collapsed;
            ListHost.Visibility = terminal ? Visibility.Collapsed : Visibility.Visible;
            if (!terminal)
            {
                OverlayText.Text = "④ 列表滚动中…";
            }
        }

        private static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            return FindDescendant<ScrollViewer>(root);
        }

        private static T FindDescendant<T>(DependencyObject root) where T : class
        {
            T direct = root as T;
            if (direct != null)
            {
                return direct;
            }
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                T found = FindDescendant<T>(VisualTreeHelper.GetChild(root, i));
                if (found != null)
                {
                    return found;
                }
            }
            return null;
        }

        private int CountHostRows()
        {
            if (_hostsVm == null)
            {
                return 0;
            }
            int total = 0;
            foreach (HostListGroup group in _hostsVm.Groups)
            {
                IReadOnlyList<HostListRow> rows = group.Rows;
                if (rows != null)
                {
                    total += rows.Count;
                }
            }
            return total;
        }

        // ------------------------------------------------------------ 入口 / 收尾

        private void SetButtonsEnabled(bool enabled)
        {
            RunAllButton.IsEnabled = enabled;
            IdleButton.IsEnabled = enabled;
            Base64Button.IsEnabled = enabled;
            YesButton.IsEnabled = enabled;
            VimButton.IsEnabled = enabled;
            HostListButton.IsEnabled = enabled;
            ScrollbackButton.IsEnabled = enabled;
            GenerateHostsButton.IsEnabled = enabled;
        }

        private async Task RunStepAsync(StringBuilder sb, string title, Func<StringBuilder, Task> body)
        {
            sb.AppendLine("== " + title + " ==");
            try
            {
                await body(sb).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                sb.AppendLine("失败：" + ex.GetType().Name + " " + ex.Message);
            }
        }

        private async void OnRunAllClick(object sender, RoutedEventArgs e)
        {
            if (_running)
            {
                return;
            }
            _running = true;
            SetButtonsEnabled(false);
            try
            {
                int gen = _generation;
                StringBuilder sb = NewReport("Q01 全场景（⓪静止 → ①base64 → ②yes → ③vim → ⑤回滚 → ④列表）");
                _lastAllReport = sb;
                await RunStepAsync(sb, "⓪ 静止读数", RunIdleAsync);
                await Task.Delay(SettleMs);
                if (_generation != gen) { return; }
                await RunStepAsync(sb, "① base64 1MB", RunBase64Async);
                await Task.Delay(SettleMs);
                if (_generation != gen) { return; }
                await RunStepAsync(sb, "② yes 20万行", RunYesAsync);
                await Task.Delay(SettleMs);
                if (_generation != gen) { return; }
                await RunStepAsync(sb, "③ vim 600页", RunVimAsync);
                await Task.Delay(SettleMs);
                if (_generation != gen) { return; }
                await RunStepAsync(sb, "⑤ 4会话×5000行回滚内存", RunScrollbackAsync);
                await Task.Delay(SettleMs);
                if (_generation != gen) { return; }
                await RunStepAsync(sb, "④ 100 主机列表滚动", RunHostListAsync);
                string report = sb.ToString();
                ShowReport(report);
                StatsText.Text = await DebugReport.PublishAsync("perf-q01", "Q01", report);
            }
            catch (Exception ex)
            {
                StatsText.Text = "全场景中断：" + ex.GetType().Name + " " + ex.Message;
            }
            finally
            {
                _running = false;
                SetButtonsEnabled(true);
                SwitchPanel(true);
            }
        }

        private async void RunSingle(string title, string slug, Func<StringBuilder, Task> body)
        {
            if (_running)
            {
                return;
            }
            _running = true;
            SetButtonsEnabled(false);
            try
            {
                StringBuilder sb = NewReport("Q01 单场景：" + title);
                await RunStepAsync(sb, title, body);
                string report = sb.ToString();
                ShowReport(report);
                StatsText.Text = await DebugReport.PublishAsync("perf-q01-" + slug, "Q01", report);
            }
            catch (Exception ex)
            {
                StatsText.Text = title + " 中断：" + ex.GetType().Name + " " + ex.Message;
            }
            finally
            {
                _running = false;
                SetButtonsEnabled(true);
            }
        }

        private void OnIdleClick(object sender, RoutedEventArgs e)
        {
            RunSingle("⓪ 静止读数", "s0-idle", RunIdleAsync);
        }

        private void OnBase64Click(object sender, RoutedEventArgs e)
        {
            RunSingle("① base64 1MB", "s1-base64", RunBase64Async);
        }

        private void OnYesClick(object sender, RoutedEventArgs e)
        {
            RunSingle("② yes 20万行", "s2-yes", RunYesAsync);
        }

        private void OnVimClick(object sender, RoutedEventArgs e)
        {
            RunSingle("③ vim 600页", "s3-vim", RunVimAsync);
        }

        private void OnScrollbackClick(object sender, RoutedEventArgs e)
        {
            RunSingle("⑤ 4会话×5000行回滚内存", "s5-scrollback", RunScrollbackAsync);
        }

        private void OnHostListClick(object sender, RoutedEventArgs e)
        {
            RunSingle("④ 100 主机列表滚动", "s4-hostlist", RunHostListAsync);
        }

        private void OnGenerateHostsClick(object sender, RoutedEventArgs e)
        {
            // 复用 HostListViewModel 既有调试命令（同 MainPage「生成 100 台测试主机」）；
            // 写的是真实主机库，生成后手动删除「测试主机」分组即可。
            if (_hostsVm != null && _hostsVm.GenerateTestHostsCommand.CanExecute(null))
            {
                _hostsVm.GenerateTestHostsCommand.Execute(null);
                StatsText.Text = "已发起生成 100 台测试主机，主机列表刷新后可跑 ④（可先跑全场景）";
            }
            else
            {
                StatsText.Text = "生成命令不可用（AppServices 未就绪？）";
            }
        }

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            if (_lastAllReport == null)
            {
                return;
            }
            string report = _lastAllReport.ToString();
            StatsText.Text = DebugReport.CopyToClipboard(report) ? "已复制到剪贴板" : "复制失败";
        }

        private void OnToggleReportClick(object sender, RoutedEventArgs e)
        {
            ReportBox.Visibility = ReportBox.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }
    }
}
