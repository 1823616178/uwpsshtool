namespace SshTool.Core.Terminal
{
    // 一次按键：命名键 + 修饰状态。Key==Char 时 Character 为实际字符（KeyBar 符号键直接给符号，
    // 物理键盘由 T09 从 CharacterReceived/VirtualKey 转换）。
    public sealed class KeyChord
    {
        public KeyChord(TerminalKey key)
            : this(key, '\0', false, false, false)
        {
        }

        public KeyChord(TerminalKey key, char character, bool ctrl, bool alt, bool shift)
        {
            Key = key;
            Character = character;
            Ctrl = ctrl;
            Alt = alt;
            Shift = shift;
        }

        public TerminalKey Key { get; private set; }
        public char Character { get; private set; }
        public bool Ctrl { get; private set; }
        public bool Alt { get; private set; }
        public bool Shift { get; private set; }

        public bool HasModifier
        {
            get { return Ctrl || Alt || Shift; }
        }
    }
}
