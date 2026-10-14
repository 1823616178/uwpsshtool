using System;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels.Keys;
using SshTool.Core.Common;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views.Keys
{
    public sealed partial class KeysPage : Page
    {
        // R01 (C-02)：导航世代，离开后加载链不再触碰 UI。
        private readonly NavigationLifetime _lifetime = new NavigationLifetime();
        private readonly ResourceLoader _loader = ResourceLoader.GetForCurrentView();

        public KeysPage()
        {
            ViewModel = new KeysViewModel(AppServices.Current);
            this.InitializeComponent();
            KeyList.ItemsSource = ViewModel.Rows;
            Empty.PrimaryCommand = ViewModel.GenerateCommand;
            Empty.SecondaryCommand = ViewModel.ImportCommand;
            // V04b（C-06）：EmptyState 文案走 resw 双语。
            Empty.Title = _loader.GetString("Keys_Empty_Title");
            Empty.Description = _loader.GetString("Keys_Empty_Description");
            Empty.PrimaryText = _loader.GetString("Keys_Empty_Primary");
            Empty.SecondaryText = _loader.GetString("Keys_Empty_Secondary");
            BottomBar.PrimaryText = _loader.GetString("Keys_Generate/Label");
            UpdateCountText();
        }

        // opt/full-pass：页头标题 + 摘要行（数量）。文案走 resw，两处都允许资源缺失时为空闲值。
        private void UpdateCountText()
        {
            int count = ViewModel.Rows == null ? 0 : ViewModel.Rows.Count;
            CountText.Text = count == 0
                ? _loader.GetString("Keys_CountFormatNone")
                : Localized.Format("Keys_CountFormat", "{0} 个密钥 · 长按可查看详情或删除", count);
        }

        // opt/full-pass：整行长期按住/右键与尾槽「⋯」共用同一组动作（详情 / 删除）。
        private void OnRowHolding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState == Windows.UI.Input.HoldingState.Started)
            {
                ShowRowMenu((FrameworkElement)sender, e.GetPosition((UIElement)sender));
            }
        }

        private void OnRowRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            ShowRowMenu((FrameworkElement)sender, e.GetPosition((UIElement)sender));
        }

        private void ShowRowMenu(FrameworkElement element, Windows.Foundation.Point position)
        {
            var row = element.DataContext as KeyRow;
            if (row == null)
            {
                return;
            }

            var menu = new MenuFlyout();
            var detail = new MenuFlyoutItem { Text = _loader.GetString("Keys_MenuDetail/Text"), DataContext = row };
            detail.Click += OnDetailClick;
            var delete = new MenuFlyoutItem
            {
                Text = _loader.GetString("Keys_MenuDelete/Text"),
                DataContext = row,
                Style = (Style)Application.Current.Resources["DangerMenuItemStyle"]
            };
            delete.Click += OnDeleteClick;
            menu.Items.Add(detail);
            menu.Items.Add(delete);
            menu.ShowAt(element, position);
        }

        public KeysViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            int generation = _lifetime.Begin();
            RefreshAsync(generation).Forget("KeysPage.Refresh", AppLog.Logger);
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
                ErrorState.Visibility = Visibility.Collapsed;
                UpdateChrome();
                ViewModel.PropertyChanged += OnVmPropertyChanged;
            }
            catch (Exception ex)
            {
                AppLog.Error("Keys", "key list load failed", ex);
                if (_lifetime.IsCurrent(generation))
                {
                    ErrorState.Title = _loader.GetString("Common_LoadFailed");
                    ErrorState.Description = ex.Message;
                    ErrorState.PrimaryText = _loader.GetString("Common_Retry");
                    ErrorState.PrimaryClick -= OnRetryClick;
                    ErrorState.PrimaryClick += OnRetryClick;
                    ErrorState.Visibility = Visibility.Visible;
                    Empty.Visibility = Visibility.Collapsed;
                    KeyList.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void OnRetryClick(object sender, RoutedEventArgs e)
        {
            ErrorState.PrimaryClick -= OnRetryClick;
            int generation = _lifetime.Begin();
            RefreshAsync(generation).Forget("KeysPage.Retry", AppLog.Logger);
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
            UpdateCountText();
        }

        private void OnHeaderBackRequested(object sender, EventArgs e)
        {
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.RequestBack();
            }
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
            try
            {
                await ViewModel.DeleteAsync(((FrameworkElement)sender).DataContext as KeyRow);
                UpdateChrome();
            }
            catch (Exception ex)
            {
                AppLog.Error("KeysPage", "OnDeleteClick failed", ex);
            }
        }

        private async void OnImportClick(object sender, EventArgs e)
        {
            try
            {
                await ViewModel.ImportAsync();
                UpdateChrome();
            }
            catch (Exception ex)
            {
                AppLog.Error("KeysPage", "OnImportClick failed", ex);
            }
        }

        private async void OnGenerateClick(object sender, EventArgs e)
        {
            try
            {
                await ViewModel.GenerateAsync();
                UpdateChrome();
            }
            catch (Exception ex)
            {
                AppLog.Error("KeysPage", "OnGenerateClick failed", ex);
            }
        }
    }
}
