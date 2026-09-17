using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Controls
{
    // Compact=True 换 Caption 字号（randomart 等密集内容）。
    public sealed partial class MonoText : UserControl
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(MonoText),
            new PropertyMetadata(string.Empty, OnVisualChanged));
        public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
            nameof(Compact), typeof(bool), typeof(MonoText),
            new PropertyMetadata(false, OnVisualChanged));

        public MonoText()
        {
            this.InitializeComponent();
            this.Loaded += (s, e) => UpdateVisual();
        }

        public string Text
        {
            get { return (string)GetValue(TextProperty); }
            set { SetValue(TextProperty, value); }
        }

        public bool Compact
        {
            get { return (bool)GetValue(CompactProperty); }
            set { SetValue(CompactProperty, value); }
        }

        private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((MonoText)d).UpdateVisual();
        }

        private void UpdateVisual()
        {
            InnerText.Text = Text ?? string.Empty;
            InnerText.Style = (Style)Application.Current.Resources[Compact ? "MonoCaptionTextStyle" : "MonoTextStyle"];
        }
    }
}
