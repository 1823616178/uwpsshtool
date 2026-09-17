namespace SshTool.Core.Terminal
{
    // 影响键位映射的终端模式位（由 native 终端解析器维护，T 系列后续任务填充）。
    public sealed class TerminalModes
    {
        // DECCKM：方向键与 Home/End 用 SS3（ESC O X）而非 CSI（ESC [ X）
        public bool ApplicationCursorKeys { get; set; }
    }
}
