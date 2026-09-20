using System;
using System.ComponentModel;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.App.ViewModels.Sync;
using SshTool.Core.Sync;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views.Sync
{
    // U15 登录/注册页（02-UI-DESIGN.md §5.13）。
    // PasswordBox 不做 x:Bind：由 PasswordChanged 事件写入 ViewModel 内存字段。
    // 成功后暂跳 PlaceholderPage（U17 建 AccountSyncPage 后改为导航状态页）。
    public sealed partial class LoginPage : Page
    {
        private readonly ResourceLoader _loader = ResourceLoader.GetForCurrentView();

        public LoginPage()
        {
            ViewModel = CreateViewModel();
            this.InitializeComponent();
            ViewModel.SubmitCommand.CanExecuteChanged += OnCanExecuteChanged;
            RefreshMode();
            RefreshHttpBanner();
            DeviceBox.Text = ViewModel.DeviceName;
        }

        public LoginViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // 已登录不再重复登录（每次登录都会新建设备，见 03-SYNC-PROTOCOL.md §11-10）：
            // 直接去状态占位页（U17 后改为 AccountSyncPage）。
            if (IsSignedIn())
            {
                GoStatusPlaceholder();
                return;
            }
            ViewModel.PropertyChanged += OnViewModelChanged;
            ViewModel.LoginSucceeded += OnLoginSucceeded;
            RefreshMode();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ViewModel.PropertyChanged -= OnViewModelChanged;
            ViewModel.LoginSucceeded -= OnLoginSucceeded;
            base.OnNavigatedFrom(e);
        }

        private static LoginViewModel CreateViewModel()
        {
            AppServices services = AppServices.Current;
            SyncCoordinator sync = services != null ? services.Sync : null;
            string defaultName = "Lumia";
            try
            {
                defaultName = new UwpDeviceDescriptorProvider().GetDescriptor().Name;
            }
            catch (Exception)
            {
                // 取不到设备名不影响登录：回退固定名（Provider 内部同样有回退，此处防设计器）。
            }
            return new LoginViewModel(
                sync,
                defaultName,
                AppConfig.Current.SyncApiBaseUrl,
                AppConfig.Current.AllowHttp);
        }

        private static bool IsSignedIn()
        {
            try
            {
                AppServices services = AppServices.Current;
                return services != null && services.Auth != null && services.Auth.Session.Authenticated;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void OnLoginTabClick(object sender, RoutedEventArgs e)
        {
            ViewModel.SetRegisterMode(false);
        }

        private void OnRegisterTabClick(object sender, RoutedEventArgs e)
        {
            ViewModel.SetRegisterMode(true);
        }

        private void OnEmailChanged(object sender, TextChangedEventArgs e)
        {
            ViewModel.Email = EmailBox.Text;
        }

        private void OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            ViewModel.Password = PasswordBox.Password;
        }

        private void OnConfirmChanged(object sender, RoutedEventArgs e)
        {
            ViewModel.ConfirmPassword = ConfirmBox.Password;
        }

        private void OnInviteChanged(object sender, TextChangedEventArgs e)
        {
            ViewModel.InviteCode = InviteBox.Text;
        }

        private void OnDeviceChanged(object sender, TextChangedEventArgs e)
        {
            ViewModel.DeviceName = DeviceBox.Text;
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
            if (string.Equals(e.PropertyName, "IsRegisterMode", StringComparison.Ordinal))
            {
                RefreshMode();
            }
            else if (string.Equals(e.PropertyName, "ErrorMessage", StringComparison.Ordinal)
                || string.Equals(e.PropertyName, "HasError", StringComparison.Ordinal))
            {
                RefreshError();
            }
            else if (string.Equals(e.PropertyName, "IsBusy", StringComparison.Ordinal))
            {
                RefreshBusy();
            }
        }

        private void OnLoginSucceeded(object sender, EventArgs e)
        {
            GoStatusPlaceholder();
        }

        private void OnCanExecuteChanged(object sender, EventArgs e)
        {
            RefreshBusy();
        }

        private void GoStatusPlaceholder()
        {
            // U17 未做：先跳占位页并注明目标里程碑；U17 建 AccountSyncPage 后改跳状态页。
            Frame.Navigate(typeof(PlaceholderPage), new PlaceholderArgs("同步状态", "U17"));
        }

        private void RefreshAll()
        {
            RefreshMode();
            RefreshError();
            RefreshBusy();
        }

        private void RefreshMode()
        {
            bool register = ViewModel.IsRegisterMode;
            RegisterPanel.Visibility = register ? Visibility.Visible : Visibility.Collapsed;
            PasswordHint.Visibility = register ? Visibility.Visible : Visibility.Collapsed;
            LoginTabButton.IsEnabled = !register;
            RegisterTabButton.IsEnabled = register;
            SubmitButton.Content = GetString(register ? "Login_SubmitRegister" : "Login_SubmitLogin",
                register ? "注册" : "登录");
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
            Working.Message = busy
                ? GetString(ViewModel.IsRegisterMode ? "Login_WorkingRegister" : "Login_WorkingLogin",
                    ViewModel.IsRegisterMode ? "正在注册…" : "正在登录…")
                : string.Empty;
            SubmitButton.IsEnabled = !busy && ViewModel.SubmitCommand.CanExecute(null);
        }

        private void RefreshHttpBanner()
        {
            HttpBanner.Visibility = ViewModel.ShowHttpBanner ? Visibility.Visible : Visibility.Collapsed;
        }

        private string GetString(string key, string fallback)
        {
            try
            {
                string value = _loader.GetString(key);
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            catch (Exception)
            {
            }
            return fallback;
        }
    }
}
