using System;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels.Keys;
using SshTool.Core.Common;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views.Keys
{
    public sealed partial class KeysPage : Page
    {
        // R01 (C-02)：导航世代，离开后加载链不再触碰 UI。
        private readonly NavigationLifetime _lifetime = new NavigationLifetime();

        public KeysPage()
        {
            ViewModel = new KeysViewModel(AppServices.Current);
            this.InitializeComponent();
            KeyList.ItemsSource = ViewModel.Rows;
            Empty.PrimaryCommand = ViewModel.GenerateCommand;
            Empty.SecondaryCommand = ViewModel.ImportCommand;
        }

        public KeysViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            int generation = _lifetime.Begin();
            var ignore = RefreshAsync(generation);
        }

        // R01 (C-02)：await 后先查世代，页面已离开则不再 UpdateChrome / 订阅。
        private async System.Threading.Tasks.Task RefreshAsync(int generation)
        {
            try
            {
                await ViewModel.RefreshAsync();
                if (!_lifetime.IsCurrent(generation))
                {
                    return;
                }
                UpdateChrome();
                ViewModel.PropertyChanged += OnVmPropertyChanged;
            }
            catch (Exception ex)
            {
                AppLog.Error("Keys", "密钥列表加载失败", ex);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _lifetime.End();
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
