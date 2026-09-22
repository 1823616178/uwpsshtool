using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using SshTool.Core.Models;
using SshTool.Core.Terminal;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;

namespace SshTool.App.Terminal
{
    // T06/T07：行缓存、属性管线与光标；字体度量委托给 FontMetrics（§7.3–7.4）。
    // SP04：绝不逐格 FillRectangle+DrawText；宽字符单独 2 格居中。
    public sealed class TerminalRenderer : IDisposable
    {
        public const string FontFamilyUri = "ms-appx:///Assets/Fonts/JetBrainsMono-Regular.ttf#JetBrains Mono";
        public const string BoldFontFamilyUri = "ms-appx:///Assets/Fonts/JetBrainsMono-Bold.ttf#JetBrains Mono";
        public const uint FallbackFgArgb = 0xFFE5E5E5u;
        public const uint FallbackBgArgb = 0xFF000000u;

        // 终端内部几何（非 Design Token）：与鸿蒙端 TerminalDrawCommands / TerminalCanvas 同系数。
        private const float DecoThicknessFraction = 0.06f;
        private const float CursorBarFraction = 0.15f;
        private const float CursorUnderlineFraction = 0.12f;
        private const float CursorMinPx = 1.5f;
        private const float FakeBoldOffset = 1f;
        public const float MinimumFontSize = 8f;
        public const float MaximumFontSize = 28f;
        public const float MinimumLineHeightFactor = 1f;
        public const float MaximumLineHeightFactor = 1.6f;

        private CanvasRenderTarget _surface;
        private readonly FontMetrics _fontMetrics = new FontMetrics();
        private CanvasTextFormat _regular;
        private CanvasTextFormat _italic;
        private CanvasTextFormat _bold;
        private CanvasTextFormat _boldItalic;
        // O11：居中对齐的另一套（宽字符段）。对齐是 CanvasTextFormat 的属性，
        // 所以不能只备一套。随字号变化一起重建（EnsureFormats）。
        private CanvasTextFormat _regularCentered;
        private CanvasTextFormat _italicCentered;
        private CanvasTextFormat _boldCentered;
        private CanvasTextFormat _boldItalicCentered;
        // O11：逐行绘制复用的 run 表（UI 线程独占）。
        private readonly List<CellRun> _runs = new List<CellRun>();
        private int _cols;
        private int _rows;
        private float _dpi;
        private bool _formatsDirty = true;

        public event EventHandler MetricsInvalidated;

        public float CellWidth { get; private set; }
        public float CellHeight { get; private set; }
        public uint DefaultFgArgb { get; set; }
        public uint DefaultBgArgb { get; set; }
        public uint[] PaletteArgb { get; set; }
        public bool BoldAsBright { get; set; }
        public CursorStyle CursorStyle { get; set; }
        public bool CursorBlink { get; set; }
        public Color CursorColor { get; set; }
        public bool Focused { get; set; }

        public bool SuppressCursor { get; set; }

        private float _fontSize = 12f;
        private float _lineHeightFactor = 1.2f;
        private bool _fontWeightBold;

        public float FontSize
        {
            get { return _fontSize; }
            set
            {
                float next = Clamp(value, MinimumFontSize, MaximumFontSize, 12f);
                if (_fontSize != next)
                {
                    _fontSize = next;
                    _formatsDirty = true;
                    _fontMetrics.Invalidate();
                    OnMetricsInvalidated();
                }
            }
        }

        public float LineHeightFactor
        {
            get { return _lineHeightFactor; }
            set
            {
                float next = Clamp(value, MinimumLineHeightFactor, MaximumLineHeightFactor, 1.2f);
                if (_lineHeightFactor != next)
                {
                    _lineHeightFactor = next;
                    _fontMetrics.Invalidate();
                    OnMetricsInvalidated();
                }
            }
        }

        public bool FontWeightBold
        {
            get { return _fontWeightBold; }
            set
            {
                if (_fontWeightBold != value)
                {
                    _fontWeightBold = value;
                    _formatsDirty = true;
                }
            }
        }

