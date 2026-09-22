using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.Core.Mvvm;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Api.Dtos;
using SshTool.Core.Sync.Auth;
using SshTool.Core.Sync.Vault;
using Windows.ApplicationModel.Resources;

namespace SshTool.App.ViewModels.Sync
{
    // U17/U18 同步状态页 ViewModel（02-UI-DESIGN.md §5.13 状态 Pivot + 设备/历史 Pivot）。
    // 消费 AuthService/SyncCoordinator：根据 AuthState / vault / phase 决定路由到登录页、
    // 建库、解锁或状态卡；暴露状态卡文案（相对时间 + 相位）、启用/自动同步开关、同步密码/
    // 私钥开关（开→关进 U20 安全清理），以及设备列表与历史版本（U18）。
    // 脱敏：日志只记操作相位与异常类型名，绝不记录密码/token/恢复密钥。
    public sealed class AccountSyncViewModel : ViewModelBase
    {
        private readonly SyncCoordinator _sync;
        private bool _detached;
        private readonly AuthService _auth;
        private readonly SyncStatePresenter _presenter;
        private readonly ResourceLoader _loader;

        private SyncState _state;
        private bool _isSyncing;
        private string _statusTextKey;
        private string _statusIconKey;
        private bool _statusSpin;
        private string _lastSyncedRelative = string.Empty;
        private string _nextRetryRelative = string.Empty;
        private bool _isLoadingDevices;
        private bool _isLoadingHistory;
        private string _errorMessage = string.Empty;

        public AccountSyncViewModel(SyncCoordinator sync, AuthService auth)
        {
            _sync = sync;
            _auth = auth;
            _presenter = new SyncStatePresenter();
            _loader = ResourceLoader.GetForCurrentView();
            // StateChanged 可能在线程池触发：属性通知统一封送回 UI 线程。
            SetDispatcherPost(action => DispatcherHelper.Post(action));

            SyncNowCommand = new AsyncCommand(SyncNowAsync, () => CanSyncNow(), OnCommandError);
            RefreshDevicesCommand = new AsyncCommand(
                LoadDevicesAsync, () => !IsLoadingDevices, OnCommandError);
            RefreshHistoryCommand = new AsyncCommand(
                LoadHistoryAsync, () => !IsLoadingHistory, OnCommandError);
            LogoutCommand = new AsyncCommand(LogoutAsync, onError: OnCommandError);
            LogoutAllCommand = new AsyncCommand(LogoutAllAsync, onError: OnCommandError);

            Devices = new ObservableCollection<DeviceRow>();
            History = new ObservableCollection<HistoryRow>();

            if (sync != null)
            {
                sync.StateChanged += OnSyncChanged;
                _state = sync.State;
            }
            else
            {
                _state = new SyncState();
            }
            RefreshAll();
        }

        // ---------------- 命令 ----------------

        public AsyncCommand SyncNowCommand { get; private set; }
        public AsyncCommand RefreshDevicesCommand { get; private set; }
        public AsyncCommand RefreshHistoryCommand { get; private set; }
        public AsyncCommand LogoutCommand { get; private set; }
        public AsyncCommand LogoutAllCommand { get; private set; }

        // U20 导航请求（开→关同步密码/私钥 → 安全清理页）。页面订阅并导航。
        public event EventHandler RequestSecurityRotate;

        // ---------------- 状态卡（U17 状态 Pivot） ----------------

        public SyncScreenKind Screen
        {
            get
            {
                return _presenter.DetermineScreen(
                    _auth != null ? _auth.Session : AuthState.Unauthenticated, _state);
            }
        }

        public string Email
        {
            get
            {
                var session = _auth != null ? _auth.Session : null;
                return session != null ? (session.UserEmail ?? string.Empty) : string.Empty;
            }
        }

        public string DeviceName
        {
            get
            {
                var session = _auth != null ? _auth.Session : null;
                return session != null ? (session.DeviceName ?? string.Empty) : string.Empty;
            }
        }

        public string Revision
        {
            get { return _state.Revision ?? "0"; }
        }

        public int KeyVersion
        {
            get { return _state.KeyVersion; }
        }

        public string StatusText
        {
            get
            {
                string overridden = ResolvePhaseText();
                return string.IsNullOrEmpty(overridden) ? GetString(_statusTextKey, string.Empty) : overridden;
            }
        }

