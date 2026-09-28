using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels.Keys;
using SshTool.Core.Keys;
using SshTool.Core.Models;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Dialogs
{
    public sealed class KeyGenerateDialogResult
    {
        public KeyEntry Created { get; set; }
        public bool Cancelled { get; set; }
    }

    public sealed partial class KeyGenerateDialog : ContentDialog
    {
        private readonly KeyGenerateViewModel _vm;
        private KeyEntry _created;

        private KeyGenerateDialog()
        {
            _vm = new KeyGenerateViewModel(AppServices.Current);
            this.InitializeComponent();
            this.PrimaryButtonClick += OnGenerateClick;
        }

        // 生成约数秒（RSA-4096）：PrimaryButtonClick + deferral 让对话框保持打开
        // 并显示进度；失败留框显示错误，成功关框返回 Created。取消/返回 → Cancelled。
        public static new async Task<KeyGenerateDialogResult> ShowAsync()
        {
            var dialog = new KeyGenerateDialog();
            ContentDialogResult result = await ServiceRegistry.Get<DialogService>().ShowAsync(dialog);
            if (result != ContentDialogResult.Primary || dialog._created == null)
            {
                return new KeyGenerateDialogResult { Cancelled = true };
            }
            return new KeyGenerateDialogResult { Created = dialog._created };
        }

        private async void OnGenerateClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            ContentDialogButtonClickDeferral deferral = args.GetDeferral();
            try
            {
                _vm.TypeIndex = TypeBox.SelectedIndex < 0 ? 0 : TypeBox.SelectedIndex;
                _vm.KeyName = NameBox.Text;
                _vm.Comment = CommentBox.Text;
                string nameError = _vm.ValidateName();
                if (nameError != null)
                {
                    SetError(nameError);
                    args.Cancel = true;
                    return;
                }
                SetError(null);
                Progress.IsActive = true;
                IsPrimaryButtonEnabled = false;
                try
                {
                    KeyGenerateOutcome outcome = await _vm.GenerateAsync();
                    if (!outcome.Success || outcome.Entry == null)
                    {
                        SetError(outcome.Error ?? Localized.Get("KeyGenerate_ErrFallback", "生成失败，请重试"));
                        args.Cancel = true;
                        return;
                    }
                    _created = outcome.Entry;
                }
                finally
                {
                    Progress.IsActive = false;
                    IsPrimaryButtonEnabled = true;
                }
            }
            finally
            {
                deferral.Complete();
            }
        }

        private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            SetError(null);
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
    }
}
