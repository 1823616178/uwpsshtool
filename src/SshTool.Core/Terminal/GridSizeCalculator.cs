using System;

namespace SshTool.Core.Terminal
{
    // T07：终端可视区换算出的 PTY 网格尺寸。
    public struct GridSize : IEquatable<GridSize>
    {
        public GridSize(int cols, int rows)
        {
            Cols = cols;
            Rows = rows;
        }

        public int Cols { get; }
        public int Rows { get; }

        public bool Equals(GridSize other)
        {
            return Cols == other.Cols && Rows == other.Rows;
        }

        public override bool Equals(object obj)
        {
            return obj is GridSize && Equals((GridSize)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Cols * 397) ^ Rows;
            }
        }

        public static bool operator ==(GridSize left, GridSize right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(GridSize left, GridSize right)
        {
            return !left.Equals(right);
        }

        public override string ToString()
        {
            return Cols + "x" + Rows;
        }
    }

    // 01-DESIGN.md §7.4：可视尺寸扣除 padding/覆盖式键条后，按单元格尺寸向下取整。
    public static class GridSizeCalculator
    {
        public const int MinimumCols = 20;
        public const int MinimumRows = 5;

        public static GridSize Calculate(double viewWidth, double viewHeight,
                                         double cellWidth, double cellHeight,
                                         double padding, double keyBarHeight = 0,
                                         bool keyBarOverlays = true)
        {
            RequirePositiveFinite(cellWidth, nameof(cellWidth));
            RequirePositiveFinite(cellHeight, nameof(cellHeight));

            double width = NonNegativeFinite(viewWidth);
            double height = NonNegativeFinite(viewHeight);
            double inset = NonNegativeFinite(padding);
            double coveredHeight = keyBarOverlays ? NonNegativeFinite(keyBarHeight) : 0;

            double availableWidth = Math.Max(0, width - (2 * inset));
            double availableHeight = Math.Max(0, height - (2 * inset) - coveredHeight);
            int cols = Math.Max(MinimumCols, FloorToInt(availableWidth / cellWidth));
            int rows = Math.Max(MinimumRows, FloorToInt(availableHeight / cellHeight));
            return new GridSize(cols, rows);
        }

        private static void RequirePositiveFinite(double value, string name)
        {
            if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(name);
            }
        }

        private static double NonNegativeFinite(double value)
        {
            if (value <= 0 || double.IsNaN(value))
            {
                return 0;
            }
            return double.IsPositiveInfinity(value) ? double.MaxValue : value;
        }

        private static int FloorToInt(double value)
        {
            if (value >= int.MaxValue)
            {
                return int.MaxValue;
            }
            return (int)Math.Floor(value);
        }
    }
}
