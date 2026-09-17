namespace SshTool.Core.Terminal
{
    // 键条/物理键盘可产生的命名键。可打印字符键统一用 Char（实际字符放 KeyChord.Character），
    // 让 Ctrl 组合与键盘布局无关（Ctrl+[ 不论哪个键位都发 0x1B）。
    public enum TerminalKey
    {
        None = 0,
        Up,
        Down,
        Left,
        Right,
        Home,
        End,
        PageUp,
        PageDown,
        Insert,
        Delete,
        F1,
        F2,
        F3,
        F4,
        F5,
        F6,
        F7,
        F8,
        F9,
        F10,
        F11,
        F12,
        Escape,
        Enter,
        Tab,
        Backspace,
        Char
    }
}
