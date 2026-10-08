using System;
using System.Globalization;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.Core.Mvvm;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Api;
using SshTool.Core.Sync.Auth;
using Windows.ApplicationModel.Resources;

namespace SshTool.App.ViewModels.Sync
{
    // U15 登录/注册页 ViewModel（02-UI-DESIGN.md §5.13）。
    // 经 S14 接线的 SyncCoordinator.RegisterAsync/LoginAsync 调用（含 AfterAuthenticated
    // 保险库探测；AuthService 未接入组合根，此处不用，见 AppServices.BuildSyncStack）。
    // 脱敏：密码只存内存字段，日志只记操作相位与异常类型名，绝不记录密码/邀请码/token。
    // 本文件只用 15063 基线 API（ResourceLoader.GetForCurrentView 在 15063 可用）。
    public sealed class LoginViewModel : ViewModelBase
    {
        private readonly SyncCoordinator _sync;
        private bool _isRegisterMode;
        private string _email = string.Empty;
        private string _password = string.Empty;
        private string _confirmPassword = string.Empty;
        private string _inviteCode = string.Empty;
        private string _deviceName;
        private string _errorMessage;
        private bool _isBusy;

        // 提交成功（UI 线程触发）：页面导航到状态页。
        // SubmitAsync 全程不 ConfigureAwait(false)，成功续体回到 UI 线程。
        public event EventHandler LoginSucceeded;

        public LoginViewModel(SyncCoordinator sync, string defaultDeviceName, string apiBaseUrl, bool allowHttp,
                              bool httpFallback = false)
        {
            _sync = sync;
            _deviceName = defaultDeviceName ?? string.Empty;
            ShowHttpBanner = IsInsecureHttp(apiBaseUrl, allowHttp, httpFallback);
            SubmitCommand = new AsyncCommand(SubmitAsync, CanSubmit, OnSubmitError);
        }

        public AsyncCommand SubmitCommand { get; private set; }

        // allowHttp 且 URL 为 http 时展示明文风险 Banner（02-UI-DESIGN.md §5.13）。
        public bool ShowHttpBanner { get; private set; }

        public bool IsRegisterMode
        {
            get { return _isRegisterMode; }
            private set { SetProperty(ref _isRegisterMode, value); }
        }

        public string Email
        {
            get { return _email; }
            set
            {
                if (SetProperty(ref _email, value ?? string.Empty))
                {
                    SubmitCommand.RaiseCanExecuteChanged();
                }
            }
        }

