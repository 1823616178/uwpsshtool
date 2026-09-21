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
    // U16 建库页（02-UI-DESIGN.md §5.13 VaultSetupPage）。
    // PasswordBox 不做 x:Bind：由 PasswordChanged 事件写入 ViewModel 内存字段（脱敏）。
    // 成功链路：SetupVaultAsync → RecoveryKeyDialog（必须勾选已保存，被系统返回键关闭会
    // 重弹并警告）→ FinishSetupAsync 自动首次同步 → GoAfterAuth 去状态页（U17 后为 AccountSyncPage）。
    public sealed partial class VaultSetupPage : Page
    {
        // O04（C-02）：导航世代。恢复密钥对话框可能开着很久，
        // 期间用户返回时不得再导航或刷新本页。
        private readonly SshTool.Core.Common.NavigationLifetime _lifetime =
            new SshTool.Core.Common.NavigationLifetime();

        public VaultSetupPage()
        {
            ViewModel = CreateViewModel();
            this.InitializeComponent();
            ViewModel.SubmitCommand.CanExecuteChanged += OnCanExecuteChanged;
            ViewModel.PropertyChanged += OnViewModelChanged;
            RefreshBusy();
        }

        public VaultSetupViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _lifetime.Begin();
            ViewModel.SetupCompleted += OnSetupCompleted;
            // 保险库已就绪（本页已无意义，如从旧入口重复进入）：直接去状态路由。
            if (VaultIsReady())
            {
                SyncNavigation.GoAfterAuth(Frame, 1);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _lifetime.End(); // O04：恢复密钥流程若仍在飞行，之后不再碰本页
            ViewModel.SetupCompleted -= OnSetupCompleted;
            ViewModel.Detach(); // O03：VM 挂在应用级 SyncCoordinator 上
            base.OnNavigatedFrom(e);
        }

        private static VaultSetupViewModel CreateViewModel()
        {
            AppServices services = AppServices.Current;
            SyncCoordinator sync = services != null ? services.Sync : null;
            return new VaultSetupViewModel(sync);
        }

        private static bool VaultIsReady()
        {
            try
            {
                AppServices services = AppServices.Current;
                return services != null && services.Sync != null
                    && services.Sync.State.Vault == VaultStatus.Ready;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void OnPasswordChanged(object sender, RoutedEventArgs e)
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

        private void OnViewModelChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null || string.IsNullOrEmpty(e.PropertyName))
            {
                RefreshAll();
                return;
            }
            if (string.Equals(e.PropertyName, "ErrorMessage", StringComparison.Ordinal)
                || string.Equals(e.PropertyName, "HasError", StringComparison.Ordinal))
            {
                RefreshError();
            }
            else if (string.Equals(e.PropertyName, "IsBusy", StringComparison.Ordinal)
                || string.Equals(e.PropertyName, "BusyMessage", StringComparison.Ordinal))
            {
                RefreshBusy();
            }
        }

        private void OnCanExecuteChanged(object sender, EventArgs e)
        {
            RefreshBusy();
        }

        private void RefreshAll()
        {
            RefreshError();
            RefreshBusy();
        }

        // 成功：弹恢复密钥对话框（§5.8）。非确认路径（系统返回键）关闭会重弹并显示警告——
        // 保险库已创建，恢复密钥丢失将无法找回，必须等到勾选确认。
        private async void OnSetupCompleted(object sender, string recoveryKey)
        {
            int generation = _lifetime.Current;
            bool warned = false;
            try
            {
                while (true)
                {
                    RecoveryDialogResult result = await RecoveryKeyDialog.ShowAsync(recoveryKey, warned);
                    if (result.SavedConfirmed)
                    {
                        break;
                    }
                    warned = true;
                }
                await ViewModel.FinishSetupAsync();
                if (!_lifetime.IsCurrent(generation))
                {
                    return; // O04：用户已离开，导航与刷新都属于旧页面
                }
                SyncNavigation.GoAfterAuth(Frame, 2);
            }
            catch (Exception)
            {
                // 对话框/导航异常不拖垮页面：复位忙碌态，密钥可经再次创建或解锁流程找回。
            }
            finally
            {
                if (_lifetime.IsCurrent(generation))
                {
                    RefreshAll();
                }
            }
        }

        private void RefreshError()
        {
            string message = ViewModel.ErrorMessage;
            ErrorText.Text = message ?? string.Empty;
            ErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void RefreshBusy()
        {
            bool busy = ViewModel.IsBusy;
            Working.IsActive = busy;
            Working.Message = busy ? ViewModel.BusyMessage : string.Empty;
            SubmitButton.IsEnabled = !busy && ViewModel.SubmitCommand.CanExecute(null);
        }
    }
}
