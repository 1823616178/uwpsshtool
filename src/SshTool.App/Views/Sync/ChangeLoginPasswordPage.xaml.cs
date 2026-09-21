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
            ExplanationText.Text = "修改密码后需要重新登录。";
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

        private void OnSubmitClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SubmitCommand.CanExecute(null))
            {
                ViewModel.SubmitCommand.Execute(null);
            }
        }

        private void OnPasswordChanged(object sender, EventArgs e)
        {
            // 成功后回登录页（需重新登录）。
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
