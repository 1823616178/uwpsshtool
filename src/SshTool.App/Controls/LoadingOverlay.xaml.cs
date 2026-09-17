using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Controls
{
    public sealed partial class LoadingOverlay : UserControl
    {
        public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
            nameof(IsActive), typeof(bool), typeof(LoadingOverlay),
            new PropertyMetadata(false, OnChanged));
        public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
            nameof(Message), typeof(string), typeof(LoadingOverlay),
            new PropertyMetadata(string.Empty, OnChanged));

        public LoadingOverlay()
        {
            this.InitializeComponent();
            this.Loaded += (s, e) => UpdateVisual();
        }

        public bool IsActive
        {
            get { return (bool)GetValue(IsActiveProperty); }
            set { SetValue(IsActiveProperty, value); }
        }

        public string Message
        {
            get { return (string)GetValue(MessageProperty); }
            set { SetValue(MessageProperty, value); }
        }

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((LoadingOverlay)d).UpdateVisual();
        }

        private void UpdateVisual()
        {
            Overlay.Visibility = IsActive ? Visibility.Visible : Visibility.Collapsed;
            Ring.IsActive = IsActive;
            MessageText.Text = Message ?? string.Empty;
            MessageText.Visibility = string.IsNullOrEmpty(Message) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
