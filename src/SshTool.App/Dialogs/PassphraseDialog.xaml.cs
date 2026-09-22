using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Dialogs
{
    public sealed class PassphraseDialogResult
    {
        public string Passphrase { get; set; }
        public bool Remember { get; set; }
        public bool Cancelled { get; set; }
    }

    public sealed partial class PassphraseDialog : ContentDialog
    {
        private PassphraseDialog()
        {
            this.InitializeComponent();
        }

        // keyName 例：「lumia-ed25519」。短语读出后立即清空控件，不记日志。
        public static async Task<PassphraseDialogResult> ShowAsync(string keyName)
        {
            var dialog = new PassphraseDialog();
            dialog.PromptText.Text = Localized.Format("Passphrase_Prompt", "私钥 {0} 的短语", keyName);
            var result = await ServiceRegistry.Get<DialogService>().ShowAsync(dialog);
            if (result != ContentDialogResult.Primary)
            {
                dialog.PassphraseBox.Password = string.Empty;
                return new PassphraseDialogResult { Cancelled = true };
            }
            var passphrase = dialog.PassphraseBox.Password;
            dialog.PassphraseBox.Password = string.Empty;
            return new PassphraseDialogResult
            {
                Passphrase = passphrase,
                Remember = dialog.RememberSwitch.IsOn
            };
        }
    }
}
