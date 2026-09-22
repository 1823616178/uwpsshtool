using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.Core.Terminal;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Dialogs
{
    public sealed class PasteConfirmDialogResult
    {
        public bool Confirmed { get; set; }
        public bool DontAskAgain { get; set; }
    }

    public sealed partial class PasteConfirmDialog : ContentDialog
    {
        private PasteConfirmDialog()
        {
            this.InitializeComponent();
        }

        public static async Task<PasteConfirmDialogResult> ShowAsync(string normalized)
        {
            int lines = PasteProcessor.LineCount(normalized);
            var dialog = new PasteConfirmDialog();
            dialog.Title = Localized.Get("PasteConfirm_Title", "多行粘贴");
            dialog.PrimaryButtonText = Localized.Get("PasteConfirm_Paste", "粘贴");
            dialog.SecondaryButtonText = Localized.Get("Dialog_Cancel", "取消");
            dialog.SummaryText.Text = Localized.Format("PasteConfirm_Summary", "将粘贴 {0} 行", lines);
            dialog.PreviewText.Text = Preview(normalized);
            ContentDialogResult result = await ServiceRegistry.Get<DialogService>().ShowAsync(dialog);
            return new PasteConfirmDialogResult
            {
                Confirmed = result == ContentDialogResult.Primary,
                DontAskAgain = dialog.DontAskBox.IsChecked == true
            };
        }

        private static string Preview(string normalized)
        {
            if (string.IsNullOrEmpty(normalized))
            {
                return string.Empty;
            }
            string[] parts = normalized.Split('\r');
            int take = parts.Length < 5 ? parts.Length : 5;
            var sb = new StringBuilder();
            for (int i = 0; i < take; i++)
            {
                if (i > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(parts[i]);
            }
            if (parts.Length > 5)
            {
                sb.Append('\n').Append("…");
            }
            return sb.ToString();
        }
    }
}
