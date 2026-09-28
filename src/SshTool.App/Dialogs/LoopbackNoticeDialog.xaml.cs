using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using Windows.Storage;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Dialogs
{
    public sealed partial class LoopbackNoticeDialog : ContentDialog
    {
        private const string SettingKey = "LoopbackNoticeDismissed";

        private LoopbackNoticeDialog()
        {
            this.InitializeComponent();
        }

        public static bool IsDismissed()
        {
            object val;
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(SettingKey, out val) && val is bool b)
            {
                return b;
            }
            return false;
        }

        public static async Task<bool> CheckAndPromptAsync()
        {
            if (IsDismissed())
            {
                return true;
            }

            var dialog = new LoopbackNoticeDialog();
            dialog.Title = Localized.Get("Tunnels_LoopbackNoticeTitle", "回环隔离限制");
            dialog.MessageText.Text = Localized.Get("Tunnels_LoopbackNoticeMessage", "UWP 应用具有网络隔离机制：本机其他应用可能无法直接访问 127.0.0.1 上的转发端口。");
            dialog.PrimaryButtonText = Localized.Get("Tunnels_Continue", "继续开启");
            dialog.SecondaryButtonText = Localized.Get("Tunnels_Cancel", "取消");

            DialogService dialogService;
            if (ServiceRegistry.TryGet(out dialogService))
            {
                var result = await dialogService.ShowAsync(dialog);
                if (result == ContentDialogResult.Primary)
                {
                    if (dialog.DoNotRemindCheck.IsChecked == true)
                    {
                        ApplicationData.Current.LocalSettings.Values[SettingKey] = true;
                    }
                    return true;
                }
                return false;
            }

            return true;
        }
    }
}
