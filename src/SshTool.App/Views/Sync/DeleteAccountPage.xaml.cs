using System;
using System.ComponentModel;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels.Sync;
using SshTool.Core.Sync;
using Windows.System;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views.Sync
{
    // U20 注销账号页（02-UI-DESIGN.md §5.13）。成功后清本地并回登录页。
    public sealed partial class DeleteAccountPage : Page
    {
        public DeleteAccountPage()
        {
            this.InitializeComponent();
        }

        public DeleteAccountViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel = CreateViewModel();
            ViewModel.SubmitCommand.CanExecuteChanged += OnCanExecuteChanged;
            ViewModel.PropertyChanged += OnViewModelChanged;
            ViewModel.AccountDeleted += OnAccountDeleted;
            BottomBar.PrimaryText = Localized.Get("DeleteAccount_Submit", "注销账号");
            RefreshError();
            RefreshBusy();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.SubmitCommand.CanExecuteChanged -= OnCanExecuteChanged;
                ViewModel.PropertyChanged -= OnViewModelChanged;
                ViewModel.AccountDeleted -= OnAccountDeleted;
            }
            base.OnNavigatedFrom(e);
        }

        private static DeleteAccountViewModel CreateViewModel()
        {
            AppServices services = AppServices.Current;
            SyncCoordinator sync = services != null ? services.Sync : null;
            return new DeleteAccountViewModel(sync);
        }

        private void OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            ViewModel.Password = PasswordBox.Password;
        }

        private void OnConfirmChanged(object sender, TextChangedEventArgs e)
        {
            ViewModel.ConfirmText = ConfirmBox.Text;
        }

        private void OnSubmitClick(object sender, EventArgs e)
        {
            if (ViewModel == null)
            {
                return;
            }
            // 提交前从控件回读（输入法组合态下 TextChanged/PasswordChanged 可能漏发最后一次）。
            ViewModel.Password = PasswordBox.Password;
            ViewModel.ConfirmText = ConfirmBox.Text;
            if (ViewModel.SubmitCommand.CanExecute(null))
            {
                ViewModel.SubmitCommand.Execute(null);
            }
        }

        // fix/auth-audit：回车：密码 → 确认框；确认框回车只收起软键盘（露出底部红色按钮），
        // 不直接提交——注销不可恢复，留一次明确点击。
        private void OnPasswordKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }
            e.Handled = true;
            ConfirmBox.Focus(FocusState.Programmatic);
        }

        private void OnConfirmKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }
            e.Handled = true;
            HideSoftKeyboard();
        }

        // InputPane.TryHide 自 10586 起可用（低于 15063 基线，无需 ApiInformation 守卫）。
        private static void HideSoftKeyboard()
        {
            try
            {
                InputPane pane = InputPane.GetForCurrentView();
                if (pane != null)
                {
                    pane.TryHide();
                }
            }
            catch (Exception)
            {
            }
        }


        private void OnAccountDeleted(object sender, EventArgs e)
        {
            PasswordBox.Password = string.Empty;
            // 注销成功 → 回登录页。
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
            if (name == "IsBusy")
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
                ErrorPanel.Visibility = Visibility.Visible;
            }
            else
            {
                ErrorText.Text = string.Empty;
                ErrorPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void RefreshBusy()
        {
            bool busy = ViewModel != null && ViewModel.IsBusy;
            BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            BottomBar.IsPrimaryEnabled = ViewModel != null && !busy && ViewModel.SubmitCommand.CanExecute(null);
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
