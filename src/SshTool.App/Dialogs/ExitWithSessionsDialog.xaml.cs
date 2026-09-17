using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Dialogs
{
    public sealed class ExitWithSessionsDialogResult
    {
        public bool Exit { get; set; }
        public bool Cancelled { get; set; }
    }

    public sealed partial class ExitWithSessionsDialog : ContentDialog
    {
        private ExitWithSessionsDialog()
        {
            this.InitializeComponent();
        }

        public static async Task<ExitWithSessionsDialogResult> ShowAsync(int sessionCount)
        {
            var dialog = new ExitWithSessionsDialog();
            dialog.MessageText.Text = "有 " + sessionCount + " 个会话正在连接，退出将全部断开。";
            var result = await ServiceRegistry.Get<DialogService>().ShowAsync(dialog);
            return new ExitWithSessionsDialogResult
            {
                Exit = result == ContentDialogResult.Primary,
                Cancelled = result != ContentDialogResult.Primary
            };
        }
    }
}
