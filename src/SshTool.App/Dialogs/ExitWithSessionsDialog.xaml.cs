using System.Globalization;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
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
        private static readonly ResourceLoader Loader = ResourceLoader.GetForCurrentView();

        private ExitWithSessionsDialog()
        {
            this.InitializeComponent();
        }

        public static async Task<ExitWithSessionsDialogResult> ShowAsync(int sessionCount)
        {
            var dialog = new ExitWithSessionsDialog();
            // 退出断开所有会话属于破坏性操作，主按钮设为红色警告
            dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["DangerDialogButtonStyle"];
            // 标题/按钮由 x:Uid 本地化；正文带会话数，用 {0} 模板格式化（不拼字符串）。
            dialog.MessageText.Text = string.Format(CultureInfo.CurrentCulture,
                Loader.GetString("ExitDialog_Message"),
                sessionCount.ToString(CultureInfo.InvariantCulture));
            var result = await ServiceRegistry.Get<DialogService>().ShowAsync(dialog);
            return new ExitWithSessionsDialogResult
            {
                Exit = result == ContentDialogResult.Primary,
                Cancelled = result != ContentDialogResult.Primary
            };
        }
    }
}
