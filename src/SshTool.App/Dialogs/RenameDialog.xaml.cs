using System;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Dialogs
{
    public sealed class RenameDialogResult
    {
        public bool Confirmed { get; set; }
        public string Name { get; set; }
    }

    // F03：重命名 / 新建文件夹共用的名称输入框（§5.17 行菜单）。
    // 校验：非空、不含 “/”、不为 “.”/“..”（RemotePath 语义：名称段）；
    // 校验失败时禁用主按钮并提示。取消返回 Confirmed=false。
    public sealed partial class RenameDialog : ContentDialog
    {
        private static readonly ResourceLoader Loader = ResourceLoader.GetForCurrentView();

        private RenameDialog()
        {
            this.InitializeComponent();
            NameBox.Header = Loader.GetString("Sftp_RenameName.Header");
            // 取消/确定复用成对共享键 Dialog_Ok / Dialog_Cancel（x:Uid 只能取每对话框前缀键，无法复用）。
            PrimaryButtonText = Loader.GetString("Dialog_Ok");
            SecondaryButtonText = Loader.GetString("Dialog_Cancel");
        }

        public static async Task<RenameDialogResult> ShowAsync(string title, string initialName)
        {
            var dialog = new RenameDialog();
            dialog.Title = string.IsNullOrEmpty(title)
                ? Loader.GetString("Sftp_RenameTitle")
                : title;
            dialog.NameBox.Text = initialName ?? string.Empty;
            dialog.Validate();
            var result = await ServiceRegistry.Get<DialogService>().ShowAsync(dialog);
            if (result != ContentDialogResult.Primary)
            {
                return new RenameDialogResult { Confirmed = false };
            }
            return new RenameDialogResult
            {
                Confirmed = true,
                Name = dialog.NameBox.Text == null ? string.Empty : dialog.NameBox.Text.Trim()
            };
        }

        private void OnNameChanged(object sender, TextChangedEventArgs e)
        {
            Validate();
        }

        private void Validate()
        {
            string name = NameBox.Text == null ? string.Empty : NameBox.Text.Trim();
            string error = null;
            if (name.Length == 0)
            {
                error = Loader.GetString("Sftp_NameRequired");
            }
            else if (name.IndexOf('/') >= 0 || name == "." || name == "..")
            {
                error = Loader.GetString("Sftp_NameInvalid");
            }
            if (error == null)
            {
                ErrorText.Visibility = Visibility.Collapsed;
                IsPrimaryButtonEnabled = true;
            }
            else
            {
                ErrorText.Text = error;
                ErrorText.Visibility = Visibility.Visible;
                IsPrimaryButtonEnabled = false;
            }
        }
    }
}
