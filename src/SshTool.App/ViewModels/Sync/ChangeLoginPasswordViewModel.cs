using System;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.Core.Mvvm;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Auth;
using Windows.ApplicationModel.Resources;

namespace SshTool.App.ViewModels.Sync
{
    // U20 修改登录密码 ViewModel（02-UI-DESIGN.md §5.13）。
    // 当前密码 + 新密码 ×2 → Coordinator.ChangeAccountPasswordAsync（成功后清本地，需重新登录）；
    // 成功后回登录页。
    // 脱敏：密码只存内存字段；日志只记相位与异常类型名。
    public sealed class ChangeLoginPasswordViewModel : ViewModelBase
    {
        private readonly SyncCoordinator _sync;
        private readonly ResourceLoader _loader;

        private string _currentPassword = string.Empty;
        private string _newPassword = string.Empty;
        private string _confirmPassword = string.Empty;
        private string _errorMessage;
        private bool _isBusy;
        private bool _succeeded;

        // 修改成功（需重新登录）：页面回登录页。
        public event EventHandler PasswordChanged;

        public ChangeLoginPasswordViewModel(SyncCoordinator sync)
        {
            _sync = sync;
            _loader = ResourceLoader.GetForCurrentView();
            SetDispatcherPost(action => DispatcherHelper.Post(action));
            SubmitCommand = new AsyncCommand(SubmitAsync, () => CanSubmit(), OnSubmitError);
        }

        public AsyncCommand SubmitCommand { get; private set; }

        public string CurrentPassword
        {
            get { return _currentPassword; }
            set
            {
                if (SetProperty(ref _currentPassword, value ?? string.Empty))
                {
                    SubmitCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string NewPassword
        {
            get { return _newPassword; }
            set
            {
                if (SetProperty(ref _newPassword, value ?? string.Empty))
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

        public string ErrorMessage
        {
            get { return _errorMessage; }
            private set
            {
                if (SetProperty(ref _errorMessage, value))
                {
                    RaisePropertyChanged("HasError");
                }
            }
        }

        public bool HasError
        {
            get { return !string.IsNullOrEmpty(_errorMessage); }
        }

        public bool IsBusy
        {
            get { return _isBusy; }
            private set { SetProperty(ref _isBusy, value); }
        }

        public bool Succeeded
        {
            get { return _succeeded; }
            private set { SetProperty(ref _succeeded, value); }
        }

        private bool CanSubmit()
        {
            if (_isBusy || _succeeded)
            {
                return false;
            }
            return !string.IsNullOrEmpty(_currentPassword)
                && !string.IsNullOrEmpty(_newPassword)
                && !string.IsNullOrEmpty(_confirmPassword);
        }

        private async Task SubmitAsync()
        {
            if (_sync == null)
            {
                ShowError("Vault_SyncUnavailable");
                return;
            }
            if (_newPassword.Length < LoginFormValidator.MinRegisterPasswordLength)
            {
                ShowError("ChangeLogin_TooShort");
                return;
            }
            if (!string.Equals(_newPassword, _confirmPassword, StringComparison.Ordinal))
            {
                ShowError("Login_PasswordMismatch");
                return;
            }
            ErrorMessage = null;
            IsBusy = true;
            try
            {
                await _sync.ChangeAccountPasswordAsync(_currentPassword, _newPassword)
                    .ConfigureAwait(true);
                LogInfo("登录密码已修改");
                Succeeded = true;
                IsBusy = false;
                PasswordChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                IsBusy = false;
                LogWarning("修改登录密码失败 " + ex.GetType().Name);
                ShowError(DescribeError(ex));
            }
        }

        private string DescribeError(Exception ex)
        {
            return VaultErrorText.Describe(ex, _loader);
        }

        private void ShowError(string errorKey)
        {
            ErrorMessage = GetString(errorKey, errorKey);
            RaisePropertyChanged("HasError");
            RaisePropertyChanged("ErrorMessage");
        }

        private void OnSubmitError(Exception ex)
        {
            ShowError("ChangeLogin_Failed");
            LogWarning("提交异常 " + ex.GetType().Name);
        }

        private string GetString(string key, string fallback)
        {
            try
            {
                string value = _loader.GetString(key);
                return string.IsNullOrEmpty(value) ? fallback : value;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private void LogInfo(string message)
        {
            try
            {
                Logger.Log(LogLevel.Info, "ChangeLogin", message);
            }
            catch (Exception)
            {
            }
        }

        private void LogWarning(string message)
        {
            try
            {
                Logger.Log(LogLevel.Warning, "ChangeLogin", message);
            }
            catch (Exception)
            {
            }
        }
    }
}
