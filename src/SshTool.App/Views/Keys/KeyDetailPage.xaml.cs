using System;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels.Keys;
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
        public KeyDetailPage()
        {
            ViewModel = new KeyDetailViewModel(AppServices.Current);
            this.InitializeComponent();
            HostList.ItemsSource = ViewModel.Hosts;
        }

        public KeyDetailViewModel ViewModel { get; private set; }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var args = e.Parameter as KeyDetailArgs;
            string keyId = args == null ? null : args.KeyId;
            bool found = await ViewModel.LoadAsync(keyId);
            if (!found)
            {
                Frame.GoBack();
                return;
            }
            BindLoaded();
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
                StatusText.Text = "没有可复制的公钥";
                return;
            }
            ViewModel.CopyPublicKey();
            StatusText.Text = "已复制公钥";
        }

        private void OnShareClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(ViewModel.PublicKey))
            {
                StatusText.Text = "没有可分享的公钥";
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
                    StatusText.Text = "分享不可用，已复制公钥";
                    return;
                }
                DataTransferManager manager = DataTransferManager.GetForCurrentView();
                manager.DataRequested += OnShareDataRequested;
                DataTransferManager.ShowShareUI();
            }
            catch (Exception)
            {
                ViewModel.CopyPublicKey();
                StatusText.Text = "分享失败，已复制公钥";
            }
        }

        private void OnShareDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            sender.DataRequested -= OnShareDataRequested;
            args.Request.Data.SetText(ViewModel.PublicKey);
            args.Request.Data.Properties.Title = ViewModel.Name;
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
