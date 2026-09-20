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
    // U16 建库页 ViewModel（02-UI-DESIGN.md §5.13 VaultSetupPage）。
    // 流程：表单校验（≥8 位 + 确认）→ SyncCoordinator.SetupVaultAsync（Argon2id 数秒，
    // 遮罩「正在生成密钥」；Coordinator 相位转 syncing 时按相位改显「正在同步」）→
    // 页面弹 RecoveryKeyDialog（必须勾选已保存）→ FinishSetupAsync 自动首次同步 →
    // 页面经 SyncNavigation 去状态页。不提供「记住同步密码」：保险库密钥解锁后由
    // VaultCache 持久化（DPAPI），重启无需再次输入。
    // 脱敏：同步密码只存内存字段（PasswordBox 事件写入，页面离开即弃）；日志只记
    // 相位与异常类型名，绝不记录密码/恢复密钥/vaultKey。
    public sealed class VaultSetupViewModel : ViewModelBase
    {
        private readonly SyncCoordinator _sync;
        private string _syncPassword = string.Empty;
        private string _confirmPassword = string.Empty;
        private string _errorMessage;
        private bool _isBusy;
        private string _busyMessage = string.Empty;

        // SetupVaultAsync 成功（UI 线程触发）：页面弹恢复密钥对话框（§5.8）。
        public event EventHandler<string> SetupCompleted;

        public VaultSetupViewModel(SyncCoordinator sync)
        {
            _sync = sync;
            // StateChanged 可能在线程池触发：属性通知统一封送回 UI 线程。
            SetDispatcherPost(action => DispatcherHelper.Post(action));
            SubmitCommand = new AsyncCommand(SubmitAsync, CanSubmit, OnSubmitError);
            // 遮罩文案随 Coordinator 相位驱动（syncing → 正在同步）。VM 与页面同生命周期，
            // Coordinator 是应用级单例，无需退订（与 MainViewModel 同例）。
            if (sync != null)
            {
                sync.StateChanged += OnSyncPhaseChanged;
            }
        }

        public AsyncCommand SubmitCommand { get; private set; }

        // 同步密码不绑定（密码框绑定会把密码留在绑定管线里），由页面 PasswordChanged 事件写入。
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

        // 进度遮罩文案：先「正在生成密钥（约数秒）」（Argon2id），
        // Coordinator 相位转 syncing 时改「正在同步…」。
        public string BusyMessage
        {
            get { return _busyMessage; }
            private set { SetProperty(ref _busyMessage, value); }
        }

        public bool CanSubmit()
        {
            if (IsBusy)
            {
                return false;
            }
            return !string.IsNullOrEmpty(_syncPassword) && !string.IsNullOrEmpty(_confirmPassword);
        }

        private async Task SubmitAsync()
        {
            var loader = ResourceLoader.GetForCurrentView();
            string errorKey = VaultFormValidator.ValidateSetup(_syncPassword, _confirmPassword);
            if (errorKey != null)
            {
                ShowError(loader, errorKey);
                return;
            }
            if (_sync == null)
            {
                ShowError(loader, "Vault_SyncUnavailable");
                return;
            }
            IsBusy = true;
            ErrorMessage = null;
            RaisePropertyChanged("HasError");
            BusyMessage = GetString(loader, "Vault_WorkingSetup", "正在生成密钥（约数秒）…");
            try
            {
                // CreateAsync（Argon2id，后台线程）+ 先落盘 pending 再 POST；返回恢复密钥。
                string recoveryKey = await _sync.SetupVaultAsync(_syncPassword).ConfigureAwait(true);
                LogInfo("保险库创建成功");
                EventHandler<string> handler = SetupCompleted;
                if (handler != null)
                {
                    handler(this, recoveryKey);
                }
            }
            catch (Exception ex)
            {
                LogWarning("建库失败 " + ex.GetType().Name);
                ErrorMessage = VaultErrorText.Describe(ex, loader);
                RaisePropertyChanged("HasError");
            }
            finally
            {
                IsBusy = false;
                BusyMessage = string.Empty;
            }
        }

        // 对话框确认后调用：自动首次同步（错误已由 HandleSyncError 落状态，此处吞掉重抛）。
        public async Task FinishSetupAsync()
        {
            if (_sync == null)
            {
                return;
            }
            var loader = ResourceLoader.GetForCurrentView();
            IsBusy = true;
            BusyMessage = GetString(loader, "Vault_WorkingSync", "正在同步…");
            try
            {
                await _sync.SyncNowAsync().ConfigureAwait(true);
                LogInfo("首次同步完成");
            }
            catch (Exception ex)
            {
                LogWarning("首次同步失败 " + ex.GetType().Name);
            }
            finally
            {
                IsBusy = false;
                BusyMessage = string.Empty;
            }
        }

        // Coordinator 相位驱动遮罩文案：建库/同步期间进入 syncing 相位时改显「正在同步」。
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
                Logger.Log(LogLevel.Info, "VaultSetup", message);
            }
            catch (Exception)
            {
            }
        }

        private void LogWarning(string message)
        {
            try
            {
                Logger.Log(LogLevel.Warning, "VaultSetup", message);
            }
            catch (Exception)
            {
            }
        }
    }
}
