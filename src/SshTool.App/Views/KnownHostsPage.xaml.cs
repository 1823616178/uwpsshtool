using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.App.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    public sealed partial class KnownHostsPage : Page
    {
        // W06：页头返回按钮（与硬件返回键同一条处理链）。
        private void OnHeaderBackRequested(object sender, System.EventArgs e)
        {
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.RequestBack();
            }
        }

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
            ViewModel.RefreshAsync().Forget("KnownHostsPage.Refresh", AppLog.Logger);
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
            ViewModel.DeleteSelectedAsync().Forget("KnownHostsPage.DeleteSelected", AppLog.Logger);
        }
    }
}
