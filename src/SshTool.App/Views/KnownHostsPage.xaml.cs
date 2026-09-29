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

        private readonly Windows.ApplicationModel.Resources.ResourceLoader _loader = Windows.ApplicationModel.Resources.ResourceLoader.GetForCurrentView();

        public KnownHostsPage()
        {
            ViewModel = new KnownHostsViewModel(AppServices.Current);
            this.InitializeComponent();
            HostList.ItemsSource = ViewModel.Rows;
            Empty.Title = _loader.GetString("KnownHosts_EmptyTitle");
            Empty.Description = _loader.GetString("KnownHosts_EmptyDescription");
            NoMatches.Title = _loader.GetString("KnownHosts_NoMatchesTitle");
            NoMatches.PrimaryText = _loader.GetString("Common_ClearSearch");
            NoMatches.PrimaryClick += OnClearSearchClick;
        }

        public KnownHostsViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.PropertyChanged += OnVmPropertyChanged;
            ViewModel.RefreshAsync().Forget("KnownHostsPage.Refresh", AppLog.Logger);
            UpdateChrome();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ViewModel.PropertyChanged -= OnVmPropertyChanged;
            base.OnNavigatedFrom(e);
        }

        private void OnVmPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            UpdateChrome();
        }

        private void UpdateChrome()
        {
            bool hasError = ViewModel.HasError;
            ErrorState.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
            Empty.Visibility = (!hasError && ViewModel.IsEmpty) ? Visibility.Visible : Visibility.Collapsed;
            NoMatches.Visibility = (!hasError && ViewModel.HasNoMatches) ? Visibility.Visible : Visibility.Collapsed;
            HostList.Visibility = (hasError || ViewModel.IsEmpty || ViewModel.HasNoMatches) ? Visibility.Collapsed : Visibility.Visible;
            if (hasError)
            {
                ErrorState.Title = _loader.GetString("Common_LoadFailed");
                ErrorState.Description = ViewModel.ErrorMessage;
                ErrorState.PrimaryText = _loader.GetString("Common_Retry");
                ErrorState.PrimaryClick -= OnRetryClick;
                ErrorState.PrimaryClick += OnRetryClick;
            }
        }

        private void OnClearSearchClick(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = string.Empty;
            ViewModel.Search = string.Empty;
            UpdateChrome();
        }

        private void OnRetryClick(object sender, RoutedEventArgs e)
        {
            ViewModel.RefreshAsync().Forget("KnownHostsPage.Retry", AppLog.Logger);
        }

        private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                ViewModel.Search = sender.Text;
                UpdateChrome();
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
