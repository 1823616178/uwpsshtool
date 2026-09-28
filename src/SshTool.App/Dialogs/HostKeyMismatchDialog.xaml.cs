using System;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Dialogs
{
    public sealed class HostKeyMismatchDialogResult
    {
        public bool RemoveAndRetry { get; set; }
        public bool Cancelled { get; set; }
    }

    public sealed partial class HostKeyMismatchDialog : ContentDialog
    {
        private HostKeyMismatchDialog()
        {
            this.InitializeComponent();
        }

        // 次按钮「移除旧记录并重试…」需先输入主机名确认；不匹配则阻止关闭。
        public static async Task<HostKeyMismatchDialogResult> ShowAsync(
            string hostName, string recordedFingerprint, string currentFingerprint)
        {
            var dialog = new HostKeyMismatchDialog();
            dialog.OldFingerprintText.Text = recordedFingerprint;
            dialog.NewFingerprintText.Text = currentFingerprint;
            dialog.ConfirmHintText.Text = Localized.Format("HostKeyMismatch_ConfirmHint", "移除需确认：请输入主机名 {0}", hostName);
            dialog.SecondaryButtonClick += (s, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    if (!string.Equals(dialog.ConfirmBox.Text.Trim(), hostName, StringComparison.Ordinal))
                    {
                        args.Cancel = true;
                        dialog.ConfirmErrorText.Text = Localized.Get("HostKeyMismatch_NameMismatch", "主机名不匹配，未移除。");
                        dialog.ConfirmErrorText.Visibility = Visibility.Visible;
                    }
                }
                finally
                {
                    deferral.Complete();
                }
            };
            var result = await ServiceRegistry.Get<DialogService>().ShowAsync(dialog);
            return new HostKeyMismatchDialogResult
            {
                RemoveAndRetry = result == ContentDialogResult.Secondary,
                Cancelled = result != ContentDialogResult.Secondary
            };
        }
    }
}
