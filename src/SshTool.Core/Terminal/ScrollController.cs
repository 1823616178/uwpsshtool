using System;

namespace SshTool.Core.Terminal
{
    // 拖动像素 → 整行（余量跨事件累计）。deltaY > 0 = 手指下移 = 看更旧内容。
    public sealed class ScrollLineAccumulator
    {
        private double _remainder;

        public double Remainder
        {
            get { return _remainder; }
        }

        public int Push(double deltaY, double cellHeight)
        {
            if (cellHeight <= 0 || double.IsNaN(cellHeight) || double.IsInfinity(cellHeight))
            {
                return 0;
            }
            if (double.IsNaN(deltaY) || double.IsInfinity(deltaY))
            {
                return 0;
            }
            _remainder += deltaY;
            int lines = (int)(_remainder / cellHeight);
            _remainder -= lines * cellHeight;
            return lines;
        }

        public void Reset()
        {
            _remainder = 0;
        }
    }

    // 01-DESIGN.md §7.6：视口偏移 0 = 底部（当前屏）。正偏移 = 向回滚区上移。
    public sealed class ScrollController
    {
        public const int MinimumFontSize = 8;
        public const int MaximumFontSize = 28;
        public const int WheelLinesPerNotch = 3;

        private int _offset;
        private int _scrollbackCount;

        public int Offset
        {
            get { return _offset; }
        }

        public int ScrollbackCount
        {
            get { return _scrollbackCount; }
        }

        public int MaxOffset
        {
            get { return _scrollbackCount < 0 ? 0 : _scrollbackCount; }
        }

        public bool IsAtBottom
        {
            get { return _offset == 0; }
        }

        public event EventHandler OffsetChanged;

        public static int ClampOffset(int offset, int maxOffset)
        {
            int cap = maxOffset > 0 ? maxOffset : 0;
            if (offset < 0)
            {
                return 0;
            }
            return offset > cap ? cap : offset;
        }

        public static int ClampFontSize(double size)
        {
            if (double.IsNaN(size) || double.IsInfinity(size))
            {
                return MinimumFontSize;
            }
            if (size < MinimumFontSize)
            {
                return MinimumFontSize;
            }
            if (size > MaximumFontSize)
            {
                return MaximumFontSize;
            }
            int rounded = (int)Math.Round(size);
            if (rounded < MinimumFontSize)
            {
                return MinimumFontSize;
            }
            if (rounded > MaximumFontSize)
            {
                return MaximumFontSize;
            }
            return rounded;
        }

        public void SetScrollbackCount(int count)
        {
            _scrollbackCount = count < 0 ? 0 : count;
            SetOffset(ClampOffset(_offset, MaxOffset));
        }

        // 正 lines = 看更旧内容（增大 offset）。
        public int ApplyLines(int lines)
        {
            return SetOffset(_offset + lines);
        }

        // W02：跳到指定偏移（查找命中定位）；越界夹取。
        public void ScrollTo(int offset)
        {
            SetOffset(offset);
        }

        public void SnapToBottom()
        {
            SetOffset(0);
        }

        // 新输出：在底部则跟随（保持 0）；已回滚则保持偏移并夹取。
        public void OnOutput(int newScrollbackCount)
        {
            bool follow = _offset == 0;
            _scrollbackCount = newScrollbackCount < 0 ? 0 : newScrollbackCount;
            if (follow)
            {
                SetOffset(0);
                return;
            }
            SetOffset(ClampOffset(_offset, MaxOffset));
        }

        // 用户击键 / 粘贴：回到底部。
        public void OnUserInput()
        {
            SetOffset(0);
        }

        private int SetOffset(int value)
        {
            int next = ClampOffset(value, MaxOffset);
            if (next == _offset)
            {
                return _offset;
            }
            _offset = next;
            EventHandler handler = OffsetChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
            return _offset;
        }
    }
}
