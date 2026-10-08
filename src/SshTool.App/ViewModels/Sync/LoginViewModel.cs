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
    // fix/login-feedback（真机反馈「登录没有报错也没有反应」）：
    //  - 任何失败都必须落到一条非空、本地化的 ErrorMessage 上（AsyncCommand 兜底不再只记日志）；
    //  - 提交按钮不再因「邮箱/密码为空」而静默禁用：始终可点，由校验给出提示；
    //  - 已登录（含「登录已生效但登录后的保险库探测/缓存绑定失败」）时直接继续路由，
    //    不再让下一次点击撞上「已登录，请先退出」而停在登录页。
    public sealed class LoginViewModel : ViewModelBase
    {
        private const string GenericErrorFallback = "Sign-in failed ({0}). Please try again.";

        private readonly SyncCoordinator _sync;
        private readonly Func<bool> _isSignedIn;
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
                              bool httpFallback = false, Func<bool> isSignedIn = null)
        {
            _sync = sync;
            _isSignedIn = isSignedIn;
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

        // fix/auth-audit：被动登出（会话被吊销 / 过期）或修改登录密码后回到登录页时说明原因。
        // 复用页头下方的提示条；已有错误时不覆盖，切换登录 / 注册页签或提交时自然清除。
        public void ShowNotice(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || HasError)
            {
                return;
            }
            ErrorMessage = text;
            RaisePropertyChanged("HasError");
        }

        public void SetRegisterMode(bool register)
        {
            IsRegisterMode = register;
            ErrorMessage = null;
            RaisePropertyChanged("HasError");
            SubmitCommand.RaiseCanExecuteChanged();
        }

        // fix/login-feedback：只在执行中禁用（防重复提交）。此前邮箱/密码为空时按钮被静默禁用，
        // 真机上禁用态不显眼，用户点了没有任何反馈；现在始终可点，由 LoginFormValidator 给出提示。
        private bool CanSubmit()
        {
            return !IsBusy;
        }

        private async Task SubmitAsync()
        {
            ResourceLoader loader = TryGetLoader();
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
            // 已登录（如另一处入口刚登录过）：每次登录都会在服务端新建设备（03 §11-10），
            // 不再发请求，直接按状态继续路由——此前这里会报「已登录，请先退出」并停在本页。
            if (IsSignedInNow())
            {
                LogInfo("already signed in, routing");
                RaiseSucceeded(loader);
                return;
            }
            IsBusy = true;
            ErrorMessage = null;
            RaisePropertyChanged("HasError");
            bool authenticated = false;
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
                LogInfo(IsRegisterMode ? "registered" : "signed in");
                authenticated = true;
                // fix/auth-audit：密码已用完，不在内存里多留（页面随后离开）。
                _password = string.Empty;
                _confirmPassword = string.Empty;
            }
            catch (Exception ex)
            {
                LogWarning("login/register failed " + ex.GetType().Name);
                if (IsSignedInNow())
                {
                    // 凭据已被服务端接受、会话已落盘，失败的是登录后的本地步骤（保险库缓存绑定等）：
                    // 继续路由（状态页会显示同步错误），而不是停在登录页让下一次点击撞上「已登录」。
                    LogWarning("signed in despite post-auth failure, routing");
                    authenticated = true;
                }
                else
                {
                    ErrorMessage = Describe(ex, loader);
                    RaisePropertyChanged("HasError");
                }
            }
            finally
            {
                IsBusy = false;
            }
            if (authenticated)
            {
                RaiseSucceeded(loader);
            }
        }

        // 导航放在 try 之外：导航本身失败（目标页构造异常等）不能被当成「登录失败」，
        // 也不能静默——给出通用失败文案（会话已保存，返回后重新进入即可继续）。
        private void RaiseSucceeded(ResourceLoader loader)
        {
            EventHandler handler = LoginSucceeded;
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                LogWarning("post-login navigation failed " + ex.GetType().Name);
                ErrorMessage = GenericError(ex, loader);
                RaisePropertyChanged("HasError");
            }
        }

        private bool IsSignedInNow()
        {
            if (_isSignedIn == null)
            {
                return false;
            }
            try
            {
                return _isSignedIn();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static ResourceLoader TryGetLoader()
        {
            try
            {
                return ResourceLoader.GetForCurrentView();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void ShowError(ResourceLoader loader, string key)
        {
            ErrorMessage = GetString(loader, key, key);
            RaisePropertyChanged("HasError");
        }

        // AsyncCommand 兜底：SubmitAsync 之外抛出的意外（资源加载、校验、状态读取……）。
        // fix/login-feedback：此前这里只记日志——用户看到的就是「点了登录，没有报错也没有反应」。
        private void OnSubmitError(Exception ex)
        {
            LogWarning("submit failed " + (ex != null ? ex.GetType().Name : "null"));
            try
            {
                IsBusy = false;
                ErrorMessage = GenericError(ex, TryGetLoader());
                RaisePropertyChanged("HasError");
            }
            catch (Exception)
            {
            }
        }

        private static string GenericError(Exception ex, ResourceLoader loader)
        {
            string template = GetString(loader, LoginErrorKeys.UnexpectedKey, GenericErrorFallback);
            try
            {
                return string.Format(CultureInfo.InvariantCulture, template, LoginErrorKeys.Diagnostic(ex));
            }
            catch (FormatException)
            {
                return template;
            }
        }

        // 错误码中文映射：Api_<CODE> 查 resw；RATE_LIMITED 用 RetryAfterMs 填充秒数；
        // 推断码（HTTP_<status> 等 CodeUnknown）无 resw 键，回退服务端原文。
        // fix/login-feedback：先经 Core 的 LoginErrorKeys 判定（超时/连接失败/https 已降级待重试/
        // 意外异常各有专门文案）；结果保证非空（空串会让错误区折叠，等于没报错）。
        internal static string Describe(Exception ex, ResourceLoader loader)
        {
            string text;
            try
            {
                text = DescribeCore(ex, loader);
            }
            catch (Exception)
            {
                text = null;
            }
            return string.IsNullOrWhiteSpace(text) ? GenericError(ex, loader) : text;
        }

        private static string DescribeCore(Exception ex, ResourceLoader loader)
        {
            if (ex == null)
            {
                return null;
            }
            string overrideKey = LoginErrorKeys.OverrideKey(ex);
            if (overrideKey != null)
            {
                if (string.Equals(overrideKey, LoginErrorKeys.UnexpectedKey, StringComparison.Ordinal))
                {
                    return GenericError(ex, loader);
                }
                return GetString(loader, overrideKey, null);
            }
            if (ex is SshTool.Core.Sync.SyncOperationException)
            {
                return VaultErrorText.Describe(ex, loader);
            }
            ApiError api = ex as ApiError;
            if (api == null)
            {
                return null;
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
            string text = VaultErrorText.CodeText(api, loader);
            if (string.IsNullOrEmpty(text))
            {
                text = VaultErrorText.FallbackText(api, loader);
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
            if (loader == null)
            {
                return fallback;
            }
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
