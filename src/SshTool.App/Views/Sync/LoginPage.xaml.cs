using System;
using System.ComponentModel;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.App.ViewModels.Sync;
using SshTool.Core.Sync;
using Windows.ApplicationModel.Resources;
using Windows.System;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views.Sync
{
    // U15 登录/注册页（02-UI-DESIGN.md §5.13）。
    // PasswordBox 不做 x:Bind：由 PasswordChanged 事件写入 ViewModel 内存字段。
    // 成功后经 SyncNavigation（U16）按保险库状态去建库/解锁/状态占位页。
    // fix/login-feedback：错误区在页头下方（始终可见）；回车键提交（软键盘弹出时底部操作条收起）；
    // 提交前从控件回读一次输入，防止个别输入法组合态下 TextChanged/PasswordChanged 漏发。
    public sealed partial class LoginPage : Page
    {
        // W06：页头返回按钮（与硬件返回键同一条处理链）。
        private void OnHeaderBackRequested(object sender, System.EventArgs e)
        {
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.RequestBack();
            }
        }

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
            // 直接按保险库状态去建库/解锁/状态页。路由器对已登录用户不会给出「登录页」，
            // 不会弹回本页；若因同步栈缺失而不导航（返回 false），则留在本页正常显示表单。
            if (IsSignedIn() && SyncNavigation.GoAfterAuth(Frame, 1))
            {
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
                AppConfig.Current.AllowHttp,
                AppConfig.Current.HttpFallback,
                IsSignedIn);
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

        private void OnSubmitClick(object sender, EventArgs e)
        {
            SubmitFromUi();
        }

        // 回车：邮箱 → 密码；密码（登录模式）/ 确认密码 / 邀请码 / 设备名 → 提交。
        // 只接 KeyDown 并标记 Handled：若接 KeyUp，邮箱框的回车抬起会落到刚获得焦点的密码框上直接提交。
        private void OnEmailKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }
            e.Handled = true;
            PasswordBox.Focus(FocusState.Programmatic);
        }

        private void OnPasswordKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }
            e.Handled = true;
            if (ViewModel.IsRegisterMode)
            {
                ConfirmBox.Focus(FocusState.Programmatic);
                return;
            }
            SubmitFromKeyboard();
        }

        private void OnSubmitKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter)
            {
                return;
            }
            e.Handled = true;
            SubmitFromKeyboard();
        }

        private void SubmitFromKeyboard()
        {
            // 先收起软键盘：让出底部操作条与加载遮罩/错误条的可视空间。
            HideSoftKeyboard();
            SubmitFromUi();
        }

        private void SubmitFromUi()
        {
            PullInputs();
            if (ViewModel.SubmitCommand.CanExecute(null))
            {
                ViewModel.SubmitCommand.Execute(null);
            }
        }

        // 提交前从控件回读：TextChanged/PasswordChanged 是唯一的写入通道，个别输入法组合态
        // （预测词未上屏）下可能漏发最后一次，导致 ViewModel 里的值与屏幕上不一致。
        private void PullInputs()
        {
            ViewModel.Email = EmailBox.Text;
            ViewModel.Password = PasswordBox.Password;
            if (ViewModel.IsRegisterMode)
            {
                ViewModel.ConfirmPassword = ConfirmBox.Password;
                ViewModel.InviteCode = InviteBox.Text;
            }
            ViewModel.DeviceName = DeviceBox.Text;
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
            // 路由器拒绝导航（只会在同步栈缺失 / 会话与登录结果不一致时发生）不能静默：
            // 抛出后由 LoginViewModel.RaiseSucceeded 兜住并显示通用失败文案。
            if (!GoStatusPlaceholder())
            {
                throw new InvalidOperationException("post-login routing declined");
            }
        }

        private void OnCanExecuteChanged(object sender, EventArgs e)
        {
            RefreshBusy();
        }

        private bool GoStatusPlaceholder()
        {
            // U16：按保险库状态路由（missing → 建库页，locked → 解锁页，ready → 状态占位）；
            // 并剪掉登录页自身——U15 原实现直接进占位页，返回键会弹回登录页再被重定向成循环。
            // U17 建 AccountSyncPage 后状态分支改跳真状态页（见 SyncNavigation）。
            return SyncNavigation.GoAfterAuth(Frame, 1);
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
            BottomBar.PrimaryText = GetString(register ? "Login_SubmitRegister" : "Login_SubmitLogin",
                register ? "Register" : "Sign in");
            RefreshError();
            RefreshBusy();
        }

        private void RefreshError()
        {
            string message = ViewModel.ErrorMessage;
            ErrorText.Text = message ?? string.Empty;
            ErrorPanel.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void RefreshBusy()
        {
            bool busy = ViewModel.IsBusy;
            Working.IsActive = busy;
            Working.Message = busy
                ? GetString(ViewModel.IsRegisterMode ? "Login_WorkingRegister" : "Login_WorkingLogin",
                    ViewModel.IsRegisterMode ? "Registering…" : "Signing in…")
                : string.Empty;
            BottomBar.IsPrimaryEnabled = !busy && ViewModel.SubmitCommand.CanExecute(null);
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