        public string StatusIconKey
        {
            get { return _statusIconKey; }
        }

        public bool StatusSpin
        {
            get { return _statusSpin; }
        }

        public bool IsSyncing
        {
            get { return _isSyncing; }
        }

        public string LastSyncedRelative
        {
            get { return _lastSyncedRelative; }
        }

        public string NextRetryRelative
        {
            get { return _nextRetryRelative; }
        }

        public bool HasConflict
        {
            get { return _state != null && _state.Conflict != null; }
        }

        // ---------------- 同步设置开关 ----------------

        public bool EnableSync
        {
            get { return _state.Preferences != null && _state.Preferences.Enabled; }
        }

        public bool AutoSync
        {
            get { return _state.Preferences == null || _state.Preferences.AutoSync; }
        }

        public bool SyncPasswords
        {
            get { return _state.Preferences != null && _state.Preferences.SyncPasswords; }
        }

        public bool SyncPrivateKeys
        {
            get { return _state.Preferences != null && _state.Preferences.SyncPrivateKeys; }
        }

        // 同步私钥开关在 S15 前禁用（见 04-TASKS U17）。
        public bool SyncPrivateKeysEnabled
        {
            get { return false; }
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

        // ---------------- 设备与历史（U18） ----------------

        public ObservableCollection<DeviceRow> Devices { get; private set; }
        public ObservableCollection<HistoryRow> History { get; private set; }

        public bool IsLoadingDevices
        {
            get { return _isLoadingDevices; }
            private set { SetProperty(ref _isLoadingDevices, value); }
        }

        public bool IsLoadingHistory
        {
            get { return _isLoadingHistory; }
            private set { SetProperty(ref _isLoadingHistory, value); }
        }

        public bool HasDevices
        {
            get { return Devices.Count > 0; }
        }

        public bool HasHistory
        {
            get { return History.Count > 0; }
        }

        // ---------------- 开关操作 ----------------

        // 启用同步。直接写入偏好。
        public Task SetEnableSyncAsync(bool value)
        {
            if (_sync == null)
            {
                return Task.CompletedTask;
            }
            var next = _state.Preferences == null
                ? SyncPreferences.Defaults()
                : _state.Preferences.Clone();
            next.Enabled = value;
            next.AutoSync = value && next.AutoSync;
            return RunSafe("SetPreferences", () => _sync.SetPreferencesAsync(next));
        }

        public Task SetAutoSyncAsync(bool value)
        {
            if (_sync == null)
            {
                return Task.CompletedTask;
            }
            var next = _state.Preferences == null
                ? SyncPreferences.Defaults()
                : _state.Preferences.Clone();
            next.AutoSync = value;
            return RunSafe("SetPreferences", () => _sync.SetPreferencesAsync(next));
        }

        // 同步密码开关：开→关必须走 U20 安全清理（轮换密钥），此处触发事件；关→开直接写入。
        public void ToggleSyncPasswords(bool value)
        {
            if (_sync == null)
            {
                return;
            }
            if (!value)
            {
                // 开→关：进安全清理流程。
                RequestSecurityRotate?.Invoke(this, EventArgs.Empty);
                return;
            }
            var next = _state.Preferences == null
                ? SyncPreferences.Defaults()
                : _state.Preferences.Clone();
            next.SyncPasswords = true;
            RunSafe("SetPreferences", () => _sync.SetPreferencesAsync(next)).Forget("AccountSyncViewModel.SetPreferences", AppLog.Logger);
        }

        // ---------------- 路由 ----------------

        // 是否需要离开本页（去登录/建库/解锁）。
        public bool NeedsRouting
        {
            get
            {
                return Screen == SyncScreenKind.Login
                    || Screen == SyncScreenKind.CreateVault
                    || Screen == SyncScreenKind.UnlockVault;
            }
        }

        // ---------------- 立即同步 ----------------

        private bool CanSyncNow()
        {
            return !_isSyncing && _sync != null && Screen == SyncScreenKind.Status;
        }

        private async Task SyncNowAsync()
        {
            if (_sync == null)
            {
                return;
            }
            ErrorMessage = null;
            await RunSafe("SyncNow", () => _sync.SyncNowAsync());
        }

        // ---------------- 设备（U18） ----------------

        private async Task LoadDevicesAsync()
        {
            if (_sync == null)
            {
                return;
            }
            IsLoadingDevices = true;
            try
            {
                var response = await _sync.ListDevicesAsync().ConfigureAwait(true);
                var rows = new List<DeviceRow>();
                if (response != null && response.Items != null)
                {
                    foreach (var item in response.Items)
                    {
                        if (item == null)
                        {
                            continue;
                        }
                        rows.Add(new DeviceRow
                        {
                            Id = item.Id,
                            Name = item.Name ?? string.Empty,
                            Platform = item.AppVersion ?? item.Platform ?? string.Empty,
                            IsCurrent = item.Current,
                            LastSeenRelative = _presenter.FormatRelativeTime(item.LastSeenAt)
                        });
                    }
                }
                ReplaceAll(Devices, rows);
                RaisePropertyChanged("HasDevices");
            }
            catch (Exception ex)
            {
                LogWarning("加载设备列表失败 " + ex.GetType().Name);
                ErrorMessage = GetString("Sync_DeviceListFailed", "无法加载设备列表");
            }
            finally
            {
                IsLoadingDevices = false;
            }
        }

        // 重命名设备（本机或远端）。
        public Task RenameDeviceAsync(string deviceId, string newName)
        {
            if (_auth == null)
            {
                return Task.CompletedTask;
            }
            return RunSafe("RenameDevice", async () =>
            {
                await _auth.RenameDeviceAsync(deviceId, newName).ConfigureAwait(true);
                await LoadDevicesAsync().ConfigureAwait(true);
            });
        }

        // 撤销设备（本机禁止撤销，由 AuthService 抛错）。
        public Task RevokeDeviceAsync(string deviceId)
        {
            if (_auth == null)
            {
                return Task.CompletedTask;
            }
            return RunSafe("RevokeDevice", async () =>
            {
                await _auth.RevokeDeviceAsync(deviceId).ConfigureAwait(true);
                await LoadDevicesAsync().ConfigureAwait(true);
            });
        }

        // ---------------- 历史版本（U18） ----------------

        private async Task LoadHistoryAsync()
        {
            if (_sync == null)
            {
                return;
            }
            IsLoadingHistory = true;
            try
            {
                var response = await _sync.ListRevisionsAsync().ConfigureAwait(true);
                var rows = new List<HistoryRow>();
                if (response != null && response.Items != null)
                {
                    foreach (var item in response.Items)
                    {
                        if (item == null)
                        {
                            continue;
                        }
                        string deviceName = item.CreatedByDevice != null
                            ? (item.CreatedByDevice.Name ?? item.CreatedByDevice.Id ?? string.Empty)
                            : string.Empty;
                        rows.Add(new HistoryRow
                        {
                            Revision = item.Revision ?? string.Empty,
                            KeyVersion = item.KeyVersion,
                            CreatedAtRelative = _presenter.FormatRelativeTime(item.CreatedAt),
                            DeviceName = deviceName,
                            DevicePlatform = item.CreatedByDevice != null
                                ? (item.CreatedByDevice.Platform ?? string.Empty) : string.Empty
                        });
                    }
                }
                ReplaceAll(History, rows);
                RaisePropertyChanged("HasHistory");
            }
            catch (Exception ex)
            {
                LogWarning("加载历史版本失败 " + ex.GetType().Name);
                ErrorMessage = GetString("Sync_HistoryListFailed", "无法加载历史版本");
            }
            finally
            {
                IsLoadingHistory = false;
            }
        }

        // 恢复历史版本（SyncNow(use-remote)）。
        public Task RestoreRevisionAsync(string revision)
        {
            if (_sync == null)
            {
                return Task.CompletedTask;
            }
            return RunSafe("RestoreRevision", () => _sync.RestoreRevisionAsync(revision));
        }

        // 清空历史版本。
        public Task ClearHistoryAsync()
        {
            if (_sync == null)
            {
                return Task.CompletedTask;
            }
            return RunSafe("ClearRevisions", () => _sync.ClearRevisionsAsync());
        }

        // ---------------- 退出登录 ----------------

        private async Task LogoutAsync()
        {
            if (_sync == null)
            {
                return;
            }
            await RunSafe("Logout", () => _sync.LogoutAsync(all: false));
        }

        private async Task LogoutAllAsync()
        {
            if (_sync == null)
            {
                return;
            }
            await RunSafe("LogoutAll", () => _sync.LogoutAsync(all: true));
        }

        // ---------------- 状态刷新 ----------------

        private void OnSyncChanged(SyncState state)
        {
            _state = state ?? new SyncState();
            RefreshAll();
        }

        private void RefreshAll()
        {
            var spec = _presenter.StatusCardText(_state);
            _statusTextKey = spec.TextKey;
            _statusIconKey = spec.IconKey;
            _statusSpin = spec.Spin;
            _isSyncing = _state != null && _state.Phase == SyncPhase.Syncing;
            _lastSyncedRelative = _presenter.FormatRelativeTime(
                _state == null ? null : _state.LastSyncedAt);
            _nextRetryRelative = _presenter.FormatRelativeTime(
                _state == null ? null : _state.NextRetryAt);

            RaisePropertyChanged("Screen");
            RaisePropertyChanged("Email");
            RaisePropertyChanged("DeviceName");
            RaisePropertyChanged("Revision");
            RaisePropertyChanged("KeyVersion");
            RaisePropertyChanged("StatusText");
            RaisePropertyChanged("StatusIconKey");
            RaisePropertyChanged("StatusSpin");
            RaisePropertyChanged("IsSyncing");
            RaisePropertyChanged("LastSyncedRelative");
            RaisePropertyChanged("NextRetryRelative");
            RaisePropertyChanged("HasConflict");
            RaisePropertyChanged("EnableSync");
            RaisePropertyChanged("AutoSync");
            RaisePropertyChanged("SyncPasswords");
            RaisePropertyChanged("SyncPrivateKeys");
            RaisePropertyChanged("NeedsRouting");
            SyncNowCommand.RaiseCanExecuteChanged();
            RefreshDevicesCommand.RaiseCanExecuteChanged();
            RefreshHistoryCommand.RaiseCanExecuteChanged();
        }

        // 相位文案覆盖：conflict/error/offline 等优先显示状态卡 message。
        private string ResolvePhaseText()
        {
            if (_state == null)
            {
                return string.Empty;
            }
            string message = _state.Message;
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }
            switch (_state.Phase)
            {
                case SyncPhase.Conflict:
                case SyncPhase.Error:
                case SyncPhase.Offline:
                case SyncPhase.AuthError:
                    return message;
                default:
                    return string.Empty;
            }
        }

