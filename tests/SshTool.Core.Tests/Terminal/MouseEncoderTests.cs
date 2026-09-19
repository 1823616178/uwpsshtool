using System.Text;
using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    public class MouseEncoderTests
    {
        [Fact]
        public void ShouldReport_FourModes()
        {
            Assert.False(MouseEncoder.ShouldReport(0, MouseEventKind.Press, false));
            Assert.True(MouseEncoder.ShouldReport(9, MouseEventKind.Press, false));
            Assert.False(MouseEncoder.ShouldReport(9, MouseEventKind.Release, false));
            Assert.False(MouseEncoder.ShouldReport(9, MouseEventKind.Move, true));

            Assert.True(MouseEncoder.ShouldReport(1000, MouseEventKind.Press, false));
            Assert.True(MouseEncoder.ShouldReport(1000, MouseEventKind.Release, false));
            Assert.False(MouseEncoder.ShouldReport(1000, MouseEventKind.Move, true));

            Assert.True(MouseEncoder.ShouldReport(1002, MouseEventKind.Move, true));
            Assert.False(MouseEncoder.ShouldReport(1002, MouseEventKind.Move, false));

            Assert.True(MouseEncoder.ShouldReport(1003, MouseEventKind.Move, false));
            Assert.True(MouseEncoder.ShouldReport(1003, MouseEventKind.Wheel, false));
        }

        [Fact]
        public void Sgr_PressRelease_UsesMAndm()
        {
            byte[] press = MouseEncoder.Encode(1000, true, MouseEventKind.Press, 0, 4, 7, false, false, false);
            Assert.Equal(Encoding.ASCII.GetBytes("\x1b[<0;4;7M"), press);
            byte[] release = MouseEncoder.Encode(1000, true, MouseEventKind.Release, 0, 4, 7, false, false, false);
            Assert.Equal(Encoding.ASCII.GetBytes("\x1b[<0;4;7m"), release);
        }

        [Fact]
        public void Default_PressRelease_UsesButtonPlus32AndButton3()
        {
            byte[] press = MouseEncoder.Encode(1000, false, MouseEventKind.Press, 0, 1, 1, false, false, false);
            Assert.Equal(new byte[] { 0x1B, (byte)'[', (byte)'M', 32, 33, 33 }, press);
            byte[] release = MouseEncoder.Encode(1000, false, MouseEventKind.Release, 0, 1, 1, false, false, false);
            Assert.Equal(new byte[] { 0x1B, (byte)'[', (byte)'M', 3 + 32, 33, 33 }, release);
        }

        [Fact]
        public void ButtonEvent_MoveAdds32()
        {
            byte[] move = MouseEncoder.Encode(1002, true, MouseEventKind.Move, 0, 2, 3, false, false, false);
            Assert.Equal(Encoding.ASCII.GetBytes("\x1b[<32;2;3M"), move);
        }

        [Fact]
        public void AnyEvent_MoveWithoutButton()
        {
            byte[] move = MouseEncoder.Encode(1003, true, MouseEventKind.Move, 0, 8, 9, false, false, false);
            Assert.Equal(Encoding.ASCII.GetBytes("\x1b[<32;8;9M"), move);
        }

        [Fact]
        public void X10_PressOnlyEncoding()
        {
            byte[] press = MouseEncoder.Encode(9, false, MouseEventKind.Press, 2, 5, 6, false, false, false);
            Assert.Equal(new byte[] { 0x1B, (byte)'[', (byte)'M', 2 + 32, 5 + 32, 6 + 32 }, press);
        }

        [Fact]
        public void Sgr_CoordinateAbove223_Unclamped()
        {
            byte[] seq = MouseEncoder.Encode(1000, true, MouseEventKind.Press, 0, 300, 400, false, false, false);
            Assert.Equal(Encoding.ASCII.GetBytes("\x1b[<0;300;400M"), seq);
        }

        [Fact]
        public void Default_CoordinateAbove223_Clamped()
        {
            byte[] seq = MouseEncoder.Encode(1000, false, MouseEventKind.Press, 0, 300, 2, false, false, false);
            Assert.Equal(new byte[] { 0x1B, (byte)'[', (byte)'M', 32, 223 + 32, 2 + 32 }, seq);
        }

        [Fact]
        public void Modifiers_ShiftAltCtrl()
        {
            // shift 4 + alt 8 + ctrl 16 = 28
            byte[] seq = MouseEncoder.Encode(1000, true, MouseEventKind.Press, 0, 1, 1, true, true, true);
            Assert.Equal(Encoding.ASCII.GetBytes("\x1b[<28;1;1M"), seq);
        }

        [Fact]
        public void Wheel_SgrAndX10()
        {
            Assert.Equal(Encoding.ASCII.GetBytes("\x1b[<64;4;7M"),
                MouseEncoder.EncodeWheel(true, 4, 7, true));
            byte[] x10 = MouseEncoder.EncodeWheel(false, 1, 1, false);
            Assert.Equal(new byte[] { 0x1B, (byte)'[', (byte)'M', 65 + 32, 33, 33 }, x10);
        }
    }
}
