using System;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels.Keys;
using SshTool.Core.Common;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation.Metadata;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views.Keys
{
    public sealed class KeyDetailArgs
    {
        public string KeyId { get; set; }

        public static KeyDetailArgs For(string keyId)
        {
            return new KeyDetailArgs { KeyId = keyId };
        }
    }

    public sealed partial class KeyDetailPage : Page
    {
        // R01 (C-02)：导航世代，离开后加载链不再触碰 UI / Frame。
        private readonly NavigationLifetime _lifetime = new NavigationLifetime();
        // R01：已订阅 DataRequested 的 DataTransferManager（视图级长寿对象），离开时解除。
        private DataTransferManager _shareManager;

        public KeyDetailPage()
        {
            ViewModel = new KeyDetailViewModel(AppServices.Current);
            this.InitializeComponent();
            HostList.ItemsSource = ViewModel.Hosts;
        }

        public KeyDetailViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            int generation = _lifetime.Begin();
            var args = e.Parameter as KeyDetailArgs;
            LoadAsync(generation, args == null ? null : args.KeyId).Forget("KeyDetailPage.Load", AppLog.Logger);
        }

        // R01 (C-02)：await 后先查世代，页面已离开则不再 BindLoaded / GoBack。
        private async System.Threading.Tasks.Task LoadAsync(int generation, string keyId)
        {
            try
            {
                bool found = await ViewModel.LoadAsync(keyId);
                if (!_lifetime.IsCurrent(generation))
                {
                    return;
                }
                if (!found)
                {
                    Frame.GoBack();
                    return;
                }
                BindLoaded();
            }
            catch (Exception ex)
            {
                AppLog.Error("KeyDetail", "密钥详情页加载失败", ex);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _lifetime.End();
            UnsubscribeShare();
            base.OnNavigatedFrom(e);
        }

        private void BindLoaded()
        {
            NameBox.Text = ViewModel.Name;
            RenameButton.IsEnabled = false;
            TypeText.Text = ViewModel.KeyTypeText;
            BitsText.Text = ViewModel.BitsText;
            FormatText.Text = ViewModel.FormatText;
            EncryptedText.Text = ViewModel.EncryptedText;
            CreatedText.Text = ViewModel.CreatedAt;
            FingerprintText.Text = ViewModel.Fingerprint;
            PublicKeyText.Text = ViewModel.PublicKey;
            NoHostsText.Visibility = ViewModel.HasHosts ? Visibility.Collapsed : Visibility.Visible;
            HostList.Visibility = ViewModel.HasHosts ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = string.Empty;
        }

        private void OnNameChanged(object sender, TextChangedEventArgs e)
        {
            ViewModel.Name = NameBox.Text;
            RenameButton.IsEnabled = ViewModel.CanRename;
        }

        private async void OnRenameClick(object sender, RoutedEventArgs e)
        {
            string error = await ViewModel.RenameAsync();
            StatusText.Text = error ?? "已保存";
            RenameButton.IsEnabled = ViewModel.CanRename;
        }

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(ViewModel.PublicKey))
            {
                StatusText.Text = Localized.Get("KeyDetail_NoPublicKeyToCopy", "没有可复制的公钥");
                return;
            }
            ViewModel.CopyPublicKey();
            StatusText.Text = Localized.Get("KeyDetail_PublicKeyCopied", "已复制公钥");
        }

        private void OnShareClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(ViewModel.PublicKey))
            {
                StatusText.Text = Localized.Get("KeyDetail_NoPublicKeyToShare", "没有可分享的公钥");
                return;
            }
            // 分享面板（DataTransferManager）在 15063 即存在，但桌面/Continuum
            // 行为以守卫 + try/catch 为准，不可用时回退为复制。
            try
            {
                if (!ApiInformation.IsTypePresent(
                        "Windows.ApplicationModel.DataTransfer.DataTransferManager")
                    || !ApiInformation.IsMethodPresent(
                        "Windows.ApplicationModel.DataTransfer.DataTransferManager", "ShowShareUI"))
                {
                    ViewModel.CopyPublicKey();
                    StatusText.Text = Localized.Get("KeyDetail_ShareUnavailable", "分享不可用，已复制公钥");
                    return;
                }
                DataTransferManager manager = DataTransferManager.GetForCurrentView();
                // R01：重复点击先解除旧订阅，避免叠加；引用存字段供离开路径兜底解除。
                UnsubscribeShare();
                _shareManager = manager;
                manager.DataRequested += OnShareDataRequested;
                DataTransferManager.ShowShareUI();
            }
            catch (Exception)
            {
                UnsubscribeShare();
                ViewModel.CopyPublicKey();
                StatusText.Text = Localized.Get("KeyDetail_ShareFailed", "分享失败，已复制公钥");
            }
        }

        private void OnShareDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            UnsubscribeShare();
            args.Request.Data.SetText(ViewModel.PublicKey);
            args.Request.Data.Properties.Title = ViewModel.Name;
        }

        // R01：DataTransferManager 是视图级长寿对象；用户取消分享面板时 DataRequested
        // 不触发，页面引用会挂到视图寿命结束。离开路径（OnNavigatedFrom）必须兜底解除
        //（-= 未订阅时无操作，幂等）。
        private void UnsubscribeShare()
        {
            DataTransferManager manager = _shareManager;
            if (manager == null)
            {
                return;
            }
            _shareManager = null;
            manager.DataRequested -= OnShareDataRequested;
        }

        private void OnHostClick(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as KeyHostRow;
            string hostId = ViewModel.GetHostId(row);
            if (!string.IsNullOrEmpty(hostId))
            {
                Frame.Navigate(typeof(HostEditPage), HostEditArgs.Edit(hostId));
            }
        }

        private async void OnExportClick(object sender, RoutedEventArgs e)
        {
            StatusText.Text = await ViewModel.ExportPrivateAsync();
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            bool deleted = await ViewModel.DeleteAsync();
            if (deleted && Frame.CanGoBack)
            {
                Frame.GoBack();
            }
            else
            {
                NoHostsText.Visibility = ViewModel.HasHosts ? Visibility.Collapsed : Visibility.Visible;
                HostList.Visibility = ViewModel.HasHosts ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }
}