        // PasswordBox 不做 x:Bind（密码框绑定会把密码留在绑定管线里），由页面
        // PasswordChanged 事件写入此处内存字段，登出/离开页面即丢弃。
        public string Password
        {
            get { return _password; }
            set
            {
                if (SetProperty(ref _password, value ?? string.Empty))
                {
                    SubmitCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string ConfirmPassword
        {
            get { return _confirmPassword; }
            set
            {
                if (SetProperty(ref _confirmPassword, value ?? string.Empty))
                {
                    SubmitCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string InviteCode
        {
            get { return _inviteCode; }
            set { SetProperty(ref _inviteCode, value ?? string.Empty); }
        }

        public string DeviceName
        {
            get { return _deviceName; }
            set { SetProperty(ref _deviceName, value ?? string.Empty); }
        }

        public string ErrorMessage
        {
            get { return _errorMessage; }
            private set { SetProperty(ref _errorMessage, value); }
        }

        public bool HasError
        {
            get { return !string.IsNullOrEmpty(_errorMessage); }
        }

        public bool IsBusy
        {
            get { return _isBusy; }
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    SubmitCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public void SetRegisterMode(bool register)
        {
            IsRegisterMode = register;
            ErrorMessage = null;
            RaisePropertyChanged("HasError");
            SubmitCommand.RaiseCanExecuteChanged();
        }

        private bool CanSubmit()
        {
            if (IsBusy)
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(_email) || string.IsNullOrEmpty(_password))
            {
                return false;
            }
            if (IsRegisterMode && string.IsNullOrEmpty(_confirmPassword))
            {
                return false;
            }
            return true;
        }

        private async Task SubmitAsync()
        {
            var loader = ResourceLoader.GetForCurrentView();
            string email = (_email ?? string.Empty).Trim();
            string errorKey = IsRegisterMode
                ? LoginFormValidator.ValidateRegister(email, _password, _confirmPassword)
                : LoginFormValidator.ValidateLogin(email, _password);
            if (errorKey != null)
            {
                ShowError(loader, errorKey);
                return;
            }
            if (_sync == null)
            {
                ShowError(loader, "Login_SyncUnavailable");
                return;
            }
            IsBusy = true;
            ErrorMessage = null;
            RaisePropertyChanged("HasError");
            try
            {
                // 设备名为空时传 null，UwpDeviceDescriptorProvider 取系统默认名；
                // 邀请码可选，为空时传 null（字段不出现在请求体中）。
                string deviceName = string.IsNullOrWhiteSpace(_deviceName) ? null : _deviceName.Trim();
                string inviteCode = string.IsNullOrWhiteSpace(_inviteCode) ? null : _inviteCode.Trim();
                if (IsRegisterMode)
                {
                    await _sync.RegisterAsync(email, _password, inviteCode, deviceName).ConfigureAwait(true);
                }
                else
                {
                    await _sync.LoginAsync(email, _password, deviceName).ConfigureAwait(true);
                }
                LogInfo(IsRegisterMode ? "注册成功" : "登录成功");
                EventHandler handler = LoginSucceeded;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                LogWarning("登录/注册失败 " + ex.GetType().Name);
                ErrorMessage = Describe(ex, loader);
                RaisePropertyChanged("HasError");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void ShowError(ResourceLoader loader, string key)
        {
            ErrorMessage = GetString(loader, key, key);
            RaisePropertyChanged("HasError");
        }

        private void OnSubmitError(Exception ex)
        {
            // AsyncCommand 兜底：SubmitAsync 内部已吞掉业务异常，此处只防资源加载等意外。
            try
            {
                LogWarning("提交异常 " + ex.GetType().Name);
            }
            catch (Exception)
            {
            }
        }

        // 错误码中文映射：Api_<CODE> 查 resw；RATE_LIMITED 用 RetryAfterMs 填充秒数；
        // 推断码（HTTP_<status> 等 CodeUnknown）无 resw 键，回退服务端原文。
        internal static string Describe(Exception ex, ResourceLoader loader)
        {
            if (ex == null)
            {
                return string.Empty;
            }
            ApiError api = ex as ApiError;
            if (api == null)
            {
                if (ex is InvalidOperationException)
                {
                    return GetString(loader, "Login_AlreadySignedIn", ex.Message);
                }
                return ex.Message;
            }
            if (string.Equals(api.Code, ApiErrorCatalog.RateLimited, StringComparison.Ordinal))
            {
                if (api.RetryAfterMs.HasValue)
                {
                    long seconds = Math.Max(1L, (api.RetryAfterMs.Value + 999L) / 1000L);
                    string template = GetString(loader, ApiErrorCatalog.ResourceKey(api.Code), null);
                    if (!string.IsNullOrEmpty(template))
                    {
                        return AppendRequestId(
                            string.Format(CultureInfo.InvariantCulture, template, seconds), api.RequestId, loader);
                    }
                }
                return AppendRequestId(GetString(loader, "Login_BusyRetry", api.Message), api.RequestId, loader);
            }
            string text = loader.GetString(ApiErrorCatalog.ResourceKey(api.Code));
            if (string.IsNullOrEmpty(text))
            {
                text = api.Message;
            }
            return AppendRequestId(text, api.RequestId, loader);
        }

        private static string AppendRequestId(string text, string requestId, ResourceLoader loader)
        {
            if (string.IsNullOrEmpty(requestId))
            {
                return text;
            }
            string template = GetString(loader, "Login_RequestId", null);
            string suffix = template != null
                ? string.Format(CultureInfo.InvariantCulture, template, requestId)
                : requestId;
            return text + " (" + suffix + ")";
        }

        private static string GetString(ResourceLoader loader, string key, string fallback)
        {
            try
            {
                string value = loader.GetString(key);
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

        // opt/full-pass：https + httpFallback 时也可能落到明文（服务器无 TLS 会自动降级），
        // 同样提示风险。
        internal static bool IsInsecureHttp(string baseUrl, bool allowHttp, bool httpFallback = false)
        {
            if (!allowHttp || string.IsNullOrWhiteSpace(baseUrl))
            {
                return false;
            }
            string trimmed = baseUrl.Trim();
            if (trimmed.StartsWith("http:", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return httpFallback && trimmed.StartsWith("https:", StringComparison.OrdinalIgnoreCase);
        }

        private void LogInfo(string message)
        {
            try
            {
                Logger.Log(LogLevel.Info, "Login", message);
            }
            catch (Exception)
            {
            }
        }

        private void LogWarning(string message)
        {
            try
            {
                Logger.Log(LogLevel.Warning, "Login", message);
            }
            catch (Exception)
            {
            }
        }
    }
}
