using System;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace SshTool.App.Terminal
{
    // T07：Win2D 字体度量缓存。CanvasControl 在 DPI 变化时重新触发 CreateResources，
    // DPI 也作为缓存键，保证字号或显示缩放变化后一定重测。
    public sealed class FontMetrics
    {
        private bool _valid;
        private float _fontSize;
        private float _lineHeightFactor;
        private float _dpi;

        public float CellWidth { get; private set; }
        public float CellHeight { get; private set; }
        public long Revision { get; private set; }

        public bool IsValid
        {
            get { return _valid && CellWidth > 0 && CellHeight > 0; }
        }

        public bool Ensure(CanvasControl canvas, CanvasTextFormat format,
                           float fontSize, float lineHeightFactor)
        {
            if (canvas == null || format == null)
            {
                return false;
            }

            float dpi = canvas.Dpi;
            if (_valid && _fontSize == fontSize
                && _lineHeightFactor == lineHeightFactor && _dpi == dpi)
            {
                return IsValid;
            }

            using (var layout = new CanvasTextLayout(canvas, "M", format, 0, 0))
            {
                CellWidth = (float)Math.Ceiling(layout.LayoutBounds.Width);
                CellHeight = (float)Math.Ceiling(layout.LayoutBounds.Height * lineHeightFactor);
            }
            _fontSize = fontSize;
            _lineHeightFactor = lineHeightFactor;
            _dpi = dpi;
            _valid = CellWidth > 0 && CellHeight > 0;
            Revision++;
            return IsValid;
        }

        public void Invalidate()
        {
            _valid = false;
        }
    }
}
