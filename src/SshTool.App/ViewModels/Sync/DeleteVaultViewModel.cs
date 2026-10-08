using System;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.Core.Mvvm;
using SshTool.Core.Sync;
using Windows.ApplicationModel.Resources;

namespace SshTool.App.ViewModels.Sync
{
    // U20 删除云端保险库 ViewModel（02-UI-DESIGN.md §5.13 / §7.1）：登录密码确认 →
    // Coordinator.DeleteVaultAsync；成功后本地保留配置、云端库与历史删除。
    // 脱敏：密码只存内存字段；日志只记相位与异常类型名。
    public sealed class DeleteVaultViewModel : ViewModelBase
    {
        private readonly SyncCoordinator _sync;
        private readonly ResourceLoader _loader;

        private string _password = string.Empty;
        private string _errorMessage;
        private bool _isBusy;
        private bool _succeeded;

        public event EventHandler VaultDeleted;

        public DeleteVaultViewModel(SyncCoordinator sync)
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
            if (_isBusy || _succeeded || _sync == null)
            {
                return false;
            }
            return !string.IsNullOrEmpty(_password);
        }

        private async Task SubmitAsync()
        {
            if (_sync == null)
            {
                ShowError("Vault_SyncUnavailable");
                return;
            }
            ErrorMessage = null;
            IsBusy = true;
            try
            {
                await _sync.DeleteVaultAsync(_password).ConfigureAwait(true);
                LogInfo("cloud vault deleted");
                Succeeded = true;
                IsBusy = false;
                VaultDeleted?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                IsBusy = false;
                LogWarning("delete vault failed " + ex.GetType().Name);
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
            ShowError("DeleteVault_Failed");
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
                Logger.Log(LogLevel.Info, "DeleteVault", message);
            }
            catch (Exception)
            {
            }
        }

        private void LogWarning(string message)
        {
            try
            {
                Logger.Log(LogLevel.Warning, "DeleteVault", message);
            }
            catch (Exception)
            {
            }
        }
    }
}
