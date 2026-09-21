using System;
using System.ComponentModel;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels.Sync;
using SshTool.Core.Common;
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
        // O04（C-02）：导航世代。恢复密钥对话框可能开着很久，
        // 期间用户返回时不得再导航或刷新本页。
        private readonly SshTool.Core.Common.NavigationLifetime _lifetime =
            new SshTool.Core.Common.NavigationLifetime();

        public SecurityRotatePage()
        {
            this.InitializeComponent();
        }

        public SecurityRotateViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _lifetime.Begin();
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
            _lifetime.End(); // O04：恢复密钥流程若仍在飞行，之后不再碰本页
            if (ViewModel != null)
            {
                ViewModel.SubmitCommand.CanExecuteChanged -= OnCanExecuteChanged;
                ViewModel.PropertyChanged -= OnViewModelChanged;
                ViewModel.RotationCompleted -= OnRotationCompleted;
                ViewModel.Detach(); // O03：VM 挂在应用级 SyncCoordinator 上
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
            ShowRecoveryKeyAsync(recoveryKey).Forget("SecurityRotatePage.ShowRecoveryKey", AppLog.Logger);
        }

        // O04 修正：原实现写的是 `var ignored = RecoveryKeyDialog.ShowAsync(...)`
        // ——任务被丢弃，于是 GoBack() 在对话框刚弹出时就执行了，用户根本没机会
        // 看到、更没机会保存轮换后的新恢复密钥（注释写的「勾选『已保存』后返回」
        // 从未成立）。恢复密钥丢失不可找回，这里改为与 VaultSetupPage 同一套：
        // 等到用户勾选确认为止，未确认就带警告重弹。
        private async System.Threading.Tasks.Task ShowRecoveryKeyAsync(string recoveryKey)
        {
            int generation = _lifetime.Current;
            bool warned = false;
            while (true)
            {
                RecoveryDialogResult result = await RecoveryKeyDialog.ShowAsync(recoveryKey, warned);
                if (result.SavedConfirmed)
                {
                    break;
                }
                warned = true;
            }
            if (!_lifetime.IsCurrent(generation))
            {
                return; // 已离开本页：GoBack 属于旧页面
            }
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
