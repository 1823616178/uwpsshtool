using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Controls
{
    public enum AppPageHeaderLevel
    {
        // 一级页面（FontHeader 28）
        Primary,
        // 编辑页（FontTitle 20）
        Secondary
    }

    public sealed partial class AppPageHeader : UserControl
    {
        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
            nameof(Title), typeof(string), typeof(AppPageHeader),
            new PropertyMetadata(string.Empty, OnChanged));
        public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
            nameof(Level), typeof(AppPageHeaderLevel), typeof(AppPageHeader),
            new PropertyMetadata(AppPageHeaderLevel.Primary, OnChanged));
        public static readonly DependencyProperty ShowBackButtonProperty = DependencyProperty.Register(
            nameof(ShowBackButton), typeof(bool), typeof(AppPageHeader),
            new PropertyMetadata(false, OnChanged));
        public static readonly DependencyProperty StatusContentProperty = DependencyProperty.Register(
            nameof(StatusContent), typeof(object), typeof(AppPageHeader),
            new PropertyMetadata(null, OnChanged));
        public static readonly DependencyProperty PrimaryActionContentProperty = DependencyProperty.Register(
            nameof(PrimaryActionContent), typeof(object), typeof(AppPageHeader),
            new PropertyMetadata(null, OnChanged));

        public event EventHandler BackRequested;

        public AppPageHeader()
        {
            this.InitializeComponent();
            this.Loaded += (s, e) => UpdateVisual();
            this.SizeChanged += (s, e) => UpdateVisual();
        }

        public string Title
        {
            get { return (string)GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value); }
        }

        public AppPageHeaderLevel Level
        {
            get { return (AppPageHeaderLevel)GetValue(LevelProperty); }
            set { SetValue(LevelProperty, value); }
        }

        public bool ShowBackButton
        {
            get { return (bool)GetValue(ShowBackButtonProperty); }
            set { SetValue(ShowBackButtonProperty, value); }
        }

        public object StatusContent
        {
            get { return GetValue(StatusContentProperty); }
            set { SetValue(StatusContentProperty, value); }
        }

        // 主操作槽：每页最多一个强强调操作（05 §5.1），其余动作请走溢出菜单。
        public object PrimaryActionContent
        {
            get { return GetValue(PrimaryActionContentProperty); }
            set { SetValue(PrimaryActionContentProperty, value); }
        }

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((AppPageHeader)d).UpdateVisual();
        }

        private void UpdateVisual()
        {
            BackButton.Visibility = ShowBackButton ? Visibility.Visible : Visibility.Collapsed;
            TitleText.Text = Title ?? string.Empty;
            TitleText.Margin = ShowBackButton
                ? (Thickness)Application.Current.Resources["GapSmLeft"]
                : (Thickness)Application.Current.Resources["PadNone"];

            StatusSlot.Content = StatusContent;
            StatusSlot.Visibility = StatusContent != null ? Visibility.Visible : Visibility.Collapsed;
            ActionSlot.Content = PrimaryActionContent;
            ActionSlot.Visibility = PrimaryActionContent != null ? Visibility.Visible : Visibility.Collapsed;

            ApplyResponsive();
        }

        // compact / normal / wide：按控件实际宽度（≈ 容器宽，页头一般通栏）。
        private void ApplyResponsive()
        {
            double width = ActualWidth;
            double wideBreakpoint = (double)Application.Current.Resources["WideBreakpoint"];
            double extraWideBreakpoint = (double)Application.Current.Resources["ExtraWideBreakpoint"];
            bool wide = width >= extraWideBreakpoint;
            bool compact = width > 0 && width < wideBreakpoint;

            RootGrid.Padding = wide
                ? (Thickness)Application.Current.Resources["PagePaddingWide"]
                : (Thickness)Application.Current.Resources["PagePadding"];

            double size;
            if (Level == AppPageHeaderLevel.Primary)
            {
                size = (double)Application.Current.Resources[compact ? "FontTitle" : "FontHeader"];
            }
            else
            {
                size = (double)Application.Current.Resources[compact ? "FontSubtitle" : "FontTitle"];
            }
            TitleText.FontSize = size;
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            EventHandler handler = BackRequested;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
