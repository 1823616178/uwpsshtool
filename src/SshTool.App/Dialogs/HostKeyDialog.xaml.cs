using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Dialogs
{
    public sealed class HostKeyDialogResult
    {
        public bool Trusted { get; set; }
        public bool Cancelled { get; set; }
    }

    public sealed partial class HostKeyDialog : ContentDialog
    {
        private HostKeyDialog()
        {
            this.InitializeComponent();
        }

        // hostDisplay 例：「web-01 (10.0.0.11:22)」；返回键（None）映射为取消。
        public static async Task<HostKeyDialogResult> ShowAsync(
            string hostDisplay, string keyType, string fingerprint, string randomArt)
        {
            var dialog = new HostKeyDialog();
            dialog.HostText.Text = "首次连接 " + hostDisplay;
            dialog.KeyTypeText.Text = "密钥类型：" + keyType;
            dialog.FingerprintText.Text = fingerprint;
            dialog.ArtView.Art = randomArt;
            var result = await ServiceRegistry.Get<DialogService>().ShowAsync(dialog);
            return new HostKeyDialogResult
            {
                Trusted = result == ContentDialogResult.Primary,
                Cancelled = result != ContentDialogResult.Primary
            };
        }
    }
}
