using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    public class TerminalChromePolicyTests
    {
        private static TerminalChromeInput Phone(double width, double height)
        {
            return new TerminalChromeInput
            {
                Width = width,
                Height = height,
                KeyBarSetting = true,
                WideBreakpoint = 720,
                CompactHeightBreakpoint = 400
            };
        }

        [Fact]
        public void Lumia950Portrait_ShowsInfoBarAndKeyBar()
        {
            TerminalChromeLayout l = TerminalChromePolicy.Compute(Phone(360, 616));
            Assert.True(l.ShowInfoBar);
            Assert.True(l.ShowKeyBar);
            Assert.False(l.ShowCompactMenu);
            Assert.False(l.IsCompactHeight);
        }

        [Fact]
        public void Lumia950Landscape_CollapsesInfoBar_KeepsMenuAndKeyBar()
        {
            TerminalChromeLayout l = TerminalChromePolicy.Compute(Phone(616, 360));
            Assert.True(l.IsCompactHeight);
            Assert.False(l.IsWide);
            Assert.False(l.ShowInfoBar);
            Assert.True(l.ShowCompactMenu);
            Assert.True(l.ShowKeyBar);
        }

        [Fact]
        public void FindOpen_ReplacesInfoBarAndCompactMenu()
        {
            TerminalChromeInput input = Phone(616, 360);
            input.FindOpen = true;
            TerminalChromeLayout l = TerminalChromePolicy.Compute(input);
            Assert.True(l.ShowFindBar);
            Assert.False(l.ShowInfoBar);
            Assert.False(l.ShowCompactMenu);
        }

        [Fact]
        public void Wide_HidesKeyBarByDefault_ButManualToggleWins()
        {
            TerminalChromeInput input = Phone(1280, 720);
            Assert.False(TerminalChromePolicy.Compute(input).ShowKeyBar);
            input.KeyBarOverride = true;
            Assert.True(TerminalChromePolicy.Compute(input).ShowKeyBar);
        }

        [Fact]
        public void ManualHide_WinsOnNarrow()
        {
            TerminalChromeInput input = Phone(360, 616);
            input.KeyBarOverride = false;
            Assert.False(TerminalChromePolicy.Compute(input).ShowKeyBar);
        }

        [Fact]
        public void MouseModeOrSettingOff_HidesKeyBarByDefault()
        {
            TerminalChromeInput input = Phone(360, 616);
            input.MouseMode = true;
            Assert.False(TerminalChromePolicy.Compute(input).ShowKeyBar);
            input.MouseMode = false;
            input.KeyBarSetting = false;
            Assert.False(TerminalChromePolicy.Compute(input).ShowKeyBar);
        }

        [Fact]
        public void ZeroHeightBeforeLayout_IsNotCompact()
        {
            TerminalChromeLayout l = TerminalChromePolicy.Compute(Phone(0, 0));
            Assert.False(l.IsCompactHeight);
            Assert.True(l.ShowInfoBar);
        }

        [Fact]
        public void NullInput_FallsBackToFullChrome()
        {
            TerminalChromeLayout l = TerminalChromePolicy.Compute(null);
            Assert.True(l.ShowInfoBar);
            Assert.True(l.ShowKeyBar);
        }

        // fix/functional-pass（P2-8）：竖屏弹 SIP 把页面压到 340（< 断点 400）不应收起信息条。
        [Fact]
        public void SipShrinksPortraitPage_NotCompact()
        {
            TerminalChromeInput input = Phone(360, 340);
            input.SipVisible = true;
            input.HeightBeforeSip = 616;
            input.WidthBeforeSip = 360;
            TerminalChromeLayout l = TerminalChromePolicy.Compute(input);
            Assert.False(l.IsCompactHeight);
            Assert.True(l.ShowInfoBar);
            Assert.False(l.ShowCompactMenu);
        }

        [Fact]
        public void SipVisible_AfterRotation_UsesCurrentHeight()
        {
            // 宽度变了（转到横屏）：弹出前的竖屏高度不可信，按当前高度判定。
            TerminalChromeInput input = Phone(616, 200);
            input.SipVisible = true;
            input.HeightBeforeSip = 616;
            input.WidthBeforeSip = 360;
            Assert.True(TerminalChromePolicy.Compute(input).IsCompactHeight);
        }

        [Fact]
        public void LandscapeWithSip_StaysCompact()
        {
            TerminalChromeInput input = Phone(616, 160);
            input.SipVisible = true;
            input.HeightBeforeSip = 360;
            input.WidthBeforeSip = 616;
            Assert.True(TerminalChromePolicy.Compute(input).IsCompactHeight);
        }

        [Fact]
        public void SipHidden_IgnoresHeightBeforeSip()
        {
            TerminalChromeInput input = Phone(360, 340);
            input.SipVisible = false;
            input.HeightBeforeSip = 616;
            input.WidthBeforeSip = 360;
            Assert.True(TerminalChromePolicy.Compute(input).IsCompactHeight);
        }

        [Fact]
        public void HeightWithoutSip_SqueezedPage_AddsOcclusion()
        {
            // 窗高 640、SIP 遮 300（顶 340）；页面顶 0、被压到 340。
            Assert.Equal(640, TerminalChromePolicy.HeightWithoutSip(0, 340, 340, 300));
        }

        [Fact]
        public void HeightWithoutSip_PageUnderSip_KeepsHeight()
        {
            // 页面仍是 640 高、延伸到 SIP 下面（系统只覆盖）：当前高度即完整高度。
            Assert.Equal(640, TerminalChromePolicy.HeightWithoutSip(0, 640, 340, 300));
        }

        [Fact]
        public void HeightWithoutSip_NoSip_KeepsHeight()
        {
            Assert.Equal(616, TerminalChromePolicy.HeightWithoutSip(24, 616, 0, 0));
        }
    }
}
