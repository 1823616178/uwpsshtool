using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    public sealed partial class KnownHostsPage : Page
    {
        public KnownHostsPage()
        {
            ViewModel = new KnownHostsViewModel(AppServices.Current);
            this.InitializeComponent();
            HostList.ItemsSource = ViewModel.Rows;
        }

        public KnownHostsViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var ignore = ViewModel.RefreshAsync();
        }

        private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                ViewModel.Search = sender.Text;
            }
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = HostList.SelectedItem as KnownHostRow;
            ViewModel.Selected = row;
            if (row == null)
            {
                Detail.Visibility = Visibility.Collapsed;
                return;
            }
            FingerprintText.Text = row.Fingerprint;
            ArtView.Art = string.IsNullOrEmpty(row.RandomArt) ? "（无 randomart）" : row.RandomArt;
            Detail.Visibility = Visibility.Visible;
        }

        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            var ignore = ViewModel.DeleteSelectedAsync();
        }
    }
}
