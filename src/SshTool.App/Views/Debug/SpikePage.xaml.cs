using SshTool.Core.Spikes;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Views.Debug
{
    public sealed partial class SpikePage : Page
    {
        public SpikePage()
        {
            this.InitializeComponent();
        }

        private void OnRunClick(object sender, RoutedEventArgs e)
        {
            ReportText.Text = JsonSpike.RoundTrip();
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }
    }
}
