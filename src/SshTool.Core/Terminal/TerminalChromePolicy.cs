namespace SshTool.Core.Terminal
{
    // ui/fix-pass：终端页顶/底栏显隐的唯一判定（纯函数，可单测）。
    // 旧实现三处各管一段：OnNavigatedTo 写 Keys.Visibility 本地值、WideState 的 Setter 强制 Collapsed、
    // 菜单 ToggleKeyBar 再写本地值——VisualState Setter 优先级高于本地值，宽屏下「显示键条」点了没反应。
    // 现在页面只把输入喂给这里，再一次性写回各元素。
    public sealed class TerminalChromeInput
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public bool FindOpen { get; set; }
        // 设置页「显示键条」。
        public bool KeyBarSetting { get; set; }
        // 鼠标模式 / Continuum：键条默认隐藏。
        public bool MouseMode { get; set; }
        // 用户本页通过菜单手动切换过：true/false 覆盖默认；null = 跟随默认。
        public bool? KeyBarOverride { get; set; }
        public double WideBreakpoint { get; set; }
        // 低于该窗高（手机横屏 ≈360 epx）收起信息条，把行数让给终端。
        public double CompactHeightBreakpoint { get; set; }
        // fix/functional-pass（P2-8）：软键盘弹出时页面可能被系统压矮（竖屏 640 − SIP ≈ 340 < 断点），
        // 不能因此误判成「横屏矮窗」收起信息条。SIP 弹出期间按弹出前的高度判定——
        // 仅当宽度没变（没转屏）时才可信，转屏后按当前高度。
        public bool SipVisible { get; set; }
        public double HeightBeforeSip { get; set; }
        public double WidthBeforeSip { get; set; }
    }

    public struct TerminalChromeLayout
    {
        public bool ShowInfoBar;
        public bool ShowFindBar;
        // 信息条收起时右上角的浮动「更多」按钮——菜单入口不能跟着信息条一起消失。
        public bool ShowCompactMenu;
        public bool ShowKeyBar;
        public bool IsWide;
        public bool IsCompactHeight;
    }

    public static class TerminalChromePolicy
    {
        public static TerminalChromeLayout Compute(TerminalChromeInput input)
        {
            var layout = new TerminalChromeLayout();
            if (input == null)
            {
                layout.ShowInfoBar = true;
                layout.ShowKeyBar = true;
                return layout;
            }
            layout.IsWide = input.WideBreakpoint > 0 && input.Width >= input.WideBreakpoint;
            // 尚未布局（高度 0）时不判紧凑，避免首帧闪一下。
            double height = EffectiveHeight(input);
            layout.IsCompactHeight = height > 0 && height < input.CompactHeightBreakpoint;
            layout.ShowFindBar = input.FindOpen;
            layout.ShowInfoBar = !input.FindOpen && !layout.IsCompactHeight;
            layout.ShowCompactMenu = !input.FindOpen && layout.IsCompactHeight;
            bool keyBarDefault = input.KeyBarSetting && !input.MouseMode && !layout.IsWide;
            layout.ShowKeyBar = input.KeyBarOverride.HasValue ? input.KeyBarOverride.Value : keyBarDefault;
            return layout;
        }

        // 估算「没有 SIP 时」的页面高度：页面底边落在 SIP 顶边之上（被系统压矮让位）→ 加回遮挡高；
        // 页面仍延伸到 SIP 下面（系统只是覆盖/上推）→ 当前高度就是完整高度。坐标均为窗口坐标。
        public static double HeightWithoutSip(double pageTop, double pageHeight, double occludedTop, double occludedHeight)
        {
            if (occludedHeight <= 0 || pageHeight <= 0)
            {
                return pageHeight;
            }
            double pageBottom = pageTop + pageHeight;
            return pageBottom <= occludedTop + SqueezeTolerance ? pageHeight + occludedHeight : pageHeight;
        }

        private const double SqueezeTolerance = 1.0;

        // 判定紧凑高度用的窗高（见 TerminalChromeInput.SipVisible）。
        public static double EffectiveHeight(TerminalChromeInput input)
        {
            if (input == null)
            {
                return 0;
            }
            if (input.SipVisible && input.HeightBeforeSip > input.Height
                && System.Math.Abs(input.WidthBeforeSip - input.Width) < 1.0)
            {
                return input.HeightBeforeSip;
            }
            return input.Height;
        }
    }
}
