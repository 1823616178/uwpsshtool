using System;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.Core.Sync.Vault;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Dialogs
{
    public sealed class RecoveryDialogResult
    {
        // 勾选了「我已保存」且经主按钮 [完成] 关闭。
        public bool SavedConfirmed { get; set; }
        public bool Cancelled { get; set; }
    }

    // §5.8 RecoveryKeyDialog：恢复密钥只显示这一次，必须勾选「我已抄写/保存」后 [完成]
    // 才可用。showWarning=true 用于重弹场景（系统返回键关闭过一次）——警告行可见。
    // 主按钮经 IsPrimaryButtonEnabled 控制；密钥只存内存字段，复制走剪贴板，
    // 不写日志、不持久化（日志脱敏硬性约束）。
    public sealed partial class RecoveryKeyDialog : ContentDialog
    {
        // 内存字段：离开对话框即弃；复制是唯一出口。
        private string _recoveryKey;

        private RecoveryKeyDialog()
        {
            this.InitializeComponent();
            IsPrimaryButtonEnabled = false;
        }

        // showWarning：此前被非确认路径关闭过一次，重弹时显示必须勾选的警告。
        public static async Task<RecoveryDialogResult> ShowAsync(string recoveryKey, bool showWarning = false)
        {
            var dialog = new RecoveryKeyDialog();
            dialog._recoveryKey = recoveryKey;
            dialog.SavedBox.IsChecked = false;
            dialog.IsPrimaryButtonEnabled = false;
            // 三行等宽显示（Core 纯函数按语义切分）；格式异常时回退整行显示，仍可复制。
            string[] lines = RecoveryKeyInput.DisplayLines(recoveryKey);
            if (lines != null && lines.Length == 3)
            {
                dialog.KeyLine1.Text = lines[0];
                dialog.KeyLine2.Text = lines[1];
                dialog.KeyLine3.Text = lines[2];
            }
            else
            {
                dialog.KeyLine1.Text = recoveryKey ?? string.Empty;
                dialog.KeyLine2.Text = string.Empty;
                dialog.KeyLine3.Text = string.Empty;
            }
            dialog.WarningText.Visibility = showWarning ? Visibility.Visible : Visibility.Collapsed;
            ContentDialogResult result = await ServiceRegistry.Get<DialogService>().ShowAsync(dialog);
            dialog._recoveryKey = null;
            bool confirmed = result == ContentDialogResult.Primary && dialog.SavedBox.IsChecked == true;
            return new RecoveryDialogResult
            {
                SavedConfirmed = confirmed,
                Cancelled = !confirmed
            };
        }

        private void OnSavedChanged(object sender, RoutedEventArgs e)
        {
            IsPrimaryButtonEnabled = SavedBox.IsChecked == true;
        }

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            try
            {
                ClipboardService.SetText(_recoveryKey);
                CopiedText.Visibility = Visibility.Visible;
            }
            catch (Exception)
            {
                // 剪贴板偶发被占用：收起提示，密钥仍在框内可选可再复制。
                CopiedText.Visibility = Visibility.Collapsed;
            }
        }
    }
}
