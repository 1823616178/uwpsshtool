using System;
using System.Globalization;
using SshTool.Core.Appearance;
using SshTool.Core.Validation;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace SshTool.App.Controls
{
    // A03 完整版：24 预设色；默认组色 #4F8CFF 在第一位。
    public sealed partial class ColorSwatchPicker : UserControl
    {
        private static readonly string[] Presets =
        {
            "#4F8CFF", "#F4605F", "#34C77B", "#F5A524", "#C37CE0", "#3EC6D9", "#2F6FE4", "#DC3F43",
            "#16A765", "#C9800C", "#7CADFF", "#FF8584", "#5BD896", "#FFBB4D", "#D6A0EC", "#6AD8E5",
            "#1A2233", "#12161F", "#6B7891", "#9AA7C0", "#E8EEFB", "#243048", "#000000", "#FFFFFF"
        };

        public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
            nameof(Color), typeof(string), typeof(ColorSwatchPicker),
            new PropertyMetadata("#4F8CFF", OnColorChanged));

        public static readonly DependencyProperty OriginalColorProperty = DependencyProperty.Register(
            nameof(OriginalColor), typeof(string), typeof(ColorSwatchPicker),
            new PropertyMetadata(null, OnOriginalColorChanged));

        private bool _suppress;

        public ColorSwatchPicker()
        {
            this.InitializeComponent();
            HueSlider.Minimum = 0;
            HueSlider.Maximum = 360;
            SaturationSlider.Minimum = 0;
            SaturationSlider.Maximum = 100;
            ValueSlider.Minimum = 0;
            ValueSlider.Maximum = 100;
            BuildSwatches();
            this.Loaded += (s, e) => SyncAll();
        }

        public string Color
        {
            get { return (string)GetValue(ColorProperty); }
            set { SetValue(ColorProperty, value); }
        }

        // 新旧对比的「旧」；null 时显示当前色。
        public string OriginalColor
        {
            get { return (string)GetValue(OriginalColorProperty); }
            set { SetValue(OriginalColorProperty, value); }
        }

        public event EventHandler ColorChanged;

        private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((ColorSwatchPicker)d).SyncAll();
        }

        private static void OnOriginalColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((ColorSwatchPicker)d).SyncCompare();
        }

        private void BuildSwatches()
        {
            SwatchGrid.Children.Clear();
            for (int i = 0; i < Presets.Length; i++)
            {
                string hex = Presets[i];
                var border = new Border
                {
                    Background = BrushFrom(hex),
                    Margin = (Thickness)Application.Current.Resources["BorderThin"],
                    Tag = hex
                };
                border.Tapped += OnSwatchTapped;
                SwatchGrid.Children.Add(border);
            }
        }

        private void OnSwatchTapped(object sender, TappedRoutedEventArgs e)
        {
            var border = sender as Border;
            if (border == null)
            {
                return;
            }
            Color = (string)border.Tag;
            RaiseChanged();
        }

        private void OnHexChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppress)
            {
                return;
            }
            string text = HexBox.Text == null ? string.Empty : HexBox.Text.Trim();
            if (!text.StartsWith("#", StringComparison.Ordinal) && text.Length == 6)
            {
                text = "#" + text;
            }
            if (GroupValidator.IsValidColor(text))
            {
                HexError.Visibility = Visibility.Collapsed;
                Color = text.ToUpperInvariant();
                RaiseChanged();
            }
            else
            {
                HexError.Text = "颜色须为 #RRGGBB 形式";
                HexError.Visibility = Visibility.Visible;
            }
        }

        private void OnHsvChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppress)
            {
                return;
            }
            var hsv = new HsvColor(HueSlider.Value, SaturationSlider.Value / 100.0, ValueSlider.Value / 100.0);
            byte r;
            byte g;
            byte b;
            HsvColor.ToRgb(hsv, out r, out g, out b);
            Color = HexOf(r, g, b);
            RaiseChanged();
        }

        private void SyncAll()
        {
            _suppress = true;
            try
            {
                HexBox.Text = Color ?? string.Empty;
                HexError.Visibility = Visibility.Collapsed;
                byte r;
                byte g;
                byte b;
                if (TryParse(Color, out r, out g, out b))
                {
                    HsvColor hsv = HsvColor.FromRgb(r, g, b);
                    HueSlider.Value = hsv.H;
                    SaturationSlider.Value = hsv.S * 100.0;
                    ValueSlider.Value = hsv.V * 100.0;
                    UpdateLabels(hsv);
                }
                SyncCompare();
            }
            finally
            {
                _suppress = false;
            }
        }

        private void SyncCompare()
        {
            if (OldSwatch != null)
            {
                string old = string.IsNullOrEmpty(OriginalColor) ? Color : OriginalColor;
                OldSwatch.Background = BrushFrom(old);
            }
            if (NewSwatch != null)
            {
                NewSwatch.Background = BrushFrom(Color);
            }
        }

        private void UpdateLabels(HsvColor hsv)
        {
            if (HueLabel != null)
            {
                HueLabel.Text = "H " + ((int)Math.Round(hsv.H)).ToString(CultureInfo.InvariantCulture);
            }
            if (SaturationLabel != null)
            {
                SaturationLabel.Text = "S " + ((int)Math.Round(hsv.S * 100)).ToString(CultureInfo.InvariantCulture);
            }
            if (ValueLabel != null)
            {
                ValueLabel.Text = "V " + ((int)Math.Round(hsv.V * 100)).ToString(CultureInfo.InvariantCulture);
            }
        }

        private void RaiseChanged()
        {
            SyncAll();
            EventHandler handler = ColorChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private static string HexOf(byte r, byte g, byte b)
        {
            return "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2");
        }

        private static bool TryParse(string hex, out byte r, out byte g, out byte b)
        {
            r = 0;
            g = 0;
            b = 0;
            if (string.IsNullOrEmpty(hex) || hex.Length != 7 || hex[0] != '#')
            {
                return false;
            }
            try
            {
                r = byte.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                g = byte.Parse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                b = byte.Parse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static Brush BrushFrom(string hex)
        {
            byte r;
            byte g;
            byte b;
            if (!TryParse(hex, out r, out g, out b))
            {
                return new SolidColorBrush(Colors.Transparent);
            }
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
        }
    }
}
