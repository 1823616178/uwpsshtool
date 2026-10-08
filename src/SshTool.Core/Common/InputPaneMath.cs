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

        // fix/functional-pass（P2-8）：滚动区内「未被 SIP 盖住」的高度（以滚动区自身顶边为 0 的坐标）。
        // 旧实现用 ViewportHeight − overlap，但补底部 Padding 后 ScrollContentPresenter 已经缩短、
        // ViewportHeight 已不含被盖部分，再减一次 overlap 等于扣了两遍——焦点框被滚到偏上、
        // 可见区底部大块留白。这里直接按几何算：底边取「滚动区底（扣当前底部 Padding）」与
        // 「SIP 顶边」中较高者，与 Padding 是否已生效无关。
        //   viewerTopInWindow：滚动区顶（窗口坐标）；viewerHeight：滚动区 ActualHeight；
        //   paddingBottom：当前底部内边距（含已补的避让量）；windowHeight / occludedHeight：窗高 / SIP 遮挡高。
        public static double VisibleHeight(double viewerTopInWindow, double viewerHeight, double paddingBottom,
            double windowHeight, double occludedHeight)
        {
            double bottom = viewerHeight - Math.Max(0, paddingBottom);
            if (occludedHeight > 0 && windowHeight > 0)
            {
                double sipTopInViewer = windowHeight - occludedHeight - viewerTopInWindow;
                bottom = Math.Min(bottom, sipTopInViewer);
            }
            return Math.Max(0, bottom);
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
