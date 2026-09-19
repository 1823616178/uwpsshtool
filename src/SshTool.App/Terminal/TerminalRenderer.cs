using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using SshTool.Core.Terminal;
using Windows.UI;

namespace SshTool.App.Terminal
{
    // T05：行缓存 + 脏行重绘 + 块光标（01-DESIGN.md §7.3 / SP04 纪律）。
    // 属性管线（bold-as-bright / reverse / dim / DeviceLost）留给 T06。
    public sealed class TerminalRenderer : IDisposable
    {
        public const string FontFamilyUri = "ms-appx:///Assets/Fonts/JetBrainsMono-Regular.ttf#JetBrains Mono";
        public const uint FallbackFgArgb = 0xFFE5E5E5u;
        public const uint FallbackBgArgb = 0xFF000000u;

        private CanvasRenderTarget _surface;
        private CanvasTextFormat _format;
        private int _cols;
        private int _rows;
        private bool _disposed;

        public float CellWidth { get; private set; }
        public float CellHeight { get; private set; }
        public float FontSize { get; set; }
        public uint DefaultFgArgb { get; set; }
        public uint DefaultBgArgb { get; set; }
        public Color CursorColor { get; set; }

        public TerminalRenderer()
        {
            FontSize = 12f;
            DefaultFgArgb = FallbackFgArgb;
            DefaultBgArgb = FallbackBgArgb;
            CursorColor = Color.FromArgb(255, 0xE8, 0xEE, 0xFB);
        }

        public bool EnsureMetrics(CanvasControl canvas)
        {
            if (canvas == null)
            {
                return false;
            }
            if (_format == null)
            {
                _format = new CanvasTextFormat
                {
                    FontFamily = FontFamilyUri,
                    FontSize = FontSize,
                    WordWrapping = CanvasWordWrapping.NoWrap
                };
            }
            using (var layout = new CanvasTextLayout(canvas, "M", _format, 0, 0))
            {
                CellWidth = (float)Math.Ceiling(layout.LayoutBounds.Width);
                CellHeight = (float)Math.Ceiling(layout.LayoutBounds.Height);
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
            if (blinkOn && screen.CursorVisible)
            {
                DrawBlockCursor(ds, screen);
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            InvalidateSurface();
            if (_format != null)
            {
                _format.Dispose();
                _format = null;
            }
        }

        private void EnsureSurface(CanvasControl canvas, int cols, int rows)
        {
            if (cols <= 0 || rows <= 0)
            {
                return;
            }
            if (_surface != null && _cols == cols && _rows == rows)
            {
                return;
            }
            InvalidateSurface();
            _cols = cols;
            _rows = rows;
            _surface = new CanvasRenderTarget(canvas, cols * CellWidth, rows * CellHeight);
        }

        private void PaintRow(CanvasDrawingSession ds, byte[] cells, int row, int cols)
        {
            float y = row * CellHeight;
            ds.FillRectangle(0, y, cols * CellWidth, CellHeight, ToColor(ResolveBg(TerminalCell.DefaultBgMarker)));
            List<CellRun> runs = RowRunBuilder.Build(cells, row, cols);
            for (int i = 0; i < runs.Count; i++)
            {
                CellRun run = runs[i];
                uint bg = ResolveBg(run.BgArgb);
                ds.FillRectangle(run.StartCol * CellWidth, y, run.Length * CellWidth, CellHeight, ToColor(bg));
                if (string.IsNullOrEmpty(run.Text) || (run.Attrs & TerminalCell.AttrInvisible) != 0)
                {
                    continue;
                }
                uint fg = ResolveFg(run.FgArgb);
                ds.DrawText(run.Text, run.StartCol * CellWidth, y, ToColor(fg), _format);
            }
        }

        private void DrawBlockCursor(CanvasDrawingSession ds, ITerminalScreen screen)
        {
            int col = screen.CursorCol;
            int row = screen.CursorRow;
            if (col < 0 || row < 0 || col >= _cols || row >= _rows)
            {
                return;
            }
            ds.FillRectangle(col * CellWidth, row * CellHeight, CellWidth, CellHeight, CursorColor);
        }

        private uint ResolveFg(uint argb)
        {
            return argb == TerminalCell.DefaultFgMarker ? DefaultFgArgb : argb;
        }

        private uint ResolveBg(uint argb)
        {
            return argb == TerminalCell.DefaultBgMarker ? DefaultBgArgb : argb;
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
