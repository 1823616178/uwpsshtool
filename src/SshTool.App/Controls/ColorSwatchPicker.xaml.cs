using System;
using System.Globalization;
using SshTool.Core.Validation;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace SshTool.App.Controls
{
    public sealed partial class ColorSwatchPicker : UserControl
    {
        // 02-UI-DESIGN / U04：24 预设色；默认组色 #4F8CFF 在第一位。
        private static readonly string[] Presets =
        {
            "#4F8CFF", "#F4605F", "#34C77B", "#F5A524", "#C37CE0", "#3EC6D9", "#2F6FE4", "#DC3F43",
            "#16A765", "#C9800C", "#7CADFF", "#FF8584", "#5BD896", "#FFBB4D", "#D6A0EC", "#6AD8E5",
            "#1A2233", "#12161F", "#6B7891", "#9AA7C0", "#E8EEFB", "#243048", "#000000", "#FFFFFF"
        };

        public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
            nameof(Color), typeof(string), typeof(ColorSwatchPicker),
            new PropertyMetadata("#4F8CFF", OnColorChanged));

        private bool _suppressHex;

        public ColorSwatchPicker()
        {
            this.InitializeComponent();
            BuildSwatches();
            this.Loaded += (s, e) => SyncHex();
        }

        public string Color
        {
            get { return (string)GetValue(ColorProperty); }
            set { SetValue(ColorProperty, value); }
        }

        public event EventHandler ColorChanged;

        private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((ColorSwatchPicker)d).SyncHex();
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
            if (_suppressHex)
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

        private void SyncHex()
        {
            _suppressHex = true;
            HexBox.Text = Color ?? string.Empty;
            HexError.Visibility = Visibility.Collapsed;
            _suppressHex = false;
        }

        private void RaiseChanged()
        {
            EventHandler handler = ColorChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private static Brush BrushFrom(string hex)
        {
            try
            {
                byte r = byte.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte g = byte.Parse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte b = byte.Parse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
            }
            catch (Exception)
            {
                return new SolidColorBrush(Colors.Transparent);
            }
        }
    }
}