        public TerminalRenderer()
        {
            DefaultFgArgb = FallbackFgArgb;
            DefaultBgArgb = FallbackBgArgb;
            PaletteArgb = TerminalPalette.CreateXtermPalette();
            BoldAsBright = true;
            CursorStyle = SshTool.Core.Models.CursorStyle.Block;
            CursorBlink = true;
            CursorColor = Color.FromArgb(255, 0xE8, 0xEE, 0xFB);
            Focused = false;
        }

        public bool EnsureMetrics(CanvasControl canvas)
        {
            if (canvas == null)
            {
                return false;
            }
            EnsureFormats();
            long revision = _fontMetrics.Revision;
            if (!_fontMetrics.Ensure(canvas, _regular, FontSize, LineHeightFactor))
            {
                return false;
            }
            CellWidth = _fontMetrics.CellWidth;
            CellHeight = _fontMetrics.CellHeight;
            if (_fontMetrics.Revision != revision)
            {
                InvalidateSurface();
            }
            return CellWidth > 0 && CellHeight > 0;
        }

        public void InvalidateSurface()
        {
            if (_surface != null)
            {
                _surface.Dispose();
                _surface = null;
            }
            _cols = 0;
            _rows = 0;
            _dpi = 0;
        }

        public void NotifyDeviceLost()
        {
            InvalidateSurface();
            _fontMetrics.Invalidate();
        }

        public void InvalidateFormats()
        {
            _formatsDirty = true;
            _fontMetrics.Invalidate();
            OnMetricsInvalidated();
        }

        public void Paint(CanvasControl canvas, CanvasDrawingSession ds, ITerminalScreen screen,
                          byte[] cells, byte[] dirty, bool fullRedraw, bool blinkOn)
        {
            if (canvas == null || ds == null || screen == null || cells == null)
            {
                return;
            }
            if (!EnsureMetrics(canvas))
            {
                return;
            }
            EnsureSurface(canvas, screen.Cols, screen.Rows);
            if (_surface == null)
            {
                return;
            }

            bool any = fullRedraw;
            if (!any && dirty != null)
            {
                for (int r = 0; r < _rows; r++)
                {
                    if (IsDirty(dirty, r))
                    {
                        any = true;
                        break;
                    }
                }
            }
            if (any)
            {
                using (CanvasDrawingSession rt = _surface.CreateDrawingSession())
                {
                    for (int r = 0; r < _rows; r++)
                    {
                        if (!fullRedraw && dirty != null && !IsDirty(dirty, r))
                        {
                            continue;
                        }
                        PaintRow(rt, cells, r, screen.Cols);
                    }
                }
            }

            ds.DrawImage(_surface, 0, 0);
            if (!SuppressCursor)
            {
                DrawCursor(ds, screen, cells, blinkOn);
            }
        }

        public void Dispose()
        {
            InvalidateSurface();
            DisposeFormats();
        }

        private void EnsureSurface(CanvasControl canvas, int cols, int rows)
        {
            if (cols <= 0 || rows <= 0)
            {
                return;
            }
            float dpi = canvas.Dpi;
            if (_surface != null && _cols == cols && _rows == rows && _dpi == dpi)
            {
                return;
            }
            InvalidateSurface();
            _cols = cols;
            _rows = rows;
            _dpi = dpi;
            _surface = new CanvasRenderTarget(canvas, cols * CellWidth, rows * CellHeight);
        }

        private void PaintRow(CanvasDrawingSession ds, byte[] cells, int row, int cols)
        {
            float y = row * CellHeight;
            ds.FillRectangle(0, y, cols * CellWidth, CellHeight, ToColor(DefaultBgArgb));
            RowRunBuilder.Build(cells, row, cols, _runs); // O11：复用表，不每行新建
            List<CellRun> runs = _runs;
            for (int i = 0; i < runs.Count; i++)
            {
                CellRun run = runs[i];
                ResolvedCellStyle style = TerminalPalette.Resolve(
                    run, DefaultFgArgb, DefaultBgArgb, PaletteArgb, BoldAsBright);
                float x = run.StartCol * CellWidth;
                float width = run.Length * CellWidth;
                ds.FillRectangle(x, y, width, CellHeight, ToColor(style.BgArgb));
                if (string.IsNullOrEmpty(run.Text) || style.Invisible)
                {
                    continue;
                }
                bool wide = (run.Attrs & TerminalCell.AttrWide) != 0;
                DrawGlyphRun(ds, run.Text, x, y, width, style, wide);
            }
        }

