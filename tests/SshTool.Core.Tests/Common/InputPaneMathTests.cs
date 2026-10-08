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
    }
}
