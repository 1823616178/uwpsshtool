using SshTool.Core.Common;
using Xunit;

namespace SshTool.Core.Tests.Common
{
    public class InputPaneMathTests
    {
        [Fact]
        public void Overlap_Lumia950PortraitSip()
        {
            // 窗高 640，SIP ≈ 260，滚动区底边贴窗底 → 被盖 260。
            Assert.Equal(260, InputPaneMath.Overlap(640, 640, 260));
            // 滚动区底下还有 56 的底栏 → 只被盖 204。
            Assert.Equal(204, InputPaneMath.Overlap(584, 640, 260));
        }

        [Fact]
        public void Overlap_NoSipOrAboveSip_IsZero()
        {
            Assert.Equal(0, InputPaneMath.Overlap(640, 640, 0));
            Assert.Equal(0, InputPaneMath.Overlap(300, 640, 260));
        }

        [Fact]
        public void ScrollTarget_FullyVisible_ReturnsNull()
        {
            Assert.Null(InputPaneMath.ScrollTarget(100, 50, 40, 300, 12));
        }

        [Fact]
        public void ScrollTarget_BelowVisibleArea_AlignsBottom()
        {
            // 元素顶在视口 400 处，可见区只剩 320：需要滚 400+40+12-320 = 132。
            Assert.Equal(232, InputPaneMath.ScrollTarget(100, 400, 40, 320, 12));
        }

        [Fact]
        public void ScrollTarget_AboveVisibleArea_AlignsTop()
        {
            Assert.Equal(78, InputPaneMath.ScrollTarget(100, -10, 40, 320, 12));
        }

        [Fact]
        public void ScrollTarget_NeverNegative()
        {
            Assert.Equal(0, InputPaneMath.ScrollTarget(5, -50, 40, 320, 12));
        }

        [Fact]
        public void ScrollTarget_ZeroVisible_ReturnsNull()
        {
            Assert.Null(InputPaneMath.ScrollTarget(0, 10, 40, 0, 12));
        }

        // fix/functional-pass（P2-8）：已补底部 Padding（ViewportHeight 已缩短）时不得再扣一遍遮挡。
        // 窗高 640，SIP 遮 300 → SIP 顶 340；滚动区顶 60、高 580（底 640）；补了 overlap=300 的 Padding。
        [Fact]
        public void VisibleHeight_WithPaddingApplied_NotDoubleSubtracted()
        {
            double overlap = InputPaneMath.Overlap(640, 640, 300);
            Assert.Equal(300, overlap);
            double visible = InputPaneMath.VisibleHeight(60, 580, overlap, 640, 300);
            Assert.Equal(280, visible); // = SIP 顶 340 − 滚动区顶 60；旧算法 (580−300)−300 = −20
        }

        [Fact]
        public void VisibleHeight_BeforePaddingApplied_UsesSipTop()
        {
            Assert.Equal(280, InputPaneMath.VisibleHeight(60, 580, 0, 640, 300));
        }

        [Fact]
        public void VisibleHeight_NoSip_IsViewerMinusPadding()
        {
            Assert.Equal(568, InputPaneMath.VisibleHeight(60, 580, 12, 640, 0));
        }

        [Fact]
        public void VisibleHeight_ViewerAboveSip_Unaffected()
        {
            // 滚动区底 300 在 SIP 顶 340 之上：不受遮挡。
            Assert.Equal(240, InputPaneMath.VisibleHeight(60, 240, 0, 640, 300));
        }

        [Fact]
        public void VisibleHeight_NeverNegative()
        {
            Assert.Equal(0, InputPaneMath.VisibleHeight(500, 100, 0, 640, 300));
        }
    }
}
