using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Controls
{
    // Art：多行等宽 randomart 字符串，原样按等宽字形展示。
    public sealed partial class RandomArtView : UserControl
    {
        public static readonly DependencyProperty ArtProperty = DependencyProperty.Register(
            nameof(Art), typeof(string), typeof(RandomArtView),
            new PropertyMetadata(string.Empty, OnArtChanged));

        public RandomArtView()
        {
            this.InitializeComponent();
            this.Loaded += (s, e) => UpdateVisual();
        }

        public string Art
        {
            get { return (string)GetValue(ArtProperty); }
            set { SetValue(ArtProperty, value); }
        }

        private static void OnArtChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((RandomArtView)d).UpdateVisual();
        }

        private void UpdateVisual()
        {
            ArtText.Text = Art ?? string.Empty;
        }
    }
}
