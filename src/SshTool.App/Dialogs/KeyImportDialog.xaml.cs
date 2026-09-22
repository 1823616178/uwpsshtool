using System;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels.Keys;
using SshTool.Core.Keys;
using SshTool.Core.Models;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Dialogs
{
    public sealed class KeyImportDialogResult
    {
        public KeyEntry Created { get; set; }
        // 去重命中且用户未选择「仍要保存」时，指向已有密钥（调用方可选中/查看它）。
        public string ExistingKeyId { get; set; }
        public bool Cancelled { get; set; }
    }

    public sealed partial class KeyImportDialog : ContentDialog
    {
        private readonly KeyImportViewModel _vm;
        private bool _awaitingSaveConfirm;

        private KeyImportDialog()
        {
            _vm = new KeyImportViewModel(AppServices.Current);
            this.InitializeComponent();
        }

        // 导入流程（循环复用同一对话框，不知道的输入留在框里供用户修正）：
        // 继续 → 解析 →（加密要短语 / 类型指纹确认 → 保存导入）。
        // 指纹重复 → 提示「仍要保存 / 取消（看已有）」。取消/返回 → Cancelled。
        public static new async Task<KeyImportDialogResult> ShowAsync()
        {
            var dialog = new KeyImportDialog();
            DialogService service = ServiceRegistry.Get<DialogService>();
            while (true)
            {
                ContentDialogResult result = await service.ShowAsync(dialog);
                if (result != ContentDialogResult.Primary)
                {
                    dialog.ClearSecrets();
                    return new KeyImportDialogResult { Cancelled = true };
                }
                string text = dialog.PasteBox.Text ?? string.Empty;
                string phrase = dialog.PassphraseBox.Password;
                dialog.PassphraseBox.Password = string.Empty;
                string name = dialog.NameBox.Text ?? string.Empty;
                dialog._vm.InputText = text;
                dialog._vm.KeyName = name;
                if (!string.IsNullOrEmpty(phrase))
                {
                    // 本次新输入的短语覆盖暂存（只在内存，退出即清空）；
                    // 修正文本后重试时沿用上次短语，无需重输。
                    dialog._stagedPhrase = phrase;
                }
                phrase = null;

                KeyInspectOutcome outcome = await dialog._vm.InspectAsync(dialog._stagedPhrase);
                switch (outcome.Status)
                {
                    case KeyInspectStatus.Ready:
                        if (!dialog._awaitingSaveConfirm)
                        {
                            // 第一次：显示类型/指纹，请用户确认后再按一次保存。
                            dialog.ShowPreview(outcome.Inspected);
                            dialog.SetError(null);
                            dialog._awaitingSaveConfirm = true;
                            dialog.PrimaryButtonText = Localized.Get("KeyImport_SaveImport", "保存导入");
                            continue;
                        }
                        string nameError = dialog._vm.ValidateName();
                        if (nameError != null)
                        {
                            dialog.SetError(nameError);
                            continue;
                        }
                        KeyEntry created = await dialog._vm.SaveAsync(
                            dialog.ConsumeStagedPhrase(), dialog.RememberBox.IsChecked == true);
                        dialog.ClearSecrets();
                        return new KeyImportDialogResult { Created = created };
                    case KeyInspectStatus.Duplicate:
                        {
                            string existingName = dialog._vm.DuplicateName();
                            dialog.ClearSecrets();
                            ConfirmDialogResult confirm = await ConfirmDialog.ShowAsync(
                                "密钥已存在",
                                "已存在相同指纹的密钥「" + existingName + "」。仍要保存为新密钥吗？",
                                "仍要保存", "取消");
                            if (confirm.Confirmed)
                            {
                                string retryNameError = dialog._vm.ValidateName();
                                if (retryNameError != null)
                                {
                                    dialog.SetError(retryNameError);
                                    dialog._awaitingSaveConfirm = false;
                                    dialog.PrimaryButtonText = Localized.Get("KeyImport_Continue", "继续");
                                    continue;
                                }
                                KeyEntry dup = await dialog._vm.SaveAsync(
                                    dialog.ConsumeStagedPhrase(),
                                    dialog.RememberBox.IsChecked == true);
                                dialog.ClearSecrets();
                                return new KeyImportDialogResult { Created = dup };
                            }
                            string existingId = outcome.Existing == null ? null : outcome.Existing.Id;
                            return new KeyImportDialogResult { ExistingKeyId = existingId };
                        }
                    case KeyInspectStatus.NeedsPassphrase:
                        dialog.PassphrasePanel.Visibility = Visibility.Visible;
                        dialog.SetError("该私钥已加密，请输入短语后继续");
                        dialog._awaitingSaveConfirm = false;
                        dialog.PrimaryButtonText = Localized.Get("KeyImport_Continue", "继续");
                        continue;
                    case KeyInspectStatus.Invalid:
                        dialog.SetError("无法解析该私钥（文件损坏或短语错误），请检查后重试");
                        dialog._awaitingSaveConfirm = false;
                        dialog.PrimaryButtonText = Localized.Get("KeyImport_Continue", "继续");
                        continue;
                    case KeyInspectStatus.TooLarge:
                        dialog.SetError("文件超过 256 KiB，拒绝导入");
                        dialog._awaitingSaveConfirm = false;
                        dialog.PrimaryButtonText = Localized.Get("KeyImport_Continue", "继续");
                        continue;
                    default:
                        dialog.SetError("请粘贴私钥内容或从文件导入");
                        dialog._awaitingSaveConfirm = false;
                        dialog.PrimaryButtonText = Localized.Get("KeyImport_Continue", "继续");
                        continue;
                }
            }
        }

        // 保存时用的短语：对话期间暂存（只在内存，保存/退出即清空），
        // 修正文本后重试无需重输。
        private string _stagedPhrase = string.Empty;

        private string ConsumeStagedPhrase()
        {
            string phrase = _stagedPhrase;
            _stagedPhrase = string.Empty;
            return phrase;
        }

        private void ClearSecrets()
        {
            PassphraseBox.Password = string.Empty;
            _stagedPhrase = string.Empty;
        }

        private void ShowPreview(InspectedKeyInfo info)
        {
            TypeText.Text = (info.KeyType ?? string.Empty) + " · " + info.Bits.ToString() + " 位 · "
                + (info.Format ?? string.Empty) + (info.Encrypted ? " · 已加密" : string.Empty);
            FingerprintText.Text = info.FingerprintSha256 ?? string.Empty;
            PreviewPanel.Visibility = Visibility.Visible;
            if (string.IsNullOrWhiteSpace(NameBox.Text) && !string.IsNullOrEmpty(info.Comment))
            {
                NameBox.Text = info.Comment;
            }
        }

        private void SetError(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                ErrorText.Text = string.Empty;
                ErrorText.Visibility = Visibility.Collapsed;
            }
            else
            {
                ErrorText.Text = message;
                ErrorText.Visibility = Visibility.Visible;
            }
        }

        private void OnInputChanged(object sender, TextChangedEventArgs e)
        {
            // 输入变化后之前的类型/指纹确认作废。
            _awaitingSaveConfirm = false;
            PrimaryButtonText = "继续";
            PreviewPanel.Visibility = Visibility.Collapsed;
        }

        private async void OnPickFileClick(object sender, RoutedEventArgs e)
        {
            SetError(null);
            FileOpenPicker picker = new FileOpenPicker();
            // 02-UI-DESIGN.md §5.10：所有文件。
            picker.FileTypeFilter.Add("*");
            StorageFile file;
            try
            {
                file = await picker.PickSingleFileAsync();
            }
            catch (Exception ex)
            {
                SetError("文件选择失败：" + ex.GetType().Name);
                return;
            }
            if (file == null)
            {
                return;
            }
            try
            {
                Windows.Storage.FileProperties.BasicProperties props =
                    await file.GetBasicPropertiesAsync();
                if (props.Size > (ulong)KeyImportService.MaxPrivateKeyBytes)
                {
                    SetError("文件超过 256 KiB，拒绝导入");
                    return;
                }
                PasteBox.Text = await FileIO.ReadTextAsync(file);
                if (string.IsNullOrWhiteSpace(NameBox.Text))
                {
                    NameBox.Text = file.DisplayName;
                }
                OnInputChanged(null, null);
            }
            catch (Exception ex)
            {
                SetError("文件读取失败：" + ex.GetType().Name);
            }
        }
    }
}
