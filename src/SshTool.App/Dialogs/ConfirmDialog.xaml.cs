using System.Threading.Tasks;
using SshTool.App.Controls;
using SshTool.App.Infrastructure;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Dialogs
{
    public sealed class ConfirmDialogResult
    {
        public bool Confirmed { get; set; }
        public bool Cancelled { get; set; }
    }

    public sealed partial class ConfirmDialog : ContentDialog
    {
        private ConfirmDialog()
        {
            this.InitializeComponent();
        }

        // 通用确认框；isDanger 时主按钮用 AppDangerBrush（删除等破坏性操作）。
        public static async Task<ConfirmDialogResult> ShowAsync(
            string title, string message, string confirmText = "确定", string cancelText = "取消", bool isDanger = false)
        {
            var dialog = new ConfirmDialog();
            dialog.Title = title;
            dialog.MessageText.Text = message ?? string.Empty;
            dialog.PrimaryButtonText = confirmText;
            dialog.SecondaryButtonText = cancelText;
            if (isDanger)
            {
                // 对话框按钮用 DangerDialogButtonStyle（仅换色、无尺寸/字号），
                // 避免 DangerButtonStyle 的 MinWidth/MinHeight/FontSize 让主/次按钮错位。
                dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["DangerDialogButtonStyle"];
            }
            var result = await ServiceRegistry.Get<DialogService>().ShowAsync(dialog);
            return new ConfirmDialogResult
            {
                Confirmed = result == ContentDialogResult.Primary,
                Cancelled = result != ContentDialogResult.Primary
            };
        }
    }
}
