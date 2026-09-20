using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Controls
{
    public enum StatusPillKind
    {
        Neutral,
        Success,
        Warning,
        Danger
    }

    public sealed partial class StatusPill : UserControl
    {
        public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
            nameof(Kind), typeof(StatusPillKind), typeof(StatusPill),
            new PropertyMetadata(StatusPillKind.Neutral, OnChanged));
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(StatusPill),
            new PropertyMetadata(string.Empty, OnChanged));

        public StatusPill()
        {
            this.InitializeComponent();
            this.Loaded += (s, e) => UpdateVisual();
        }

        public StatusPillKind Kind
        {
            get { return (StatusPillKind)GetValue(KindProperty); }
            set { SetValue(KindProperty, value); }
        }

        public string Text
        {
            get { return (string)GetValue(TextProperty); }
            set { SetValue(TextProperty, value); }
        }

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((StatusPill)d).UpdateVisual();
        }

        private void UpdateVisual()
        {
            string brushKey;
            switch (Kind)
            {
                case StatusPillKind.Success:
                    brushKey = "AppSuccessBrush";
                    break;
                case StatusPillKind.Warning:
                    brushKey = "AppWarningBrush";
                    break;
                case StatusPillKind.Danger:
                    brushKey = "AppDangerBrush";
                    break;
                default:
                    brushKey = "AppTextFaintBrush";
                    break;
            }
            Dot.Fill = Banner.ResolveThemedBrush(brushKey);
            Label.Text = Text ?? string.Empty;
            // §7.2：状态不能只靠颜色——读屏内容取可见文本。
            AutomationProperties.SetName(this, Text ?? string.Empty);
        }
    }
}
