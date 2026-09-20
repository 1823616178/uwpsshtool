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
    // U16 解锁页 ViewModel（02-UI-DESIGN.md §5.13 VaultUnlockPage）。
    // 分段「同步密码 / 恢复密钥」：密码段判空即可；恢复密钥段经 RecoveryKeyInput
    // 规范化（去空白、前缀/校验段大写），格式通过才可提交，校验段复算拦截明显抄错。
    // 成功后自动同步（Coordinator 已处理错误）并回状态页；解锁后的密钥由 VaultCache
    // 持久化，因此不提供「记住同步密码」。
    // 脱敏：密码/恢复密钥只存内存字段，页面离开即弃；日志只记相位与异常类型名。
    public sealed class VaultUnlockViewModel : ViewModelBase
    {
        private readonly SyncCoordinator _sync;
        private VaultUnlockMethod _mode = VaultUnlockMethod.Password;
        private string _syncPassword = string.Empty;
        private string _recoveryInput = string.Empty;
        private string _errorMessage;
        private bool _isBusy;
        private string _busyMessage = string.Empty;

        // 解锁 + 首次同步完成（UI 线程触发）：页面去状态页。
        public event EventHandler Unlocked;

        public VaultUnlockViewModel(SyncCoordinator sync)
        {
            _sync = sync;
            SetDispatcherPost(action => DispatcherHelper.Post(action));
            SubmitCommand = new AsyncCommand(SubmitAsync, CanSubmit, OnSubmitError);
            // 遮罩文案随 Coordinator 相位驱动（syncing → 正在同步）。
            if (sync != null)
            {
                sync.StateChanged += OnSyncPhaseChanged;
            }
        }

        public AsyncCommand SubmitCommand { get; private set; }

        // 解锁分段：同步密码 / 恢复密钥（复用 Core 的 VaultUnlockMethod）。
        public VaultUnlockMethod Mode
        {
            get { return _mode; }
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

        // 恢复密钥原文（TextBox 输入，可含换行/空格）：不回写控件，只在提交与 CanSubmit 时规范化。
        public string RecoveryInput
        {
            get { return _recoveryInput; }
            set
            {
                if (SetProperty(ref _recoveryInput, value ?? string.Empty))
                {
                    SubmitCommand.RaiseCanExecuteChanged();
                }
            }
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

        public string BusyMessage
        {
            get { return _busyMessage; }
            private set { SetProperty(ref _busyMessage, value); }
        }

        public void SetMode(VaultUnlockMethod mode)
        {
            if (_mode == mode)
            {
                return;
            }
            _mode = mode;
            RaisePropertyChanged("Mode");
            ErrorMessage = null;
            RaisePropertyChanged("HasError");
            SubmitCommand.RaiseCanExecuteChanged();
        }

        public bool CanSubmit()
        {
            if (IsBusy)
            {
                return false;
            }
            if (_mode == VaultUnlockMethod.Password)
            {
                return !string.IsNullOrEmpty(_syncPassword);
            }
            // 恢复密钥：格式通过才可提交（去空白 + 格式匹配）。
            return RecoveryKeyInput.HasValidFormat(RecoveryKeyInput.Normalize(_recoveryInput));
        }

        private async Task SubmitAsync()
        {
            var loader = ResourceLoader.GetForCurrentView();
            string secret;
            if (_mode == VaultUnlockMethod.Password)
            {
                string errorKey = VaultFormValidator.ValidateUnlockPassword(_syncPassword);
                if (errorKey != null)
                {
                    ShowError(loader, errorKey);
                    return;
                }
                secret = _syncPassword;
            }
            else
            {
                string normalized;
                string errorKey = RecoveryKeyInput.Validate(_recoveryInput, out normalized);
                if (errorKey != null)
                {
                    ShowError(loader, errorKey);
                    return;
                }
                secret = normalized;
            }
            if (_sync == null)
            {
                ShowError(loader, "Vault_SyncUnavailable");
                return;
            }
            IsBusy = true;
            ErrorMessage = null;
            RaisePropertyChanged("HasError");
            BusyMessage = GetString(loader, "Vault_WorkingUnlock", "正在解锁…");
            try
            {
                // 拉信封 → 解包（Argon2id 数秒 / 恢复密钥直解）→ 记 key 并启用。
                await _sync.UnlockVaultAsync(secret, _mode).ConfigureAwait(true);
                LogInfo("保险库解锁成功");
                // 成功回状态页并同步（§5.13）：错误已由 HandleSyncError 落状态，吞掉重抛。
                try
                {
                    await _sync.SyncNowAsync().ConfigureAwait(true);
                    LogInfo("解锁后首次同步完成");
                }
                catch (Exception syncEx)
                {
                    LogWarning("解锁后同步失败 " + syncEx.GetType().Name);
                }
                EventHandler handler = Unlocked;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                LogWarning("解锁失败 " + ex.GetType().Name);
                ErrorMessage = VaultErrorText.Describe(ex, loader);
                RaisePropertyChanged("HasError");
            }
            finally
            {
                IsBusy = false;
                BusyMessage = string.Empty;
            }
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
                syncText = ResourceLoader.GetForCurrentView().GetString("Vault_WorkingSync");
            }
            catch (Exception)
            {
            }
            BusyMessage = string.IsNullOrEmpty(syncText) ? "正在同步…" : syncText;
        }

        private void ShowError(ResourceLoader loader, string key)
        {
            ErrorMessage = GetString(loader, key, key);
            RaisePropertyChanged("HasError");
        }

        private void OnSubmitError(Exception ex)
        {
            // SubmitAsync 内部已吞掉业务异常，此处只防资源加载等意外。
            try
            {
                LogWarning("提交异常 " + ex.GetType().Name);
            }
            catch (Exception)
            {
            }
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

        private void LogInfo(string message)
        {
            try
            {
                Logger.Log(LogLevel.Info, "VaultUnlock", message);
            }
            catch (Exception)
            {
            }
        }

        private void LogWarning(string message)
        {
            try
            {
                Logger.Log(LogLevel.Warning, "VaultUnlock", message);
            }
            catch (Exception)
            {
            }
        }
    }
}
