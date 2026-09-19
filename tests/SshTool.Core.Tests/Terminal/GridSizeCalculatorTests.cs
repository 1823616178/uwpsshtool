using System;
using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    public class GridSizeCalculatorTests
    {
        [Theory]
        [InlineData(360, 640, 6, 18, 4, 44, true, 58, 32)]
        [InlineData(360, 640, 6, 18, 4, 44, false, 58, 35)]
        [InlineData(800, 600, 8, 20, 0, 0, true, 100, 30)]
        [InlineData(799, 599, 8, 20, 7, 0, true, 98, 29)]
        public void Calculate_UsesAvailableAreaAndFloors(
            double width, double height, double cellWidth, double cellHeight,
            double padding, double keyBarHeight, bool overlays,
            int expectedCols, int expectedRows)
        {
            GridSize result = GridSizeCalculator.Calculate(
                width, height, cellWidth, cellHeight, padding, keyBarHeight, overlays);

            Assert.Equal(expectedCols, result.Cols);
            Assert.Equal(expectedRows, result.Rows);
        }

        [Theory]
        [InlineData(0, 0, 6, 18, 0)]
        [InlineData(100, 40, 6, 18, 16)]
        [InlineData(-100, -100, 6, 18, -4)]
        public void Calculate_EnforcesMinimumGrid(
            double width, double height, double cellWidth, double cellHeight, double padding)
        {
            GridSize result = GridSizeCalculator.Calculate(
                width, height, cellWidth, cellHeight, padding);

            Assert.Equal(GridSizeCalculator.MinimumCols, result.Cols);
            Assert.Equal(GridSizeCalculator.MinimumRows, result.Rows);
        }

        [Fact]
        public void Calculate_LargerCellsRepresentLargerFont()
        {
            GridSize smallFont = GridSizeCalculator.Calculate(360, 640, 6, 18, 4, 44);
            GridSize largeFont = GridSizeCalculator.Calculate(360, 640, 9, 27, 4, 44);

            Assert.Equal(new GridSize(58, 32), smallFont);
            Assert.Equal(new GridSize(39, 21), largeFont);
        }

        [Fact]
        public void Calculate_RejectsInvalidCellMetrics()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                GridSizeCalculator.Calculate(360, 640, 0, 18, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                GridSizeCalculator.Calculate(360, 640, 6, double.NaN, 4));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                GridSizeCalculator.Calculate(360, 640, double.PositiveInfinity, 18, 4));
        }

        [Fact]
        public void Calculate_SubtractsInputPaneOcclusion()
        {
            GridSize without = GridSizeCalculator.Calculate(360, 640, 6, 18, 4, 0, true, 0);
            GridSize with = GridSizeCalculator.Calculate(360, 640, 6, 18, 4, 0, true, 266);

            Assert.Equal(new GridSize(58, 35), without);
            Assert.Equal(new GridSize(58, 20), with);
        }

        [Fact]
        public void GridSize_HasValueSemantics()
        {
            var left = new GridSize(80, 24);
            var right = new GridSize(80, 24);

            Assert.Equal(left, right);
            Assert.True(left == right);
            Assert.Equal("80x24", left.ToString());
        }
    }
}