        private void DrawGlyphRun(CanvasDrawingSession ds, string text, float x, float y,
                                  float width, ResolvedCellStyle style, bool wide)
        {
            // O11：原来每段都 new CanvasTextLayout —— 每行每段一个 Win2D/D2D COM
            // 对象，48×30、每行约 5 段、30 draw/s 就是每秒数千次 COM 创建销毁，
            // 而 SP04 实测整屏重绘已经 20.6 ms/帧（预算 33 ms），余量不多。
            // 对齐方式挪进预建的 CanvasTextFormat（左对齐/居中各一套），改用
            // DrawText(text, rect, color, format)，视觉结果不变。
            CanvasTextFormat format = GetFormat(style.Bold, style.Italic, wide);
            Color fg = ToColor(style.FgArgb);
            ds.DrawText(text, new Rect(x, y, width, CellHeight), fg, format);
            if (style.Bold && !FontWeightBold)
            {
                // 无粗体字重时用 1px 水平偏移描边加粗（与原实现一致）
                ds.DrawText(text, new Rect(x + FakeBoldOffset, y, width, CellHeight), fg, format);
            }

            float thickness = Math.Max(1f, FontSize * DecoThicknessFraction);
            if (style.Underline)
            {
                ds.FillRectangle(x, y + CellHeight - thickness, width, thickness, fg);
            }
            if (style.Strike)
            {
                ds.FillRectangle(x, y + (CellHeight - thickness) / 2f, width, thickness, fg);
            }
        }

        private void DrawCursor(CanvasDrawingSession ds, ITerminalScreen screen, byte[] cells, bool blinkOn)
        {
            if (!screen.CursorVisible)
            {
                return;
            }
            int col = screen.CursorCol;
            int row = screen.CursorRow;
            if (col < 0 || row < 0 || col >= _cols || row >= _rows)
            {
                return;
            }

            int span = 1;
            TerminalCell cell = default(TerminalCell);
            bool haveCell = TryReadCell(cells, row, col, _cols, out cell);
            if (haveCell && cell.IsWide)
            {
                span = (col + 1 < _cols) ? 2 : 1;
            }

            float x = col * CellWidth;
            float y = row * CellHeight;
            float width = span * CellWidth;

            if (!Focused)
            {
                ds.DrawRectangle(x, y, width, CellHeight, CursorColor, 1f);
                return;
            }
            if (CursorBlink && !blinkOn)
            {
                return;
            }

            if (CursorStyle == SshTool.Core.Models.CursorStyle.Bar)
            {
                float w = Math.Max(CursorMinPx, CellWidth * CursorBarFraction);
                ds.FillRectangle(x, y, w, CellHeight, CursorColor);
                return;
            }
            if (CursorStyle == SshTool.Core.Models.CursorStyle.Underline)
            {
                float h = Math.Max(CursorMinPx, CellHeight * CursorUnderlineFraction);
                ds.FillRectangle(x, y + CellHeight - h, width, h, CursorColor);
                return;
            }

            // block：反色语义（鸿蒙 generateCursorCommands）
            uint fg = DefaultFgArgb;
            uint bg = DefaultBgArgb;
            string glyph = string.Empty;
            bool bold = false;
            bool italic = false;
            if (haveCell)
            {
                ResolvedCellStyle style = TerminalPalette.Resolve(
                    cell.FgArgb, cell.BgArgb, cell.Attrs,
                    DefaultFgArgb, DefaultBgArgb, PaletteArgb, BoldAsBright);
                fg = style.FgArgb;
                bg = style.BgArgb;
                bold = style.Bold;
                italic = style.Italic;
                if (!cell.IsEmpty && !cell.IsWideContinuation && !style.Invisible)
                {
                    glyph = cell.Glyph();
                }
            }
            ds.FillRectangle(x, y, width, CellHeight, ToColor(fg));
            if (!string.IsNullOrEmpty(glyph))
            {
                var overlay = new ResolvedCellStyle
                {
                    FgArgb = bg,
                    BgArgb = fg,
                    Bold = bold,
                    Italic = italic
                };
                DrawGlyphRun(ds, glyph, x, y, width, overlay, span > 1);
            }
        }

