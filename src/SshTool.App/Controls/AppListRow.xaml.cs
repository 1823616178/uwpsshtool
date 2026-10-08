using System;
using System.Windows.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using SshTool.App.Infrastructure;

namespace SshTool.App.Controls
{
    public enum AppListRowState
    {
        Normal,
        Pressed,
        Selected,
        Disabled
    }

    public sealed partial class AppListRow : UserControl
    {
        public static readonly DependencyProperty IconGlyphProperty = DependencyProperty.Register(
            nameof(IconGlyph), typeof(string), typeof(AppListRow),
            new PropertyMetadata(string.Empty, OnChanged));
        public static readonly DependencyProperty IconContentProperty = DependencyProperty.Register(
            nameof(IconContent), typeof(object), typeof(AppListRow),
            new PropertyMetadata(null, OnChanged));
        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
            nameof(Title), typeof(string), typeof(AppListRow),
            new PropertyMetadata(string.Empty, OnChanged));
        public static readonly DependencyProperty TitleBadgeGlyphProperty = DependencyProperty.Register(
            nameof(TitleBadgeGlyph), typeof(string), typeof(AppListRow),
            new PropertyMetadata(string.Empty, OnChanged));
        public static readonly DependencyProperty TitleBadgeBrushProperty = DependencyProperty.Register(
            nameof(TitleBadgeBrush), typeof(Brush), typeof(AppListRow),
            new PropertyMetadata(null, OnChanged));
        public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
            nameof(Subtitle), typeof(string), typeof(AppListRow),
            new PropertyMetadata(string.Empty, OnChanged));
        public static readonly DependencyProperty SubtitleStyleProperty = DependencyProperty.Register(
            nameof(SubtitleStyle), typeof(Style), typeof(AppListRow),
            new PropertyMetadata(null, OnChanged));
        public static readonly DependencyProperty SubtitleHorizontalAlignmentProperty = DependencyProperty.Register(
            nameof(SubtitleHorizontalAlignment), typeof(HorizontalAlignment), typeof(AppListRow),
            new PropertyMetadata(HorizontalAlignment.Left, OnChanged));
        public static readonly DependencyProperty StatusContentProperty = DependencyProperty.Register(
            nameof(StatusContent), typeof(object), typeof(AppListRow),
            new PropertyMetadata(null, OnChanged));
        public static readonly DependencyProperty TrailingContentProperty = DependencyProperty.Register(
            nameof(TrailingContent), typeof(object), typeof(AppListRow),
            new PropertyMetadata(null, OnChanged));
        public static readonly DependencyProperty ShowChevronProperty = DependencyProperty.Register(
            nameof(ShowChevron), typeof(bool), typeof(AppListRow),
            new PropertyMetadata(false, OnChanged));
        public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
            nameof(IsCompact), typeof(bool), typeof(AppListRow),
            new PropertyMetadata(false, OnChanged));
        public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
            nameof(State), typeof(AppListRowState), typeof(AppListRow),
            new PropertyMetadata(AppListRowState.Normal, OnChanged));
        // opt/full-pass 竖屏重设计：卡片外观（圆角 + 发丝描边 + 渐变卡底 + 卡片间距）。
        public static readonly DependencyProperty IsCardProperty = DependencyProperty.Register(
            nameof(IsCard), typeof(bool), typeof(AppListRow),
            new PropertyMetadata(false, OnChanged));
        public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
            nameof(Command), typeof(ICommand), typeof(AppListRow),
            new PropertyMetadata(null));

        // 指针按压的即时反馈，与 State="Pressed"（静态指定，如画廊）叠加生效。
        private bool _pointerPressed;

        public event EventHandler Click;

        public AppListRow()
        {
            this.InitializeComponent();
            this.Loaded += (s, e) => UpdateVisual();
            this.IsEnabledChanged += (s, e) => UpdateVisual();
        }

        // MDL2 字形串（消费方写 {StaticResource IconXxx}）；IconContent 非空时槽内容优先。
        public string IconGlyph
        {
            get { return (string)GetValue(IconGlyphProperty); }
            set { SetValue(IconGlyphProperty, value); }
        }

        public object IconContent
        {
            get { return GetValue(IconContentProperty); }
            set { SetValue(IconContentProperty, value); }
        }

        public string Title
        {
            get { return (string)GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value); }
        }

        public string TitleBadgeGlyph
        {
            get { return (string)GetValue(TitleBadgeGlyphProperty); }
            set { SetValue(TitleBadgeGlyphProperty, value); }
        }

        public Brush TitleBadgeBrush
        {
            get { return (Brush)GetValue(TitleBadgeBrushProperty); }
            set { SetValue(TitleBadgeBrushProperty, value); }
        }

        public string Subtitle
        {
            get { return (string)GetValue(SubtitleProperty); }
            set { SetValue(SubtitleProperty, value); }
        }

        public Style SubtitleStyle
        {
            get { return (Style)GetValue(SubtitleStyleProperty); }
            set { SetValue(SubtitleStyleProperty, value); }
        }

        public HorizontalAlignment SubtitleHorizontalAlignment
        {
            get { return (HorizontalAlignment)GetValue(SubtitleHorizontalAlignmentProperty); }
            set { SetValue(SubtitleHorizontalAlignmentProperty, value); }
        }

        // 状态槽：放 StatusPill 或 StatusDot。
        public object StatusContent
        {
            get { return GetValue(StatusContentProperty); }
            set { SetValue(StatusContentProperty, value); }
        }

        // 尾操作槽：放单个按钮；为空且 ShowChevron=true 时显示右尖括号。
        public object TrailingContent
        {
            get { return GetValue(TrailingContentProperty); }
            set { SetValue(TrailingContentProperty, value); }
        }

        public bool ShowChevron
        {
            get { return (bool)GetValue(ShowChevronProperty); }
            set { SetValue(ShowChevronProperty, value); }
        }

        // false=ListRowHeight（64），true=ListRowCompactHeight（48）。
        public bool IsCompact
        {
            get { return (bool)GetValue(IsCompactProperty); }
            set { SetValue(IsCompactProperty, value); }
        }

        public bool IsCard
        {
            get { return (bool)GetValue(IsCardProperty); }
            set { SetValue(IsCardProperty, value); }
        }

        public AppListRowState State
        {
            get { return (AppListRowState)GetValue(StateProperty); }
            set { SetValue(StateProperty, value); }
        }

        public ICommand Command
        {
            get { return (ICommand)GetValue(CommandProperty); }
            set { SetValue(CommandProperty, value); }
        }

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((AppListRow)d).UpdateVisual();
        }

        private bool IsDisabled
        {
            get { return State == AppListRowState.Disabled || !IsEnabled; }
        }

        private void UpdateVisual()
        {
            bool hasIconContent = IconContent != null;
            IconSlot.Content = IconContent;
            IconSlot.Visibility = hasIconContent ? Visibility.Visible : Visibility.Collapsed;
            bool hasGlyph = !hasIconContent && !string.IsNullOrEmpty(IconGlyph);
            LeadingIcon.Glyph = IconGlyph ?? string.Empty;
            LeadingIcon.Visibility = hasGlyph ? Visibility.Visible : Visibility.Collapsed;

            var gapLeft = (Thickness)Application.Current.Resources["GapSmLeft"];
            var noPad = (Thickness)Application.Current.Resources["PadNone"];
            TextPanel.Margin = (hasIconContent || hasGlyph) ? gapLeft : noPad;

            if (!string.IsNullOrEmpty(TitleBadgeGlyph))
            {
                TitleText.Text = string.Empty;
                TitleText.Inlines.Clear();
                TitleText.Inlines.Add(new Run { Text = Title ?? string.Empty });
                TitleText.Inlines.Add(new Run { Text = " " });
                TitleText.Inlines.Add(new Run
                {
                    Text = TitleBadgeGlyph,
                    FontFamily = (FontFamily)Application.Current.Resources["AppIconFontFamily"],
                    FontSize = (double)Application.Current.Resources["FontCaption"],
                    Foreground = TitleBadgeBrush ?? (Brush)Application.Current.Resources["AppWarningBrush"]
                });
            }
            else
            {
                TitleText.Inlines.Clear();
                TitleText.Text = Title ?? string.Empty;
            }
            SubtitleText.Style = SubtitleStyle ?? (Style)Application.Current.Resources["CaptionTextStyle"];
            SubtitleText.HorizontalAlignment = SubtitleHorizontalAlignment;
            SubtitleText.Text = Subtitle ?? string.Empty;
            SubtitleText.Visibility = string.IsNullOrEmpty(Subtitle) ? Visibility.Collapsed : Visibility.Visible;

            bool hasStatus = StatusContent != null;
            StatusSlot.Content = StatusContent;
            StatusSlot.Visibility = hasStatus ? Visibility.Visible : Visibility.Collapsed;
            StatusSlot.Margin = hasStatus ? gapLeft : noPad;

            bool hasTrailing = TrailingContent != null;
            TrailingSlot.Content = TrailingContent;
            TrailingSlot.Visibility = hasTrailing ? Visibility.Visible : Visibility.Collapsed;
            TrailingSlot.Margin = hasTrailing ? gapLeft : noPad;

            bool showChevron = !hasTrailing && ShowChevron;
            ChevronIcon.Visibility = showChevron ? Visibility.Visible : Visibility.Collapsed;
            ChevronIcon.Margin = showChevron ? gapLeft : noPad;

            Root.MinHeight = (double)Application.Current.Resources[
                (IsCompact || InteractionModeHelper.IsMouseMode) ? "ListRowCompactHeight" : "ListRowHeight"];

            bool disabled = IsDisabled;
            Root.Opacity = disabled ? (double)Application.Current.Resources["DisabledOpacity"] : 1.0;
            // State=Disabled 时 IsEnabled 仍为 true，显式断掉整块命中（含槽内按钮）。
            Root.IsHitTestVisible = !disabled;

            ApplyCardChrome();
            ApplyStateBrush();
            // §7.2：行作为整体进无障碍树，名称取可见标题（标题 TextBlock 已标 Raw）。
            AutomationProperties.SetName(this, Title ?? string.Empty);
        }

        private void ApplyCardChrome()
        {
            ResourceDictionary res = Application.Current.Resources;
            if (IsCard)
            {
                Root.CornerRadius = (CornerRadius)res["RadiusLg"];
                Root.BorderThickness = (Thickness)res["BorderThin"];
                Root.BorderBrush = Banner.ResolveThemedBrush("AppCardStrokeBrush");
                Root.Margin = (Thickness)res["CardMargin"];
            }
            else
            {
                Root.CornerRadius = new CornerRadius();
                Root.BorderThickness = (Thickness)res["BorderNone"];
                Root.BorderBrush = null;
                Root.Margin = (Thickness)res["PadNone"];
            }
        }

        private void ApplyStateBrush()
        {
            bool card = IsCard;
            string brushKey = card ? "AppCardBrush" : "AppSurfaceBrush";
            if (!IsDisabled && (_pointerPressed || State == AppListRowState.Pressed))
            {
                brushKey = card ? "AppCardPressedBrush" : "AppPressedBrush";
            }
            else if (State == AppListRowState.Selected)
            {
                brushKey = card ? "AppCardSelectedBrush" : "AppSurfaceAltBrush";
            }
            Root.Background = Banner.ResolveThemedBrush(brushKey);
        }

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (IsDisabled)
            {
                return;
            }
            _pointerPressed = true;
            ApplyStateBrush();
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _pointerPressed = false;
            ApplyStateBrush();
        }

        private void OnPointerExited(object sender, PointerRoutedEventArgs e)
        {
            _pointerPressed = false;
            ApplyStateBrush();
        }

        private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _pointerPressed = false;
            ApplyStateBrush();
        }

        private void OnTapped(object sender, TappedRoutedEventArgs e)
        {
            if (IsDisabled)
            {
                e.Handled = true;
                return;
            }
            // 尾槽内容自行处理点击，这里按来源整槽排除：ButtonBase 的类处理标记的是
            // PointerPressed 而非 Tapped（UIElement.Tapped 备注明确「控件处理 PointerPressed
            // 并不阻止 Tapped 触发」，不触发 Tapped 的控件仅 PasswordBox/RichEditBox/TextBox），
            // 裸 FontIcon/Hyperlink 等也不吞事件——两类内容的点按都会冒泡到本处理器，
            // 故统一靠 IsWithin 溯源兜底，与尾内容是否为 ButtonBase 无关。
            if (IsWithin(e.OriginalSource as DependencyObject, TrailingSlot))
            {
                return;
            }
            // 事件先于 command 触发；消费方只接其一，避免双执行（同 Banner.ActionClick）。
            EventHandler handler = Click;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
            if (Command != null && Command.CanExecute(null))
            {
                Command.Execute(null);
            }
        }

        private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
        {
            while (element != null)
            {
                if (element == ancestor)
                {
                    return true;
                }
                element = VisualTreeHelper.GetParent(element);
            }
            return false;
        }
    }
}
