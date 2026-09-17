using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Dialogs
{
    public sealed class CredentialDialogResult
    {
        public string Password { get; set; }
        public bool Remember { get; set; }
        public bool Cancelled { get; set; }
    }

    public sealed partial class CredentialDialog : ContentDialog
    {
        private CredentialDialog()
        {
            this.InitializeComponent();
        }

        // errorMessage 非空时红字显示（如「密码错误，还可尝试 2 次」）。密码读出后立即清空控件，不记日志。
        public static async Task<CredentialDialogResult> ShowAsync(string hostName, string errorMessage = null)
        {
            var dialog = new CredentialDialog();
            dialog.PromptText.Text = hostName + " 的密码";
            if (!string.IsNullOrEmpty(errorMessage))
            {
                dialog.ErrorText.Text = errorMessage;
                dialog.ErrorText.Visibility = Visibility.Visible;
            }
            var result = await ServiceRegistry.Get<DialogService>().ShowAsync(dialog);
            if (result != ContentDialogResult.Primary)
            {
                dialog.PasswordBox.Password = string.Empty;
                return new CredentialDialogResult { Cancelled = true };
            }
            var password = dialog.PasswordBox.Password;
            dialog.PasswordBox.Password = string.Empty;
            return new CredentialDialogResult
            {
                Password = password,
                Remember = dialog.RememberBox.IsChecked == true
            };
        }
    }
}
