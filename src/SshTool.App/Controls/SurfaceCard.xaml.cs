using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Controls
{
    public enum SurfaceCardState
    {
        Normal,
        Pressed,
        Selected,
        Error
    }

    public sealed partial class SurfaceCard : UserControl
    {
        public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
            nameof(Header), typeof(string), typeof(SurfaceCard),
            new PropertyMetadata(string.Empty, OnChanged));
        // 命名避开 UserControl.Content（承载本控件根 Border），消费方写
        // <controls:SurfaceCard.CardContent>...</controls:SurfaceCard.CardContent>。
        public static readonly DependencyProperty CardContentProperty = DependencyProperty.Register(
            nameof(CardContent), typeof(object), typeof(SurfaceCard),
            new PropertyMetadata(null, OnChanged));
        public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
            nameof(State), typeof(SurfaceCardState), typeof(SurfaceCard),
            new PropertyMetadata(SurfaceCardState.Normal, OnChanged));

        public SurfaceCard()
        {
            this.InitializeComponent();
            // ui/fix-pass：代码赋值的主题画刷随 ThemeService.ThemeChanged 重算。
            ThemeRefreshHook.Attach(this, UpdateVisual);
            this.Loaded += (s, e) => UpdateVisual();
        }

        public string Header
        {
            get { return (string)GetValue(HeaderProperty); }
            set { SetValue(HeaderProperty, value); }
        }

        public object CardContent
        {
            get { return GetValue(CardContentProperty); }
            set { SetValue(CardContentProperty, value); }
        }

        public SurfaceCardState State
        {
            get { return (SurfaceCardState)GetValue(StateProperty); }
            set { SetValue(StateProperty, value); }
        }

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((SurfaceCard)d).UpdateVisual();
        }

        private void UpdateVisual()
        {
            string bgKey = "AppSurfaceBrush";
            string strokeKey = "AppBorderBrush";
            switch (State)
            {
                case SurfaceCardState.Pressed:
                    bgKey = "AppPressedBrush";
                    break;
                case SurfaceCardState.Selected:
                    strokeKey = "AppAccentBrush";
                    break;
                case SurfaceCardState.Error:
                    strokeKey = "AppDangerBrush";
                    break;
            }
            Root.Background = ThemeService.ResolveBrush(bgKey);
            Root.BorderBrush = ThemeService.ResolveBrush(strokeKey);

            bool hasHeader = !string.IsNullOrEmpty(Header);
            HeaderText.Text = Header ?? string.Empty;
            HeaderText.Visibility = hasHeader ? Visibility.Visible : Visibility.Collapsed;
            ContentSlot.Margin = hasHeader
                ? (Thickness)Application.Current.Resources["GapSmTop"]
                : (Thickness)Application.Current.Resources["PadNone"];
            ContentSlot.Content = CardContent;
        }
    }
}