        // wide=true 的段要在两格宽里居中（宽字符），其余左对齐。对齐是
        // CanvasTextFormat 的属性，所以两种对齐各备一套。
        private CanvasTextFormat GetFormat(bool bold, bool italic, bool centered)
        {
            EnsureFormats();
            if (bold && FontWeightBold)
            {
                if (centered)
                {
                    return italic ? _boldItalicCentered : _boldCentered;
                }
                return italic ? _boldItalic : _bold;
            }
            if (centered)
            {
                return italic ? _italicCentered : _regularCentered;
            }
            return italic ? _italic : _regular;
        }

        private void EnsureFormats()
        {
            if (!_formatsDirty && _regular != null && _regular.FontSize == FontSize)
            {
                return;
            }
            DisposeFormats();
            _regular = MakeFormat(false, false, false);
            _italic = MakeFormat(false, true, false);
            _bold = MakeFormat(true, false, false);
            _boldItalic = MakeFormat(true, true, false);
            _regularCentered = MakeFormat(false, false, true);
            _italicCentered = MakeFormat(false, true, true);
            _boldCentered = MakeFormat(true, false, true);
            _boldItalicCentered = MakeFormat(true, true, true);
            _formatsDirty = false;
        }

        private CanvasTextFormat MakeFormat(bool boldWeight, bool italic, bool centered)
        {
            return new CanvasTextFormat
            {
                FontFamily = boldWeight ? BoldFontFamilyUri : FontFamilyUri,
                FontSize = FontSize,
                FontWeight = boldWeight ? FontWeights.Bold : FontWeights.Normal,
                FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
                WordWrapping = CanvasWordWrapping.NoWrap,
                HorizontalAlignment = centered
                    ? CanvasHorizontalAlignment.Center
                    : CanvasHorizontalAlignment.Left,
                VerticalAlignment = CanvasVerticalAlignment.Center
            };
        }

        private void DisposeFormats()
        {
            if (_regular != null) { _regular.Dispose(); _regular = null; }
            if (_italic != null) { _italic.Dispose(); _italic = null; }
            if (_bold != null) { _bold.Dispose(); _bold = null; }
            if (_boldItalic != null) { _boldItalic.Dispose(); _boldItalic = null; }
            if (_regularCentered != null) { _regularCentered.Dispose(); _regularCentered = null; }
            if (_italicCentered != null) { _italicCentered.Dispose(); _italicCentered = null; }
            if (_boldCentered != null) { _boldCentered.Dispose(); _boldCentered = null; }
            if (_boldItalicCentered != null) { _boldItalicCentered.Dispose(); _boldItalicCentered = null; }
            _formatsDirty = true;
        }

        private void OnMetricsInvalidated()
        {
            EventHandler handler = MetricsInvalidated;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private static float Clamp(float value, float minimum, float maximum, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return fallback;
            }
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static bool TryReadCell(byte[] cells, int row, int col, int cols, out TerminalCell cell)
        {
            cell = default(TerminalCell);
            if (cells == null || cols <= 0)
            {
                return false;
            }
            int index = row * cols + col;
            int offset = index * TerminalCell.BytesPerCell;
            if (index < 0 || offset + TerminalCell.BytesPerCell > cells.Length)
            {
                return false;
            }
            cell = CellBufferReader.Read(cells, index);
            return true;
        }

        private static bool IsDirty(byte[] dirty, int row)
        {
            int i = row / 8;
            if (dirty == null || i < 0 || i >= dirty.Length)
            {
                return false;
            }
            return (dirty[i] & (1 << (row % 8))) != 0;
        }

        private static Color ToColor(uint argb)
        {
            return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }
    }
}
