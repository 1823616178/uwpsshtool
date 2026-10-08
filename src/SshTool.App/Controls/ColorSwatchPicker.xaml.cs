using SshTool.App.Platform;
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
using SshTool.App.Infrastructure;
using SshTool.Core.Common;

namespace SshTool.App.Controls
{
    // A03 完整版：24 预设色；默认组色 #4F8CFF 在第一位。
    // UI 走查 S 组：色块即可触控（TouchTargetMin），选中态用强调色描边 + 「✓」角标，未选中不留描边。
    // UI 走查 R-C 分层：外层 Border 只做 40×40 触控区（无圆角、无可见填充），内层 Border 才是
    // 圆角色块，四周让出 SwatchGap 的间隙 —— 邻块之间不再无缝贴死，选中环也只压住自己那块。
    public sealed partial class ColorSwatchPicker : UserControl
    {
        private static readonly string[] Presets =
        {
            "#4F8CFF", "#F4605F", "#34C77B", "#F5A524", "#C37CE0", "#3EC6D9", "#2F6FE4", "#DC3F43",
            "#16A765", "#C9800C", "#7CADFF", "#FF8584", "#5BD896", "#FFBB4D", "#D6A0EC", "#6AD8E5",
            "#1A2233", "#12161F", "#6B7891", "#9AA7C0", "#E8EEFB", "#243048", "#000000", "#FFFFFF"
        };

        // 分层刻度：既是选中环的描边宽度，也是内层色块与外层触控格之间的间隙。
        // Themes/ 只有 1px（BorderThin）与 0（BorderNone），没有 2px 均匀档，故在此登记；
        // AppearanceEditPage 的 ANSI 调色板色块共用同值（内层圆角 + 让出间隙）。
        internal const double SwatchGap = 2;
        // 「✓」角标换成 MDL2 CheckMark 字形（V01a）：文本走 IconCheck token，字体走 AppIconFontFamily
        // （TextBlock 直出字形必须显式设 MDL2 字体族）；对比色随色块亮度取黑白，与主题无关。
        // 实例字段：Brush 是 DependencyObject，随构造在 UI 线程创建。
        private readonly Brush _onLightSwatch = new SolidColorBrush(Colors.Black);
        private readonly Brush _onDarkSwatch = new SolidColorBrush(Colors.White);
        // 外层触控格的透明填充：Background 为 null 时 Border 不参与命中测试，
        // 内层让出的 SwatchGap 环带就会漏掉触控；透明填充补齐整格 40×40，且无可见底色。
        private readonly Brush _hitTestFill = new SolidColorBrush(Colors.Transparent);

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
            // ui/fix-pass：代码赋值的主题画刷随 ThemeService.ThemeChanged 重算。
            ThemeRefreshHook.Attach(this, SyncSelection);
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
            double target = (double)Application.Current.Resources["TouchTargetMin"];
            Thickness noGap = (Thickness)Application.Current.Resources["BorderNone"];
            Thickness gap = new Thickness(SwatchGap);
            CornerRadius radius = (CornerRadius)Application.Current.Resources["RadiusSm"];
            double glyphSize = (double)Application.Current.Resources["FontCaption"];
            string checkGlyph = (string)Application.Current.Resources["IconCheck"];
            FontFamily iconFont = (FontFamily)Application.Current.Resources["AppIconFontFamily"];
            for (int i = 0; i < Presets.Length; i++)
            {
                string hex = Presets[i];
                var check = new TextBlock
                {
                    Text = checkGlyph,
                    FontFamily = iconFont,
                    FontSize = glyphSize,
                    Foreground = _onDarkSwatch,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Visibility = Visibility.Collapsed,
                    IsHitTestVisible = false
                };
                // 内层：圆角色块本体，四周让出 gap 与邻块分开；选中环画在这一层。
                var swatch = new Border
                {
                    Background = BrushFrom(hex),
                    Child = check,
                    CornerRadius = radius,
                    Margin = gap,
                    BorderThickness = noGap,
                    // 与外层同值 Tag：Tapped 冒泡后无论 sender 解析到哪一层 Border 都取得到色值，
                    // 点击回调无需改动。
                    Tag = hex
                };
                // 外层：纯触控格（无圆角、无可见填充），Tapped 与色值都挂这一层，
                // 因此点击回调与 Children[i] 的索引语义不变。
                var cell = new Border
                {
                    Background = _hitTestFill,
                    Child = swatch,
                    MinWidth = target,
                    MinHeight = target,
                    Margin = noGap,
                    BorderThickness = noGap,
                    Tag = hex
                };
                cell.Tapped += OnSwatchTapped;
                SwatchGrid.Children.Add(cell);
            }
            SyncSelection();
        }

        // 选中态：内层色块加强调色 2 描边 + 「✓」；未选中无描边。
        private void SyncSelection()
        {
            // AppAccentBrush 只在 Tokens.*.xaml 的 ThemeDictionaries 里，
            // ResourceDictionary 索引器不查主题字典（恒 null）——走 Banner 的解析。
            Brush accent = ThemeService.ResolveBrush("AppAccentBrush");
            Thickness stroke = new Thickness(SwatchGap);
            Thickness noGap = (Thickness)Application.Current.Resources["BorderNone"];
            for (int i = 0; i < SwatchGrid.Children.Count; i++)
            {
                var cell = SwatchGrid.Children[i] as Border;
                if (cell == null)
                {
                    continue;
                }
                var swatch = cell.Child as Border;
                if (swatch == null)
                {
                    continue;
                }
                string hex = cell.Tag as string;
                bool selected = !string.IsNullOrEmpty(Color)
                    && string.Equals(hex, Color, StringComparison.OrdinalIgnoreCase);
                swatch.BorderBrush = selected ? accent : null;
                swatch.BorderThickness = selected ? stroke : noGap;
                var check = swatch.Child as TextBlock;
                if (check != null)
                {
                    check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
                    check.Foreground = ContrastGlyph(hex);
                }
            }
        }

        // 角标压在任意色块上都要可辨：按色块亮度取纯黑/纯白，不跟随主题。
        private Brush ContrastGlyph(string hex)
        {
            byte r;
            byte g;
            byte b;
            if (!TryParse(hex, out r, out g, out b))
            {
                return _onDarkSwatch;
            }
            int luminance = (r * 299 + g * 587 + b * 114) / 1000;
            return luminance >= 150 ? _onLightSwatch : _onDarkSwatch;
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
                HexError.Text = Localized.Get("ColorSwatch_HexError", "颜色须为 #RRGGBB 形式");
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
                SyncSelection();
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

        // O14：解析收口到 Core 的 ColorHex（此前本文件与 GroupHeader、
        // AppearanceListViewModel 三处逐字重复同一段 byte.Parse + 回退）。
        private static bool TryParse(string hex, out byte r, out byte g, out byte b)
        {
            return ColorHex.TryParse(hex, out r, out g, out b);
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
