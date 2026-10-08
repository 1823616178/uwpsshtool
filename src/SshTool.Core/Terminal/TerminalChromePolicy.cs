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
            layout.IsCompactHeight = input.Height > 0 && input.Height < input.CompactHeightBreakpoint;
            layout.ShowFindBar = input.FindOpen;
            layout.ShowInfoBar = !input.FindOpen && !layout.IsCompactHeight;
            layout.ShowCompactMenu = !input.FindOpen && layout.IsCompactHeight;
            bool keyBarDefault = input.KeyBarSetting && !input.MouseMode && !layout.IsWide;
            layout.ShowKeyBar = input.KeyBarOverride.HasValue ? input.KeyBarOverride.Value : keyBarDefault;
            return layout;
        }
    }
}
