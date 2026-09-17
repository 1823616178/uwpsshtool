using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    // 01-DESIGN §7.5 键条 / 02-UI-DESIGN §5.6 三态：点按 Off→OneShot→Locked→Off，
    // 长按切换 Locked，OneShot 用后自动释放。
    public class StickyModifiersTests
    {
        [Fact]
        public void Initial_AllOff()
        {
            var sticky = new StickyModifiers();
            Assert.Equal(StickyState.Off, sticky.Ctrl);
            Assert.Equal(StickyState.Off, sticky.Alt);
            Assert.Equal(StickyState.Off, sticky.Shift);
        }

        [Fact]
        public void Tap_CyclesOffOneShotLockedOff()
        {
            var sticky = new StickyModifiers();
            sticky.Tap(ModifierKey.Ctrl);
            Assert.Equal(StickyState.OneShot, sticky.Ctrl);
            sticky.Tap(ModifierKey.Ctrl); // 双击 → 锁定
            Assert.Equal(StickyState.Locked, sticky.Ctrl);
            sticky.Tap(ModifierKey.Ctrl); // 再点 → 释放
            Assert.Equal(StickyState.Off, sticky.Ctrl);
        }

        [Fact]
        public void LongPress_LocksAndReleases()
        {
            var sticky = new StickyModifiers();
            sticky.LongPress(ModifierKey.Alt);
            Assert.Equal(StickyState.Locked, sticky.Alt);
            sticky.LongPress(ModifierKey.Alt);
            Assert.Equal(StickyState.Off, sticky.Alt);
        }

        [Fact]
        public void LongPress_FromOneShot_Locks()
        {
            var sticky = new StickyModifiers();
            sticky.Tap(ModifierKey.Shift);
            sticky.LongPress(ModifierKey.Shift);
            Assert.Equal(StickyState.Locked, sticky.Shift);
        }

        [Fact]
        public void Wrap_OneShotAppliedAndAutoReleased()
        {
            var sticky = new StickyModifiers();
            sticky.Tap(ModifierKey.Ctrl);

            var chord = sticky.Wrap(TerminalKey.Char, 'c');

            Assert.True(chord.Ctrl);
            Assert.False(chord.Alt);
            Assert.Equal(StickyState.Off, sticky.Ctrl); // 单次用后即释放

            var next = sticky.Wrap(TerminalKey.Char, 'd');
            Assert.False(next.Ctrl);
        }

        [Fact]
        public void Wrap_LockedSurvivesRepeatedUse()
        {
            var sticky = new StickyModifiers();
            sticky.Tap(ModifierKey.Ctrl);
            sticky.Tap(ModifierKey.Ctrl); // Locked

            Assert.True(sticky.Wrap(TerminalKey.Char, 'c').Ctrl);
            Assert.Equal(StickyState.Locked, sticky.Ctrl);
            Assert.True(sticky.Wrap(TerminalKey.Char, 'd').Ctrl);
            Assert.Equal(StickyState.Locked, sticky.Ctrl);
        }

        [Fact]
        public void Modifiers_Independent()
        {
            var sticky = new StickyModifiers();
            sticky.Tap(ModifierKey.Ctrl);       // Ctrl 单次
            sticky.Tap(ModifierKey.Alt);
            sticky.Tap(ModifierKey.Alt);        // Alt 锁定

            var chord = sticky.Wrap(TerminalKey.Char, 'x');

            Assert.True(chord.Ctrl && chord.Alt);
            Assert.Equal(StickyState.Off, sticky.Ctrl);      // 单次已释放
            Assert.Equal(StickyState.Locked, sticky.Alt);    // 锁定保持
        }

        [Fact]
        public void Reset_ClearsAll()
        {
            var sticky = new StickyModifiers();
            sticky.Tap(ModifierKey.Ctrl);
            sticky.Tap(ModifierKey.Shift);
            sticky.Tap(ModifierKey.Shift);

            sticky.Reset();

            Assert.Equal(StickyState.Off, sticky.Ctrl);
            Assert.Equal(StickyState.Off, sticky.Shift);
        }

        [Fact]
        public void Changed_RaisedOnStateChangeOnly()
        {
            var sticky = new StickyModifiers();
            int changes = 0;
            sticky.Changed += delegate { changes++; };

            sticky.Tap(ModifierKey.Ctrl);   // Off→OneShot
            Assert.Equal(1, changes);
            sticky.Wrap(TerminalKey.Char, 'c'); // OneShot→Off（自动释放也算状态变化）
            Assert.Equal(2, changes);
            sticky.Wrap(TerminalKey.Char, 'd'); // 无状态变化
            Assert.Equal(2, changes);
            sticky.Reset();                 // 全 Off，无变化
            Assert.Equal(2, changes);
        }

        [Fact]
        public void Integration_ShiftOneShot_TabBecomesBacktab()
        {
            var sticky = new StickyModifiers();
            sticky.Tap(ModifierKey.Shift);

            var bytes = KeyMap.Map(sticky.Wrap(TerminalKey.Tab), new TerminalModes());

            Assert.Equal(new byte[] { 0x1B, (byte)'[', (byte)'Z' }, bytes);
            Assert.Equal(StickyState.Off, sticky.Shift);
        }

        [Fact]
        public void Integration_CtrlOneShot_CharBecomesCtrlByte()
        {
            var sticky = new StickyModifiers();
            sticky.Tap(ModifierKey.Ctrl);

            var bytes = KeyMap.Map(sticky.Wrap(TerminalKey.Char, 'c'), new TerminalModes());

            Assert.Equal(new byte[] { 0x03 }, bytes);
        }
    }
}
