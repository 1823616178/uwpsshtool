using SshTool.Core.Appearance;
using Xunit;

namespace SshTool.Core.Tests.Appearance
{
    // A03：HsvColor 往返。
    public class HsvColorTests
    {
        [Fact]
        public void BlackRoundTrips()
        {
            HsvColor hsv = HsvColor.FromRgb(0, 0, 0);
            Assert.Equal(0, hsv.S);
            Assert.Equal(0, hsv.V);
            byte r;
            byte g;
            byte b;
            HsvColor.ToRgb(hsv, out r, out g, out b);
            Assert.Equal(0, r);
            Assert.Equal(0, g);
            Assert.Equal(0, b);
        }

        [Fact]
        public void WhiteRoundTrips()
        {
            HsvColor hsv = HsvColor.FromRgb(255, 255, 255);
            Assert.Equal(0, hsv.S);
            Assert.Equal(1, hsv.V);
            byte r;
            byte g;
            byte b;
            HsvColor.ToRgb(hsv, out r, out g, out b);
            Assert.Equal(255, r);
            Assert.Equal(255, g);
            Assert.Equal(255, b);
        }

        [Theory]
        [InlineData(255, 0, 0, 0)]
        [InlineData(0, 255, 0, 120)]
        [InlineData(0, 0, 255, 240)]
        [InlineData(255, 255, 0, 60)]
        [InlineData(0, 255, 255, 180)]
        [InlineData(255, 0, 255, 300)]
        public void PrimaryHues(byte r, byte g, byte b, double h)
        {
            HsvColor hsv = HsvColor.FromRgb(r, g, b);
            Assert.Equal(h, hsv.H, 0);
            Assert.Equal(1, hsv.S, 3);
            Assert.Equal(1, hsv.V, 3);
            byte r2;
            byte g2;
            byte b2;
            HsvColor.ToRgb(hsv, out r2, out g2, out b2);
            Assert.Equal(r, r2);
            Assert.Equal(g, g2);
            Assert.Equal(b, b2);
        }

        [Theory]
        [InlineData(79, 140, 255)]
        [InlineData(18, 22, 31)]
        [InlineData(232, 238, 251)]
        [InlineData(195, 124, 224)]
        public void ArbitraryColorsRoundTrip(byte r, byte g, byte b)
        {
            HsvColor hsv = HsvColor.FromRgb(r, g, b);
            byte r2;
            byte g2;
            byte b2;
            HsvColor.ToRgb(hsv, out r2, out g2, out b2);
            Assert.InRange(r2, r - 1, r + 1);
            Assert.InRange(g2, g - 1, g + 1);
            Assert.InRange(b2, b - 1, b + 1);
        }

        [Fact]
        public void SliderRoundTrip_Hue360MapsToRed()
        {
            byte r;
            byte g;
            byte b;
            HsvColor.ToRgb(new HsvColor(360, 1, 1), out r, out g, out b);
            Assert.Equal(255, r);
            Assert.Equal(0, g);
            Assert.Equal(0, b);
        }
    }
}
