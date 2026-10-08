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
    // U20 注销账号 ViewModel（02-UI-DESIGN.md §5.13）：登录密码 + 输入 DELETE 确认 →
    // Coordinator.DeleteAccountAsync；成功后清本地并回登录页。
    // 脱敏：密码只存内存字段；日志只记相位与异常类型名。
    public sealed class DeleteAccountViewModel : ViewModelBase
    {
        private readonly SyncCoordinator _sync;
        private readonly ResourceLoader _loader;

        private string _password = string.Empty;
        private string _confirmText = string.Empty;
        private string _errorMessage;
        private bool _isBusy;
        private bool _succeeded;

        public event EventHandler AccountDeleted;

        public DeleteAccountViewModel(SyncCoordinator sync)
        {
            _sync = sync;
            _loader = ResourceLoader.GetForCurrentView();
            SetDispatcherPost(action => DispatcherHelper.Post(action));
            SubmitCommand = new AsyncCommand(SubmitAsync, () => CanSubmit(), OnSubmitError);
        }

        public AsyncCommand SubmitCommand { get; private set; }

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

        // 必须输入「DELETE」才可提交。
        public string ConfirmText
        {
            get { return _confirmText; }
            set
            {
                if (SetProperty(ref _confirmText, value ?? string.Empty))
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

        // fix/auth-audit：只在执行中 / 已成功时禁用。此前密码为空或确认词不完全等于 "DELETE"
        // （输入法常自动补尾随空格）时按钮静默变灰；现在由 AccountFormValidator 给出具体提示。
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
            string invalidKey = AccountFormValidator.ValidateDeleteAccount(_password, _confirmText);
            if (invalidKey != null)
            {
                ShowError(invalidKey);
                return;
            }
            ErrorMessage = null;
            IsBusy = true;
            try
            {
                await _sync.DeleteAccountAsync(_password).ConfigureAwait(true);
                LogInfo("account deleted");
                Succeeded = true;
                IsBusy = false;
                AccountDeleted?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                IsBusy = false;
                LogWarning("delete account failed " + ex.GetType().Name);
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
            ErrorMessage = string.IsNullOrWhiteSpace(text) ? GetString("DeleteAccount_Failed", "DeleteAccount_Failed") : text;
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
            ShowError("DeleteAccount_Failed");
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
                Logger.Log(LogLevel.Info, "DeleteAccount", message);
            }
            catch (Exception)
            {
            }
        }

        private void LogWarning(string message)
        {
            try
            {
                Logger.Log(LogLevel.Warning, "DeleteAccount", message);
            }
            catch (Exception)
            {
            }
        }
    }
}
