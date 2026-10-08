using System;

namespace SshTool.Core.Common
{
    // ui/fix-pass：表单页软键盘（SIP）避让的纯计算部分（App 侧 InputPaneScroll 调用，便于单测）。
    public static class InputPaneMath
    {
        // 滚动区底边（窗口坐标）被 SIP 盖住的高度；SIP 顶 = 窗高 − 遮挡高。
        public static double Overlap(double viewportBottomInWindow, double windowHeight, double occludedHeight)
        {
            if (occludedHeight <= 0 || windowHeight <= 0)
            {
                return 0;
            }
            double sipTop = windowHeight - occludedHeight;
            return Math.Max(0, viewportBottomInWindow - sipTop);
        }

        // 让焦点元素完整落在「视口未被遮挡部分」内所需的新纵向偏移；已经完整可见返回 null（不滚，避免抖动）。
        //   elementTop：元素顶相对视口顶（已含当前滚动）；visibleHeight：视口高 − 遮挡高；margin：上下留白。
        public static double? ScrollTarget(double currentOffset, double elementTop, double elementHeight,
            double visibleHeight, double margin)
        {
            if (visibleHeight <= 0)
            {
                return null;
            }
            double top = elementTop - margin;
            double bottom = elementTop + elementHeight + margin;
            if (top >= 0 && bottom <= visibleHeight)
            {
                return null;
            }
            double target;
            if (bottom - top > visibleHeight || top < 0)
            {
                // 元素比可见区还高，或在可见区上方：顶对齐。
                target = currentOffset + top;
            }
            else
            {
                // 在可见区下方：底对齐。
                target = currentOffset + (bottom - visibleHeight);
            }
            return Math.Max(0, target);
        }
    }
}
