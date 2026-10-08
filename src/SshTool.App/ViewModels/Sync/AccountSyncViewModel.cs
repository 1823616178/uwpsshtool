using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.Core.Common;
using SshTool.Core.Mvvm;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Api.Dtos;
using SshTool.Core.Sync.Auth;
using SshTool.Core.Sync.Vault;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace SshTool.App.ViewModels.Sync
{
    // U17/U18 同步状态页 ViewModel（02-UI-DESIGN.md §5.13 状态 Pivot + 设备/历史 Pivot）。
    // 消费 AuthService/SyncCoordinator：根据 AuthState / vault / phase 决定路由到登录页、
    // 建库、解锁或状态卡；暴露状态卡文案（相对时间 + 相位）、启用/自动同步开关、同步密码/
    // 私钥开关（开→关进 U20 安全清理），以及设备列表与历史版本（U18）。
    // 脱敏：日志只记操作相位与异常类型名，绝不记录密码/token/恢复密钥。
    // feat/account-sync-ui：页面重设计——状态卡（语气色徽章 + 内联消息 + 立即同步可用性说明）、
    // 保险库卡（创建 / 输入同步密码解锁 / 已解锁 + 锁定 / 修改同步密码）、设备卡、历史时间线。
    // 判定规则在 Core 的 AccountSyncPresenter（可单测），这里只把键翻成文案 / 字形 / 画刷。
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
        private bool _isLoggingOut;
        private bool _isLockingVault;
        private bool _devicesLoadFailed;
        private bool _historyLoadFailed;
        private StatusHeroSpec _hero = new StatusHeroSpec();
        private VaultCardSpec _vaultCard = new VaultCardSpec();

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
            LockVaultCommand = new AsyncCommand(LockVaultAsync, () => CanLockVault(), OnCommandError);

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
        public AsyncCommand LockVaultCommand { get; private set; }

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

        // 状态卡标题：相位短文案（已同步 / 同步失败 / 保险库已锁定…）。
        public string StatusTitle
        {
            get { return GetString(_statusTextKey, string.Empty); }
        }

        // 状态卡内联消息（错误原因 / 离线 / 冲突 / 其他设备改了同步密码…）；无则为空。
        // fix/functional-pass（P2-2）：按 MessageCode / MessageError 本地化，不直接显示 Core 诊断文本。
        public string StatusDetail
        {
            get
            {
                if (!_hero.ShowDetail)
                {
                    return string.Empty;
                }
                string message = SyncText.StateMessage(_state, _loader) ?? string.Empty;
                return string.Equals(message, StatusTitle, StringComparison.Ordinal) ? string.Empty : message;
            }
        }

        public bool HasStatusDetail
        {
            get { return !string.IsNullOrEmpty(StatusDetail); }
        }

        public StatusHeroSpec Hero
        {
            get { return _hero; }
        }

        public bool IsSyncNowAvailable
        {
            get { return _hero.CanSyncNow && _sync != null; }
        }

        // 「立即同步」不可用时的说明（先解锁保险库 / 先创建保险库 / 同步已关闭）。
        public string SyncNowHint
        {
            get { return string.IsNullOrEmpty(_hero.SyncNowHintKey) ? string.Empty : GetString(_hero.SyncNowHintKey, string.Empty); }
        }

        // ---------------- 保险库卡 ----------------

        public VaultCardSpec VaultCard
        {
            get { return _vaultCard; }
        }

        public string VaultTitle
        {
            get { return string.IsNullOrEmpty(_vaultCard.TitleKey) ? string.Empty : GetString(_vaultCard.TitleKey, string.Empty); }
        }

        public string VaultDescription
        {
            get { return string.IsNullOrEmpty(_vaultCard.DescriptionKey) ? string.Empty : GetString(_vaultCard.DescriptionKey, string.Empty); }
        }

        public bool IsLockingVault
        {
            get { return _isLockingVault; }
            private set
            {
                if (SetProperty(ref _isLockingVault, value))
                {
                    LockVaultCommand.RaiseCanExecuteChanged();
                }
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

        public string SyncPrivateKeysDescription
        {
            get
            {
                return SyncPrivateKeysEnabled
                    ? GetString("AccountSync_SyncPrivateKeysDesc", string.Empty)
                    : GetString("AccountSync_SyncPrivateKeysSoon", string.Empty);
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

        // fix/auth-audit：退出登录 / 退出所有设备进行中（服务端无响应时最长等到请求超时）。
        // 此前页面没有任何忙碌提示，用户以为没点上会反复点。
        public bool IsLoggingOut
        {
            get { return _isLoggingOut; }
            private set { SetProperty(ref _isLoggingOut, value); }
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

        // 设备 / 历史加载失败：在各自页签里显示错误空状态 + 重试，不再占用页头错误条
        //（进页面自动加载，离线时此前一进来就是一条红色错误）。
        public bool DevicesLoadFailed
        {
            get { return _devicesLoadFailed; }
            private set { SetProperty(ref _devicesLoadFailed, value); }
        }

        public bool HistoryLoadFailed
        {
            get { return _historyLoadFailed; }
            private set { SetProperty(ref _historyLoadFailed, value); }
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
            return RunSafe("SetPreferences", () => _sync.SetPreferencesAsync(next), onFailed: RefreshAll);
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
            return RunSafe("SetPreferences", () => _sync.SetPreferencesAsync(next), onFailed: RefreshAll);
        }

        // feat/remember-vault：退出登录后是否在本机保留保险库密钥（本机设置，不随账号同步）。
        public bool RememberVaultKey
        {
            get { return _sync == null || _sync.RememberVaultKey; }
        }

        public Task SetRememberVaultKeyAsync(bool value)
        {
            if (_sync == null || value == _sync.RememberVaultKey)
            {
                return Task.CompletedTask;
            }
            return RunSafe("SetRememberVaultKey", async () =>
            {
                await _sync.SetRememberVaultKeyAsync(value);
                // 保险库卡说明随开关变化（「本机已记住同步密码」/「退出登录时会忘记」）。
                RefreshAll();
            }, onFailed: RefreshAll);
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
            RunSafe("SetPreferences", () => _sync.SetPreferencesAsync(next), onFailed: RefreshAll)
                .Forget("AccountSyncViewModel.SetPreferences", AppLog.Logger);
        }

        // ---------------- 路由 ----------------

        // 是否需要离开本页。feat/account-sync-ui：只在未登录时转去登录页；保险库 Missing / Locked
        // 留在本页，由保险库卡给出「创建保险库」/「输入同步密码解锁」（此前直接弹去解锁页，
        // 用户找不到同步密码入口，锁定后也回不到状态页）。
        public bool NeedsRouting
        {
            get { return AccountSyncPresenter.NeedsRouting(_auth != null ? _auth.Session : null); }
        }

        // ---------------- 立即同步 ----------------

        private bool CanSyncNow()
        {
            return !_isSyncing && IsSyncNowAvailable;
        }

        // ---------------- 锁定保险库（本机忘记同步密码） ----------------

        private bool CanLockVault()
        {
            return _sync != null && !_isLockingVault && _vaultCard.ShowLock;
        }

        private async Task LockVaultAsync()
        {
            if (_sync == null)
            {
                return;
            }
            IsLockingVault = true;
            try
            {
                // Coordinator 会等进行中的同步收尾再锁；锁定后保险库卡切到「输入同步密码解锁」。
                await RunSafe("LockVault", () => _sync.LockVaultAsync());
            }
            finally
            {
                IsLockingVault = false;
            }
        }

        private async Task SyncNowAsync()
        {
            if (_sync == null)
            {
                return;
            }
            ErrorMessage = null;
            // 同步失败已由 Coordinator 写进状态卡（离线 / 错误 / 冲突文案），这里不再重复显示。
            await RunSafe("SyncNow", () => _sync.SyncNowAsync(), showError: false);
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
                        rows.Add(BuildDeviceRow(item));
                    }
                }
                // 本机排在最前，其余按服务端顺序。
                rows.Sort((a, b) => a.IsCurrent == b.IsCurrent ? 0 : (a.IsCurrent ? -1 : 1));
                ReplaceAll(Devices, rows);
                DevicesLoadFailed = false;
                RaisePropertyChanged("HasDevices");
            }
            catch (Exception ex)
            {
                LogWarning("load devices failed " + ex.GetType().Name);
                DevicesLoadFailed = true;
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
            string name = (newName ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                return Task.CompletedTask;
            }
            return RunSafe("RenameDevice", async () =>
            {
                await _auth.RenameDeviceAsync(deviceId, name).ConfigureAwait(true);
                // 改的若是本机，AuthService 已同步本地会话里的设备名：刷新状态卡的「邮箱 · 设备名」。
                RaisePropertyChanged("DeviceName");
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
                var items = new List<RevisionItemDto>();
                if (response != null && response.Items != null)
                {
                    foreach (var item in response.Items)
                    {
                        if (item != null)
                        {
                            items.Add(item);
                        }
                    }
                }
                ReplaceAll(History, BuildHistoryRows(items));
                HistoryLoadFailed = false;
                RaisePropertyChanged("HasHistory");
            }
            catch (Exception ex)
            {
                LogWarning("load history failed " + ex.GetType().Name);
                HistoryLoadFailed = true;
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

        // 单设备退出：服务端失败也会清本地并转去登录页，错误不必显示。
        private async Task LogoutAsync()
        {
            if (_sync == null)
            {
                return;
            }
            IsLoggingOut = true;
            try
            {
                await RunSafe("Logout", () => _sync.LogoutAsync(all: false), showError: false);
            }
            finally
            {
                IsLoggingOut = false;
            }
        }

        // fix/auth-audit：退出所有设备没送达服务端时 Coordinator 保留本机登录并上抛，
        // 这里必须显示原因（此前被吞掉，用户以为其他设备都已下线）。
        private async Task LogoutAllAsync()
        {
            if (_sync == null)
            {
                return;
            }
            IsLoggingOut = true;
            try
            {
                await RunSafe("LogoutAll", () => _sync.LogoutAsync(all: true));
            }
            finally
            {
                IsLoggingOut = false;
            }
        }

        // ---------------- 状态刷新 ----------------

        private void OnSyncChanged(SyncState state)
        {
            _state = state ?? new SyncState();
            RefreshAll();
        }

        private void RefreshAll()
        {
            AuthState session = _auth != null ? _auth.Session : null;
            _hero = AccountSyncPresenter.Hero(session, _state);
            _vaultCard = AccountSyncPresenter.VaultCard(session, _state, RememberVaultKey);
            var spec = _presenter.StatusCardText(_state);
            _statusTextKey = spec.TextKey;
            _statusIconKey = spec.IconKey;
            _statusSpin = spec.Spin;
            _isSyncing = _state != null && _state.Phase == SyncPhase.Syncing;
            _lastSyncedRelative = FormatRelative(
                _state == null ? null : _state.LastSyncedAt);
            _nextRetryRelative = FormatRelative(
                _state == null ? null : _state.NextRetryAt);

            RaisePropertyChanged("Screen");
            RaisePropertyChanged("Email");
            RaisePropertyChanged("DeviceName");
            RaisePropertyChanged("Revision");
            RaisePropertyChanged("KeyVersion");
            RaisePropertyChanged("StatusTitle");
            RaisePropertyChanged("StatusDetail");
            RaisePropertyChanged("HasStatusDetail");
            RaisePropertyChanged("Hero");
            RaisePropertyChanged("IsSyncNowAvailable");
            RaisePropertyChanged("SyncNowHint");
            RaisePropertyChanged("VaultCard");
            RaisePropertyChanged("VaultTitle");
            RaisePropertyChanged("VaultDescription");
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
            RaisePropertyChanged("RememberVaultKey");
            RaisePropertyChanged("NeedsRouting");
            SyncNowCommand.RaiseCanExecuteChanged();
            LockVaultCommand.RaiseCanExecuteChanged();
            RefreshDevicesCommand.RaiseCanExecuteChanged();
            RefreshHistoryCommand.RaiseCanExecuteChanged();
        }

        // ---------------- 设备 / 历史行构建 ----------------

        private DeviceRow BuildDeviceRow(DeviceInfoDto item)
        {
            DevicePlatformKind kind = AccountSyncPresenter.ClassifyPlatform(item.Platform);
            string platform = AccountSyncPresenter.DescribePlatform(item.Platform);
            string seen = FormatRelative(item.LastSeenAt);
            string active = item.Current
                ? GetString("AccountSync_DeviceActiveNow", "Active now")
                : (string.IsNullOrEmpty(seen) ? string.Empty
                    : Format(GetString("AccountSync_DeviceLastActive", "Active {0}"), seen));
            string version = string.IsNullOrEmpty(item.AppVersion)
                ? string.Empty
                : Format(GetString("AccountSync_DeviceVersion", "App {0}"), item.AppVersion);
            string name = string.IsNullOrWhiteSpace(item.Name)
                ? GetString("AccountSync_DeviceUnnamed", "Unnamed device")
                : item.Name;
            return new DeviceRow
            {
                Id = item.Id,
                Name = name,
                Platform = platform,
                IsCurrent = item.Current,
                LastSeenRelative = seen,
                Glyph = AccountSyncVisuals.Glyph(AccountSyncPresenter.GlyphOf(kind)),
                Subtitle = JoinDot(platform, active),
                Detail = version,
                DetailVisibility = string.IsNullOrEmpty(version) ? Visibility.Collapsed : Visibility.Visible,
                CurrentVisibility = item.Current ? Visibility.Visible : Visibility.Collapsed,
                CurrentBadgeText = GetString("AccountSync_ThisDevice", "This device"),
                CardStrokeBrush = AccountSyncVisuals.ThemeBrush(item.Current ? "AppAccentBrush" : "AppCardStrokeBrush"),
                BadgeBrush = AccountSyncVisuals.SoftBrush(item.Current ? SyncTone.Accent : SyncTone.Neutral),
                GlyphBrush = AccountSyncVisuals.ToneBrush(item.Current ? SyncTone.Accent : SyncTone.Neutral),
                MoreLabel = Format(GetString("AccountSync_DeviceActions", "Actions for {0}"), name)
            };
        }

        private List<HistoryRow> BuildHistoryRows(List<RevisionItemDto> items)
        {
            var inputs = new List<HistoryEntryInput>(items.Count);
            foreach (var item in items)
            {
                inputs.Add(new HistoryEntryInput { Revision = item.Revision, KeyVersion = item.KeyVersion });
            }
            HistoryEventKind[] kinds = AccountSyncPresenter.ClassifyHistory(inputs, _state != null ? _state.Revision : null);
            string thisDeviceId = _auth != null && _auth.Session != null ? _auth.Session.DeviceId : null;
            var rows = new List<HistoryRow>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                RevisionItemDto item = items[i];
                HistoryEventKind kind = kinds[i];
                RevisionDeviceDto device = item.CreatedByDevice;
                string deviceName = device != null ? (device.Name ?? device.Id ?? string.Empty) : string.Empty;
                bool fromThisDevice = device != null && !string.IsNullOrEmpty(thisDeviceId)
                    && string.Equals(device.Id, thisDeviceId, StringComparison.Ordinal);
                string source = fromThisDevice
                    ? GetString("AccountSync_HistoryFromThisDevice", "From this device")
                    : (string.IsNullOrEmpty(deviceName) ? string.Empty
                        : Format(GetString("AccountSync_HistoryFromDevice", "From {0}"), deviceName));
                string when = FormatRelative(item.CreatedAt);
                SyncTone tone = kind == HistoryEventKind.Current ? SyncTone.Success
                    : (kind == HistoryEventKind.KeyRotated ? SyncTone.Warning : SyncTone.Accent);
                string revision = item.Revision ?? string.Empty;
                rows.Add(new HistoryRow
                {
                    Revision = revision,
                    KeyVersion = item.KeyVersion,
                    CreatedAtRelative = when,
                    DeviceName = deviceName,
                    DevicePlatform = device != null ? (device.Platform ?? string.Empty) : string.Empty,
                    Kind = kind,
                    IsCurrent = kind == HistoryEventKind.Current,
                    Title = JoinDot("r" + revision, GetString("AccountSync_History_" + kind, string.Empty)),
                    Subtitle = JoinDot(source, when),
                    Detail = Format(GetString("AccountSync_HistoryKeyVersion", "Key v{0}"), item.KeyVersion.ToString()),
                    Glyph = AccountSyncVisuals.Glyph(AccountSyncPresenter.GlyphOf(kind)),
                    BadgeBrush = AccountSyncVisuals.SoftBrush(tone),
                    GlyphBrush = AccountSyncVisuals.ToneBrush(tone),
                    RailTopVisibility = i == 0 ? Visibility.Collapsed : Visibility.Visible,
                    RailBottomVisibility = i == items.Count - 1 ? Visibility.Collapsed : Visibility.Visible,
                    ChevronVisibility = kind == HistoryEventKind.Current ? Visibility.Collapsed : Visibility.Visible
                });
            }
            return rows;
        }

        private static string JoinDot(string first, string second)
        {
            if (string.IsNullOrEmpty(first))
            {
                return second ?? string.Empty;
            }
            if (string.IsNullOrEmpty(second))
            {
                return first;
            }
            return first + " · " + second;
        }

        private static string Format(string template, string value)
        {
            try
            {
                return string.Format(System.Globalization.CultureInfo.CurrentCulture, template, value);
            }
            catch (FormatException)
            {
                return template;
            }
        }

        // ---------------- 工具 ----------------

        // 安全运行业务操作：等待完成；失败记日志（只记异常类型名）并显示本地化原因，不向外抛。
        // fix/auth-audit：此前用 ContinueWith(OnlyOnFaulted) 包装——操作成功时这个延续任务是
        // 「已取消」，await 它抛 TaskCanceledException，于是立即同步 / 退出登录成功后反而显示
        // 「操作失败，请稍后重试」；操作真失败时延续正常完成，改名 / 撤销设备 / 开关 / 恢复历史
        // 的错误全被吞掉，界面毫无反应。onFailed 用于把开关等界面状态拨回真实值。
        private async Task RunSafe(string action, Func<Task> work, bool showError = true, Action onFailed = null)
        {
            if (showError)
            {
                ErrorMessage = null;
            }
            try
            {
                Task task = work();
                if (task != null)
                {
                    await task.ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                LogWarning(action + " failed " + ex.GetType().Name);
                if (onFailed != null)
                {
                    try
                    {
                        onFailed();
                    }
                    catch (Exception)
                    {
                    }
                }
                if (showError)
                {
                    ErrorMessage = DescribeError(ex);
                }
            }
        }

        private string DescribeError(Exception ex)
        {
            try
            {
                string text = VaultErrorText.Describe(ex, _loader);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
            catch (Exception)
            {
            }
            return GetString("Sync_CommandFailed", "操作失败，请稍后重试");
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
            LogWarning("command failed " + ex.GetType().Name);
        }

        private static void ReplaceAll<T>(ObservableCollection<T> target, List<T> source)
        {
            target.Clear();
            foreach (var item in source)
            {
                target.Add(item);
            }
        }

        private string FormatRelative(string isoUtc)
        {
            return SyncText.Relative(_presenter.ComputeRelativeTime(isoUtc), _loader);
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

    // U18 设备行（本机徽标、重命名、撤销）。feat/account-sync-ui：卡片模板直接绑定的展示字段
    //（本工程无值转换器，可见性 / 画刷在构建行时算好；行不可变，刷新即整表替换）。
    public sealed class DeviceRow
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Platform { get; set; }
        public bool IsCurrent { get; set; }
        public string LastSeenRelative { get; set; }
        public string Glyph { get; set; }
        public string Subtitle { get; set; }
        public string Detail { get; set; }
        public Visibility DetailVisibility { get; set; }
        public Visibility CurrentVisibility { get; set; }
        public string CurrentBadgeText { get; set; }
        public Brush CardStrokeBrush { get; set; }
        public Brush BadgeBrush { get; set; }
        public Brush GlyphBrush { get; set; }
        public string MoreLabel { get; set; }
    }

    // U18 历史版本行（来源设备、时间、keyVersion）。feat/account-sync-ui：时间线字段（事件类型字形、
    // 首尾两端的竖线显隐、当前版本不显示「可恢复」箭头）。
    public sealed class HistoryRow
    {
        public string Revision { get; set; }
        public int KeyVersion { get; set; }
        public string CreatedAtRelative { get; set; }
        public string DeviceName { get; set; }
        public string DevicePlatform { get; set; }
        public HistoryEventKind Kind { get; set; }
        public bool IsCurrent { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Detail { get; set; }
        public string Glyph { get; set; }
        public Brush BadgeBrush { get; set; }
        public Brush GlyphBrush { get; set; }
        public Visibility RailTopVisibility { get; set; }
        public Visibility RailBottomVisibility { get; set; }
        public Visibility ChevronVisibility { get; set; }
    }

    // feat/account-sync-ui：语气 → 主题画刷 / 字形键 → 字形（代码侧取主题资源的统一入口，
    // 页面与 VM 共用；取不到时回退中性色 / 空字形，不抛）。
    internal static class AccountSyncVisuals
    {
        public static string Glyph(string key)
        {
            if (string.IsNullOrEmpty(key) || Application.Current == null)
            {
                return string.Empty;
            }
            try
            {
                return Application.Current.Resources[key] as string ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        public static Brush ThemeBrush(string key)
        {
            return ThemeService.ResolveBrush(key);
        }

        public static Brush ToneBrush(SyncTone tone)
        {
            switch (tone)
            {
                case SyncTone.Success:
                    return ThemeBrush("AppSuccessBrush");
                case SyncTone.Warning:
                    return ThemeBrush("AppWarningBrush");
                case SyncTone.Danger:
                    return ThemeBrush("AppDangerBrush");
                case SyncTone.Accent:
                    return ThemeBrush("AppAccentTextBrush");
                default:
                    return ThemeBrush("AppTextDimBrush");
            }
        }

        public static Brush SoftBrush(SyncTone tone)
        {
            switch (tone)
            {
                case SyncTone.Success:
                    return ThemeBrush("AppSuccessSoftBrush");
                case SyncTone.Warning:
                    return ThemeBrush("AppWarningSoftBrush");
                case SyncTone.Danger:
                    return ThemeBrush("AppDangerSoftBrush");
                case SyncTone.Accent:
                    return ThemeBrush("AppAccentSoftBrush");
                default:
                    return ThemeBrush("AppSurfaceAltBrush");
            }
        }
    }
}
