using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.Core.Common;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    // W05（01-DESIGN §16.5）：分享目标页。复制文件进 ShareInbox 后提示用户回到应用选主机上传。
    public sealed partial class ShareTargetPage : Page
    {
        private ShareOperation _operation;

        public ShareTargetPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _operation = e.Parameter as ShareOperation;
            ReceiveAsync().Forget("ShareTarget.Receive", AppLog.Logger);
        }

        private async Task ReceiveAsync()
        {
            ShareOperation operation = _operation;
            if (operation == null)
            {
                return;
            }
            int copied = 0;
            try
            {
                if (operation.Data.Contains(StandardDataFormats.StorageItems))
                {
                    IReadOnlyList<IStorageItem> items = await operation.Data.GetStorageItemsAsync();
                    operation.ReportDataRetrieved();
                    copied = await ShareInbox.AddAsync(items);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("ShareTarget", "receive failed", ex);
            }
            Busy.IsActive = false;
            Busy.Visibility = Visibility.Collapsed;
            StatusText.Text = copied > 0
                ? Localized.Format("Share_Received", "已收到 {0} 个文件。打开 Lumia SSH，选择主机即可上传。", copied)
                : Localized.Get("Share_Nothing", "没有可上传的文件（不支持文件夹）。");
            DoneButton.Visibility = Visibility.Visible;
        }

        private void OnDoneClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_operation != null)
                {
                    _operation.ReportCompleted();
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("ShareTarget", "complete failed", ex);
            }
        }
    }
}
