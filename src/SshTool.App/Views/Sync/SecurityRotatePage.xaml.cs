using System;
using System.ComponentModel;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels.Sync;
using SshTool.Core.Sync;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views.Sync
{
    // U20 安全清理 / 修改同步密码页（02-UI-DESIGN.md §5.13）。
    // 共用页（SecurityRotateMode）：账号登录密码 + 新同步密码 ×2 → 轮换；成功弹 RecoveryKeyDialog。
    public sealed partial class SecurityRotatePage : Page
    {
        public SecurityRotatePage()
        {
            this.InitializeComponent();
        }

        public SecurityRotateViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            SecurityRotateMode mode = SecurityRotateMode.DisableSensitiveSync;
            if (e.Parameter is SecurityRotateMode)
            {
                mode = (SecurityRotateMode)e.Parameter;
            }
            ViewModel = CreateViewModel(mode);
            ViewModel.SubmitCommand.CanExecuteChanged += OnCanExecuteChanged;
            ViewModel.PropertyChanged += OnViewModelChanged;
            ViewModel.RotationCompleted += OnRotationCompleted;
            Header.Title = ViewModel.IsChangePassword ? "修改同步密码" : "安全清理";
            ExplanationText.Text = ViewModel.IsChangePassword
                ? "将生成新的加密密钥与恢复密钥，清除云端历史版本。旧恢复密钥立即失效。"
                : "关闭敏感同步将生成新的加密密钥与恢复密钥、清除云端历史版本，旧恢复密钥立即失效。";
            RefreshError();
            RefreshBusy();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.SubmitCommand.CanExecuteChanged -= OnCanExecuteChanged;
                ViewModel.PropertyChanged -= OnViewModelChanged;
                ViewModel.RotationCompleted -= OnRotationCompleted;
            }
            base.OnNavigatedFrom(e);
        }

        private static SecurityRotateViewModel CreateViewModel(SecurityRotateMode mode)
        {
            AppServices services = AppServices.Current;
            SyncCoordinator sync = services != null ? services.Sync : null;
            return new SecurityRotateViewModel(sync, mode);
        }

        private void OnLoginPasswordChanged(object sender, RoutedEventArgs e)
        {
            ViewModel.LoginPassword = LoginPasswordBox.Password;
        }

        private void OnSyncPasswordChanged(object sender, RoutedEventArgs e)
        {
            ViewModel.SyncPassword = SyncPasswordBox.Password;
        }

        private void OnConfirmChanged(object sender, RoutedEventArgs e)
        {
            ViewModel.ConfirmPassword = ConfirmBox.Password;
        }

        private void OnSubmitClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SubmitCommand.CanExecute(null))
            {
                ViewModel.SubmitCommand.Execute(null);
            }
        }

        private void OnRotationCompleted(object sender, string recoveryKey)
        {
            var ignore = ShowRecoveryKeyAsync(recoveryKey);
        }

        private async System.Threading.Tasks.Task ShowRecoveryKeyAsync(string recoveryKey)
        {
            // RecoveryKeyDialog 静态工厂；勾选「已保存」后返回。
            var ignored = RecoveryKeyDialog.ShowAsync(recoveryKey);
            // 完成后回状态页（路由到 AccountSyncPage）。
            if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private void OnViewModelChanged(object sender, PropertyChangedEventArgs e)
        {
            string name = e.PropertyName;
            if (name == "ErrorMessage" || name == "HasError")
            {
                RefreshError();
            }
            if (name == "IsBusy" || name == "BusyMessage")
            {
                RefreshBusy();
            }
        }

        private void OnCanExecuteChanged(object sender, EventArgs e)
        {
            RefreshBusy();
        }

        private void RefreshError()
        {
            if (ViewModel != null && ViewModel.HasError)
            {
                ErrorText.Text = ViewModel.ErrorMessage;
                ErrorText.Visibility = Visibility.Visible;
            }
            else
            {
                ErrorText.Visibility = Visibility.Collapsed;
            }
        }

        private void RefreshBusy()
        {
            bool busy = ViewModel != null && ViewModel.IsBusy;
            BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            SubmitButton.IsEnabled = ViewModel != null && ViewModel.SubmitCommand.CanExecute(null);
            if (busy)
            {
                BusyText.Text = ViewModel.BusyMessage;
            }
        }

        private void OnBackRequested(object sender, EventArgs e)
        {
            if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }
    }
}
