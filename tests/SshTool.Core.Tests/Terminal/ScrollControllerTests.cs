using System.Collections.Generic;
using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    public class ScrollControllerTests
    {
        [Fact]
        public void ClampOffset_PinsToRange()
        {
            Assert.Equal(0, ScrollController.ClampOffset(-3, 10));
            Assert.Equal(10, ScrollController.ClampOffset(99, 10));
            Assert.Equal(4, ScrollController.ClampOffset(4, 10));
            Assert.Equal(0, ScrollController.ClampOffset(5, 0));
            Assert.Equal(0, ScrollController.ClampOffset(5, -1));
        }

        [Fact]
        public void ApplyLines_MovesTowardOldContentAndClamps()
        {
            var scroll = new ScrollController();
            scroll.SetScrollbackCount(20);
            Assert.Equal(5, scroll.ApplyLines(5));
            Assert.Equal(5, scroll.Offset);
            Assert.Equal(20, scroll.ApplyLines(100));
            Assert.Equal(0, scroll.ApplyLines(-100));
            Assert.True(scroll.IsAtBottom);
        }

        [Fact]
        public void OnOutput_AtBottom_StaysAtBottom()
        {
            var scroll = new ScrollController();
            scroll.SetScrollbackCount(10);
            scroll.OnOutput(40);
            Assert.Equal(0, scroll.Offset);
            Assert.Equal(40, scroll.ScrollbackCount);
        }

        [Fact]
        public void OnOutput_WhileScrolled_KeepsOffsetAndClamps()
        {
            var scroll = new ScrollController();
            scroll.SetScrollbackCount(50);
            scroll.ApplyLines(12);
            scroll.OnOutput(80);
            Assert.Equal(12, scroll.Offset);
            scroll.OnOutput(5);
            Assert.Equal(5, scroll.Offset);
        }

        [Fact]
        public void OnUserInput_ReturnsToBottom()
        {
            var scroll = new ScrollController();
            scroll.SetScrollbackCount(30);
            scroll.ApplyLines(8);
            scroll.OnUserInput();
            Assert.Equal(0, scroll.Offset);
            Assert.True(scroll.IsAtBottom);
        }

        [Fact]
        public void Accumulator_CarriesRemainderAcrossPushes()
        {
            var acc = new ScrollLineAccumulator();
            Assert.Equal(0, acc.Push(10, 18));
            Assert.Equal(10, acc.Remainder);
            Assert.Equal(1, acc.Push(10, 18));
            Assert.Equal(2, acc.Remainder, 5);
            acc.Reset();
            Assert.Equal(0, acc.Remainder);
            Assert.Equal(0, acc.Push(5, 0));
        }

        [Fact]
        public void ClampFontSize_UsesDesignRange()
        {
            Assert.Equal(8, ScrollController.ClampFontSize(1));
            Assert.Equal(28, ScrollController.ClampFontSize(99));
            Assert.Equal(12, ScrollController.ClampFontSize(12.4));
            Assert.Equal(8, ScrollController.ClampFontSize(double.NaN));
        }

        [Fact]
        public void MouseEncoder_SgrWheel_UsesOneBasedCoordinates()
        {
            byte[] up = MouseEncoder.EncodeWheel(true, 4, 7, true);
            Assert.Equal(System.Text.Encoding.ASCII.GetBytes("\x1b[<64;4;7M"), up);
            byte[] down = MouseEncoder.EncodeWheel(false, 1, 1, true);
            Assert.Equal(System.Text.Encoding.ASCII.GetBytes("\x1b[<65;1;1M"), down);
        }

        [Fact]
        public void MouseEncoder_X10Wheel_ClampsAbove223()
        {
            byte[] seq = MouseEncoder.EncodeWheel(true, 300, 2, false);
            Assert.Equal(new byte[] { 0x1B, (byte)'[', (byte)'M', 64 + 32, 223 + 32, 2 + 32 }, seq);
        }

        [Fact]
        public void EncodeAltScroll_ArrowsAndWheel()
        {
            IReadOnlyList<byte[]> arrows = MouseEncoder.EncodeAltScroll(
                2, false, 0, true, 1, 1, new TerminalModes());
            Assert.Equal(2, arrows.Count);
            Assert.Equal(KeyMap.Map(new KeyChord(TerminalKey.Up), new TerminalModes()), arrows[0]);

            IReadOnlyList<byte[]> wheel = MouseEncoder.EncodeAltScroll(
                -1, true, 1000, true, 3, 5, new TerminalModes());
            Assert.Equal(MouseEncoder.EncodeWheel(false, 3, 5, true), wheel[0]);

            IReadOnlyList<byte[]> fallback = MouseEncoder.EncodeAltScroll(
                1, true, 0, true, 1, 1, new TerminalModes());
            Assert.Equal(KeyMap.Map(new KeyChord(TerminalKey.Up), new TerminalModes()), fallback[0]);
        }
    }
}