        // ---------------- 工具 ----------------

        // 安全运行业务操作：捕获异常、记日志（tag 走 "Sync.<Action>"），返回已完成的 Task。
        private Task RunSafe(string action, Func<Task> work)
        {
            try
            {
                var task = work();
                if (task == null)
                {
                    return Task.CompletedTask;
                }
                return task.ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                    {
                        LogWarning(action + " 失败 " + (t.Exception.InnerException ?? t.Exception).GetType().Name);
                    }
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception ex)
            {
                LogWarning(action + " 失败 " + ex.GetType().Name);
                return Task.CompletedTask;
            }
        }

        private void OnCommandError(Exception ex)
        {
            try
            {
                ErrorMessage = GetString("Sync_CommandFailed", "操作失败，请稍后重试");
            }
            catch (Exception)
            {
            }
            LogWarning("命令异常 " + ex.GetType().Name);
        }

        private static void ReplaceAll<T>(ObservableCollection<T> target, List<T> source)
        {
            target.Clear();
            foreach (var item in source)
            {
                target.Add(item);
            }
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

        private void LogWarning(string message)
        {
            try
            {
                Logger.Log(LogLevel.Warning, "AccountSync", message);
            }
            catch (Exception)
            {
            }
        }

        // O03：页面 OnNavigatedFrom 调用。SyncCoordinator 是应用级单例、本 VM
        // 随页面重建，不退订就按访问次数累积死 VM。幂等。
        public void Detach()
        {
            if (_detached)
            {
                return;
            }
            _detached = true;
            if (_sync != null)
            {
                _sync.StateChanged -= OnSyncChanged;
            }
        }
    }

    // U18 设备行（本机徽标、重命名、撤销）。
    public sealed class DeviceRow
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Platform { get; set; }
        public bool IsCurrent { get; set; }
        public string LastSeenRelative { get; set; }
    }

    // U18 历史版本行（来源设备、时间、keyVersion）。
    public sealed class HistoryRow
    {
        public string Revision { get; set; }
        public int KeyVersion { get; set; }
        public string CreatedAtRelative { get; set; }
        public string DeviceName { get; set; }
        public string DevicePlatform { get; set; }
    }
}
