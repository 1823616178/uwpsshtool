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

        // fix/auth-audit：只在执行中 / 已成功时禁用。此前任一字段为空就静默禁用按钮，
        // 真机上禁用态不显眼、点了毫无反馈；现在由 AccountFormValidator 给出具体提示。
        private bool CanSubmit()
        {
            return !_isBusy && !_succeeded;
        }

        private async Task SubmitAsync()
        {
            if (_sync == null)
            {
                ShowError("Vault_SyncUnavailable");
                return;
            }
            string invalidKey = AccountFormValidator.ValidateChangePassword(
                _currentPassword, _newPassword, _confirmPassword);
            if (invalidKey != null)
            {
                ShowError(invalidKey);
                return;
            }
            ErrorMessage = null;
            IsBusy = true;
            try
            {
                await _sync.ChangeAccountPasswordAsync(_currentPassword, _newPassword)
                    .ConfigureAwait(true);
                LogInfo("login password changed");
                Succeeded = true;
                IsBusy = false;
                PasswordChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                IsBusy = false;
                LogWarning("change login password failed " + ex.GetType().Name);
                ShowText(DescribeError(ex));
            }
        }

        private string DescribeError(Exception ex)
        {
            return VaultErrorText.Describe(ex, _loader);
        }

        // 已本地化的文案直接显示（此前把文案当 resw 键再查一遍，靠查不到时的回退才显示出来）。
        private void ShowText(string text)
        {
            ErrorMessage = string.IsNullOrWhiteSpace(text) ? GetString("ChangeLogin_Failed", "ChangeLogin_Failed") : text;
            RaisePropertyChanged("HasError");
            RaisePropertyChanged("ErrorMessage");
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
            LogWarning("submit failed " + ex.GetType().Name);
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
