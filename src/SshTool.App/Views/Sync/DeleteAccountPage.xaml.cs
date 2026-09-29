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
            if (ViewModel.SubmitCommand.CanExecute(null))
            {
                ViewModel.SubmitCommand.Execute(null);
            }
        }

        private void OnAccountDeleted(object sender, EventArgs e)
        {
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
