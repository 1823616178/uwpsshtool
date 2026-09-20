using System;
using System.ComponentModel;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels.Sync;
using SshTool.Core.Sync;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views.Sync
{
    // U16 解锁页（02-UI-DESIGN.md §5.13 VaultUnlockPage）。
    // 密码框不做 x:Bind（事件写入内存字段）；恢复密钥用等宽 TextBox，输入不回写控件，
    // 提交与 CanSubmit 时经 RecoveryKeyInput 规范化。成功回状态页并同步。
    public sealed partial class VaultUnlockPage : Page
    {
        public VaultUnlockPage()
        {
            ViewModel = CreateViewModel();
            this.InitializeComponent();
            ViewModel.SubmitCommand.CanExecuteChanged += OnCanExecuteChanged;
            ViewModel.PropertyChanged += OnViewModelChanged;
            RefreshMode();
            RefreshBusy();
        }

        public VaultUnlockViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.Unlocked += OnUnlocked;
            // 已就绪（如解锁后按返回键回到本页）：不留在此页，去状态路由。
            if (VaultIsReady())
            {
                SyncNavigation.GoAfterAuth(Frame, 1);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ViewModel.Unlocked -= OnUnlocked;
            base.OnNavigatedFrom(e);
        }

        private static VaultUnlockViewModel CreateViewModel()
        {
            AppServices services = AppServices.Current;
            SyncCoordinator sync = services != null ? services.Sync : null;
            return new VaultUnlockViewModel(sync);
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

        private void OnPasswordTabClick(object sender, RoutedEventArgs e)
        {
            ViewModel.SetMode(VaultUnlockMethod.Password);
        }

        private void OnRecoveryTabClick(object sender, RoutedEventArgs e)
        {
            ViewModel.SetMode(VaultUnlockMethod.Recovery);
        }

        private void OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            ViewModel.SyncPassword = SyncPasswordBox.Password;
        }

        private void OnRecoveryChanged(object sender,TextChangedEventArgs e)
        {
            ViewModel.RecoveryInput = RecoveryBox.Text;
        }

        private void OnSubmitClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SubmitCommand.CanExecute(null))
            {
                ViewModel.SubmitCommand.Execute(null);
            }
        }

        private void OnUnlocked(object sender, EventArgs e)
        {
            // GoAfterAuth 按当前状态路由（解锁后 ready → 状态占位页）；剪掉本页与其下登录页，
            // 返回键直达主页。
            SyncNavigation.GoAfterAuth(Frame, 2);
        }

        private void OnViewModelChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null || string.IsNullOrEmpty(e.PropertyName))
            {
                RefreshAll();
                return;
            }
            if (string.Equals(e.PropertyName, "Mode", StringComparison.Ordinal))
            {
                RefreshMode();
            }
            else if (string.Equals(e.PropertyName, "ErrorMessage", StringComparison.Ordinal)
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
            RefreshMode();
            RefreshError();
            RefreshBusy();
        }

        private void RefreshMode()
        {
            bool recovery = ViewModel.Mode == VaultUnlockMethod.Recovery;
            RecoveryPanel.Visibility = recovery ? Visibility.Visible : Visibility.Collapsed;
            PasswordTabButton.IsEnabled = !recovery;
            RecoveryTabButton.IsEnabled = recovery;
            RefreshError();
            RefreshBusy();
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
