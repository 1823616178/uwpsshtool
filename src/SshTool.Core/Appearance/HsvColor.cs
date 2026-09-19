using System;

namespace SshTool.Core.Appearance
{
    // A03：HSV 颜色数学（ColorSwatchPicker 三滑块用）。纯函数放 Core 以便单测；
    // App 层只做 Windows.UI.Color ↔ byte 搬运。C# 7.3：不用 readonly struct。
    public struct HsvColor
    {
        public double H; // 0–360
        public double S; // 0–1
        public double V; // 0–1

        public HsvColor(double h, double s, double v)
        {
            H = h;
            S = s;
            V = v;
        }

        public static HsvColor FromRgb(byte r, byte g, byte b)
        {
            double rd = r / 255.0;
            double gd = g / 255.0;
            double bd = b / 255.0;
            double max = Math.Max(rd, Math.Max(gd, bd));
            double min = Math.Min(rd, Math.Min(gd, bd));
            double delta = max - min;

            double h = 0;
            if (delta > 0)
            {
                if (max == rd)
                {
                    h = 60 * (((gd - bd) / delta) % 6);
                }
                else if (max == gd)
                {
                    h = 60 * (((bd - rd) / delta) + 2);
                }
                else
                {
                    h = 60 * (((rd - gd) / delta) + 4);
                }
                if (h < 0)
                {
                    h += 360;
                }
            }
            double s = max <= 0 ? 0 : delta / max;
            return new HsvColor(h, s, max);
        }

        public static void ToRgb(HsvColor hsv, out byte r, out byte g, out byte b)
        {
            double h = hsv.H;
            double s = hsv.S;
            double v = hsv.V;
            if (double.IsNaN(h) || double.IsInfinity(h))
            {
                h = 0;
            }
            h = h % 360;
            if (h < 0)
            {
                h += 360;
            }
            s = Clamp01(s);
            v = Clamp01(v);

            double c = v * s;
            double x = c * (1 - Math.Abs(((h / 60) % 2) - 1));
            double m = v - c;
            double rd = 0;
            double gd = 0;
            double bd = 0;
            if (h < 60)
            {
                rd = c; gd = x;
            }
            else if (h < 120)
            {
                rd = x; gd = c;
            }
            else if (h < 180)
            {
                gd = c; bd = x;
            }
            else if (h < 240)
            {
                gd = x; bd = c;
            }
            else if (h < 300)
            {
                rd = x; bd = c;
            }
            else
            {
                rd = c; bd = x;
            }
            r = (byte)Math.Round((rd + m) * 255, MidpointRounding.AwayFromZero);
            g = (byte)Math.Round((gd + m) * 255, MidpointRounding.AwayFromZero);
            b = (byte)Math.Round((bd + m) * 255, MidpointRounding.AwayFromZero);
        }

        private static double Clamp01(double value)
        {
            if (double.IsNaN(value))
            {
                return 0;
            }
            if (value < 0)
            {
                return 0;
            }
            if (value > 1)
            {
                return 1;
            }
            return value;
        }
    }
}
