using SshTool.App.Infrastructure;
using SshTool.App.ViewModels.Keys;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views.Keys
{
    public sealed partial class KeysPage : Page
    {
        public KeysPage()
        {
            ViewModel = new KeysViewModel(AppServices.Current);
            this.InitializeComponent();
            KeyList.ItemsSource = ViewModel.Rows;
            Empty.PrimaryCommand = ViewModel.GenerateCommand;
            Empty.SecondaryCommand = ViewModel.ImportCommand;
        }

        public KeysViewModel ViewModel { get; private set; }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ViewModel.RefreshAsync();
            UpdateChrome();
            ViewModel.PropertyChanged += OnVmPropertyChanged;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ViewModel.PropertyChanged -= OnVmPropertyChanged;
            base.OnNavigatedFrom(e);
        }

        private void OnVmPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "IsEmpty")
            {
                UpdateChrome();
            }
        }

        private void UpdateChrome()
        {
            Empty.Visibility = ViewModel.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            KeyList.Visibility = ViewModel.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            ViewModel.OpenDetail(e.ClickedItem as KeyRow);
        }

        private void OnDetailClick(object sender, RoutedEventArgs e)
        {
            ViewModel.OpenDetail(((FrameworkElement)sender).DataContext as KeyRow);
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            await ViewModel.DeleteAsync(((FrameworkElement)sender).DataContext as KeyRow);
            UpdateChrome();
        }

        private async void OnImportClick(object sender, RoutedEventArgs e)
        {
            await ViewModel.ImportAsync();
            UpdateChrome();
        }

        private async void OnGenerateClick(object sender, RoutedEventArgs e)
        {
            await ViewModel.GenerateAsync();
            UpdateChrome();
        }
    }
}
