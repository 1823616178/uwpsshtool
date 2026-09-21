using System;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.Core.Mvvm;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Vault;
using Windows.ApplicationModel.Resources;

namespace SshTool.App.ViewModels.Sync
{
    // U20 轮换模式：关闭敏感同步（开→关） / 修改同步密码（§5.13 安全清理）。
    public enum SecurityRotateMode
    {
        DisableSensitiveSync,
        ChangeSyncPassword
    }

    // U20 安全清理 / 修改同步密码 ViewModel（02-UI-DESIGN.md §5.13）。
    // 共用页：账号登录密码 + 新同步密码 ×2 → RecoveryKeyDialog；失败回滚开关
    //（Coordinator.RotateSensitiveSyncAsync / ChangeSyncPasswordAsync 内部已回滚偏好，
    // 此处只需将新恢复密钥交对话框，失败时把 UI 开关还原）。
    // 脱敏：登录密码与同步密码只存内存字段（PasswordBox 事件写入，页面离开即弃）；
    // 日志只记相位与异常类型名，绝不记录密码/恢复密钥。
    public sealed class SecurityRotateViewModel : ViewModelBase
    {
        private readonly SyncCoordinator _sync;
        private readonly SecurityRotateMode _mode;
        private readonly ResourceLoader _loader;

        private string _loginPassword = string.Empty;
        private string _syncPassword = string.Empty;
        private string _confirmPassword = string.Empty;
        private string _errorMessage;
        private bool _isBusy;
        private string _busyMessage = string.Empty;

        // 轮换成功（恢复密钥，UI 线程触发）：页面弹 RecoveryKeyDialog。
        public event EventHandler<string> RotationCompleted;

        public SecurityRotateViewModel(SyncCoordinator sync, SecurityRotateMode mode)
        {
            _sync = sync;
            _mode = mode;
            _loader = ResourceLoader.GetForCurrentView();
            SetDispatcherPost(action => DispatcherHelper.Post(action));

            SubmitCommand = new AsyncCommand(SubmitAsync, () => CanSubmit(), OnSubmitError);
            if (sync != null)
            {
                sync.StateChanged += OnSyncPhaseChanged;
            }
        }

        public AsyncCommand SubmitCommand { get; private set; }

        public SecurityRotateMode Mode
        {
            get { return _mode; }
        }

        public bool IsDisableSensitive
        {
            get { return _mode == SecurityRotateMode.DisableSensitiveSync; }
        }

        public bool IsChangePassword
        {
            get { return _mode == SecurityRotateMode.ChangeSyncPassword; }
        }

        // 密码框不做 x:Bind；由页面 PasswordChanged 事件写入内存字段。
        public string LoginPassword
        {
            get { return _loginPassword; }
            set
            {
                if (SetProperty(ref _loginPassword, value ?? string.Empty))
                {
                    SubmitCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public string SyncPassword
        {
            get { return _syncPassword; }
            set
            {
                if (SetProperty(ref _syncPassword, value ?? string.Empty))
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

        public string BusyMessage
        {
            get { return _busyMessage; }
            private set { SetProperty(ref _busyMessage, value); }
        }

        private bool CanSubmit()
        {
            if (_isBusy)
            {
                return false;
            }
            return !string.IsNullOrEmpty(_loginPassword)
                && !string.IsNullOrEmpty(_syncPassword)
                && !string.IsNullOrEmpty(_confirmPassword);
        }

        private void SetError(string errorKey)
        {
            ErrorMessage = GetString(errorKey, errorKey);
        }

        private async Task SubmitAsync()
        {
            string errorKey = VaultFormValidator.ValidateSetup(_syncPassword, _confirmPassword);
            if (errorKey != null)
            {
                SetError(errorKey);
                return;
            }
            if (_sync == null)
            {
                SetError("Vault_SyncUnavailable");
                return;
            }
            ErrorMessage = null;
            BusyMessage = GetString("SecurityRotate_Working", "正在生成新密钥（约数秒）…");
            IsBusy = true;
            try
            {
                string recoveryKey;
                if (_mode == SecurityRotateMode.ChangeSyncPassword)
                {
                    recoveryKey = await _sync.ChangeSyncPasswordAsync(_loginPassword, _syncPassword)
                        .ConfigureAwait(true);
                }
                else
                {
                    var next = _sync.State.Preferences == null
                        ? SyncPreferences.Defaults()
                        : _sync.State.Preferences.Clone();
                    bool wasPasswords = next.SyncPasswords;
                    bool wasPrivateKeys = next.SyncPrivateKeys;
                    next.SyncPasswords = false;
                    next.SyncPrivateKeys = false;
                    try
                    {
                        recoveryKey = await _sync.RotateSensitiveSyncAsync(next, _loginPassword, _syncPassword)
                            .ConfigureAwait(true);
                    }
                    catch (Exception)
                    {
                        // 失败回滚开关：Coordinator 内部已回滚 Preferences，此处无需额外操作。
                        throw;
                    }
                }
                LogInfo("轮换成功");
                IsBusy = false;
                RotationCompleted?.Invoke(this, recoveryKey);
            }
            catch (Exception ex)
            {
                IsBusy = false;
                LogWarning("轮换失败 " + ex.GetType().Name);
                SetError(DescribeError(ex));
            }
        }

        // 失败回滚开关：开→关失败时还原 UI（Coordinator 内部已回滚 Preferences 并上抛，
        // 页面收到异常后将开关翻回）。页面订阅 PropertyChanged 后据此重置。
        public void RollbackSwitch()
        {
            RaisePropertyChanged("EnableSync");
            RaisePropertyChanged("SyncPasswords");
            RaisePropertyChanged("SyncPrivateKeys");
        }

        private string DescribeError(Exception ex)
        {
            return VaultErrorText.Describe(ex, _loader);
        }

        private void OnSyncPhaseChanged(SyncState state)
        {
            if (!_isBusy || state == null || state.Phase != SyncPhase.Syncing)
            {
                return;
            }
            string syncText = null;
            try
            {
                syncText = _loader.GetString("Vault_WorkingSync");
            }
            catch (Exception)
            {
            }
            BusyMessage = string.IsNullOrEmpty(syncText) ? "正在同步…" : syncText;
        }

        private void OnSubmitError(Exception ex)
        {
            SetError("SecurityRotate_Failed");
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
                Logger.Log(LogLevel.Info, "SecurityRotate", message);
            }
            catch (Exception)
            {
            }
        }

        private void LogWarning(string message)
        {
            try
            {
                Logger.Log(LogLevel.Warning, "SecurityRotate", message);
            }
            catch (Exception)
            {
            }
        }
    }
}
