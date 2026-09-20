using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.Core.Sftp;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Dialogs
{
    public sealed class PermissionsDialogResult
    {
        public bool Confirmed { get; set; }
        public int Mode { get; set; }
    }

    // F03：权限编辑框（02-UI-DESIGN.md §5.17：rwx 九宫格 + 八进制互转，转换在 Core
    // PermissionBits 并有单测）。九宫格 ⇄ 八进制文本双向联动；Primary 返回八进制解析
    // 结果（非法时禁用主按钮）；文案走 resw（双语），色值一律 Token。
    public sealed partial class PermissionsDialog : ContentDialog
    {
        private static readonly ResourceLoader Loader = ResourceLoader.GetForCurrentView();

        private bool _updating;
        private CheckBox[] _bits;

        private PermissionsDialog()
        {
            this.InitializeComponent();
            // 取消/确定复用成对共享键 Dialog_Ok / Dialog_Cancel（x:Uid 只能取每对话框前缀键，无法复用）。
            PrimaryButtonText = Loader.GetString("Dialog_Ok");
            SecondaryButtonText = Loader.GetString("Dialog_Cancel");
            OwnerLabel.Text = Loader.GetString("Sftp_PermissionsOwner");
            GroupLabel.Text = Loader.GetString("Sftp_PermissionsGroup");
            OtherLabel.Text = Loader.GetString("Sftp_PermissionsOther");
            ReadLabel.Text = Loader.GetString("Sftp_PermissionsRead");
            WriteLabel.Text = Loader.GetString("Sftp_PermissionsWrite");
            ExecuteLabel.Text = Loader.GetString("Sftp_PermissionsExecute");
            OctalBox.Header = Loader.GetString("Sftp_PermissionsOctal.Header");
            // PermissionBits.ToRwx 位序：r,w,x | r,w,x | r,w,x（高→低 0x100→0x1）。
            _bits = new CheckBox[9]
            {
                OwnerRead, OwnerWrite, OwnerExecute,
                GroupRead, GroupWrite, GroupExecute,
                OtherRead, OtherWrite, OtherExecute
            };
            for (int i = 0; i < _bits.Length; i++)
            {
                _bits[i].Checked += OnCellChanged;
                _bits[i].Unchecked += OnCellChanged;
            }
        }

        public static async Task<PermissionsDialogResult> ShowAsync(string title, int mode)
        {
            var dialog = new PermissionsDialog();
            dialog.Title = string.IsNullOrEmpty(title)
                ? Loader.GetString("Sftp_PermissionsTitle")
                : title;
            dialog._updating = true;
            dialog.OctalBox.Text = PermissionBits.ToOctal(mode);
            dialog.ApplyToCells(mode);
            dialog._updating = false;
            ContentDialogResult result =
                await ServiceRegistry.Get<DialogService>().ShowAsync(dialog);
            if (result != ContentDialogResult.Primary)
            {
                return new PermissionsDialogResult { Confirmed = false };
            }
            int parsed;
            if (!PermissionBits.TryParseOctal(dialog.OctalBox.Text, out parsed))
            {
                return new PermissionsDialogResult { Confirmed = false };
            }
            return new PermissionsDialogResult { Confirmed = true, Mode = parsed };
        }

        // 九宫格 → 八进制文本。
        private void OnCellChanged(object sender, RoutedEventArgs e)
        {
            if (_updating)
            {
                return;
            }
            _updating = true;
            int mode = CollectMode();
            OctalBox.Text = PermissionBits.ToOctal(mode);
            HideError();
            _updating = false;
        }

        // 八进制文本 → 九宫格。
        private void OnOctalChanged(object sender, TextChangedEventArgs e)
        {
            if (_updating)
            {
                return;
            }
            _updating = true;
            int mode;
            if (PermissionBits.TryParseOctal(OctalBox.Text, out mode))
            {
                ApplyToCells(mode);
                HideError();
            }
            else
            {
                ShowError(Loader.GetString("Sftp_PermissionsInvalid"));
            }
            _updating = false;
        }

        private int CollectMode()
        {
            int mode = 0;
            for (int i = 0; i < _bits.Length; i++)
            {
                if (_bits[i] != null && _bits[i].IsChecked == true)
                {
                    mode |= 1 << (8 - i);
                }
            }
            return mode;
        }

        private void ApplyToCells(int mode)
        {
            string rwx = PermissionBits.ToRwx(mode);
            for (int i = 0; i < _bits.Length; i++)
            {
                _bits[i].IsChecked = rwx[i] != '-';
            }
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message ?? string.Empty;
            ErrorText.Visibility = string.IsNullOrEmpty(message)
                ? Visibility.Collapsed : Visibility.Visible;
            IsPrimaryButtonEnabled = false;
        }

        private void HideError()
        {
            ErrorText.Visibility = Visibility.Collapsed;
            IsPrimaryButtonEnabled = true;
        }
    }
}
