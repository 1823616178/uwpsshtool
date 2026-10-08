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
    }
}
