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
    // U20 修改登录密码页（02-UI-DESIGN.md §5.13）。成功后清本地并回登录页。
    public sealed partial class ChangeLoginPasswordPage : Page
    {
        public ChangeLoginPasswordPage()
        {
            this.InitializeComponent();
        }

        public ChangeLoginPasswordViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel = CreateViewModel();
            ViewModel.SubmitCommand.CanExecuteChanged += OnCanExecuteChanged;
            ViewModel.PropertyChanged += OnViewModelChanged;
            ViewModel.PasswordChanged += OnPasswordChanged;
            ExplanationText.Text = Localized.Get("ChangeLoginPassword_Explanation",
                "After the change, every device is signed out and must sign in again with the new password.");
            BottomBar.PrimaryText = Localized.Get("ChangeLogin_Submit", "修改密码");
            RefreshError();
            RefreshBusy();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.SubmitCommand.CanExecuteChanged -= OnCanExecuteChanged;
                ViewModel.PropertyChanged -= OnViewModelChanged;
                ViewModel.PasswordChanged -= OnPasswordChanged;
            }
            base.OnNavigatedFrom(e);
        }

        private static ChangeLoginPasswordViewModel CreateViewModel()
        {
            AppServices services = AppServices.Current;
            SyncCoordinator sync = services != null ? services.Sync : null;
            return new ChangeLoginPasswordViewModel(sync);
        }

        private void OnCurrentChanged(object sender, RoutedEventArgs e)
        {
            ViewModel.CurrentPassword = CurrentBox.Password;
        }

        private void OnNewChanged(object sender, RoutedEventArgs e)
        {
            ViewModel.NewPassword = NewBox.Password;
        }

        private void OnConfirmChanged(object sender, RoutedEventArgs e)
        {
            ViewModel.ConfirmPassword = ConfirmBox.Password;
        }

        private void OnSubmitClick(object sender, EventArgs e)
        {
            SubmitFromUi();
        }

        // fix/auth-audit：回车键链（当前密码 → 新密码 → 确认 → 提交），与登录页一致。
        // 此前没有回车处理，软键盘弹出时底部操作条收起，用户找不到提交入口。
        // 只接 KeyDown 并标记 Handled，避免回车抬起落到下一个刚获得焦点的框上。
        private void OnCurrentKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }
            e.Handled = true;
            NewBox.Focus(FocusState.Programmatic);
        }

        private void OnNewKeyDown(object sender, KeyRoutedEventArgs e)
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
            SubmitFromUi();
        }

        // 提交前从控件回读（个别输入法组合态下 PasswordChanged 可能漏发最后一次）。
        private void SubmitFromUi()
        {
            if (ViewModel == null)
            {
                return;
            }
            ViewModel.CurrentPassword = CurrentBox.Password;
            ViewModel.NewPassword = NewBox.Password;
            ViewModel.ConfirmPassword = ConfirmBox.Password;
            if (ViewModel.SubmitCommand.CanExecute(null))
            {
                ViewModel.SubmitCommand.Execute(null);
            }
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


        private void OnPasswordChanged(object sender, EventArgs e)
        {
            CurrentBox.Password = string.Empty;
            NewBox.Password = string.Empty;
            ConfirmBox.Password = string.Empty;
            // 成功后回状态页，状态页随即转去登录页（登录页会提示「密码已修改，请用新密码重新登录」）。
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
