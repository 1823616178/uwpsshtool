using System;
using System.Globalization;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Sessions;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;

namespace SshTool.Core.Sync
{
    // 03-SYNC-PROTOCOL.md §7.5 同步触发器（S14）。
    //
    // 分工：本类是 Core 纯逻辑（可单测），只依赖窄接口与仓库事件；
    // 平台事件（应用前后台、网络变化）由组合根（AppServices）订阅后调用 Notify* 喂入，
    // 本类不引用任何 UWP 类型。定时经 ITimerFactory（生产用 DispatcherTimerFactory，
    // 单测用 ManualTimerFactory）；时钟可注入（回前台 30 s 阈值可测）。
    //
    // 触发表（条件不满足时静默跳过，绝不抛异常——事件订阅路径抛异常会拖垮仓库写入与设置保存）：
    //   启动           已登录且 enabled                    → SyncNow
    //   仓库 Changed   origin=User 且实体为主机/分组/隧道   → MarkDirty（协调器内防抖 3 s）
    //   凭据 Changed   origin=User 且键为 host:/key: 前缀   → MarkDirty
    //   回前台         已登录 enabled autoSync 且距上次同步 > 30 s → SyncNow；同时打开前台轮询
    //   网络恢复       已登录 enabled autoSync              → SyncNow
    //   前台轮询       间隔 syncPollForegroundSeconds（默认 60，0 关）；后台不轮询 → SyncNow
    //   手动           用户点击 → SyncNow（由调用方等待结果并展示）
    //
    // 片段/外观/known_hosts/密钥元数据仓库不在订阅之列：它们是本机专有数据（§1），
    // 变化永不标脏。密钥库私钥原文走凭据通道（key: 前缀），仍能触发。
    //
    // 日志脱敏：只记触发来源与是否跳过，不记 id、键名、文档内容与任何密钥材料。
    public sealed class SyncTriggers : IDisposable
    {
        // 回前台阈值：距上次同步超过 30 s 才补一次（§7.5，严格大于）。
        public const int ForegroundSyncThresholdSeconds = 30;

        // 轮询间隔兜底（设置不可读时；正常值来自 syncPollForegroundSeconds，默认 60，0 关）。
        public const int FallbackPollSeconds = 60;

        private readonly ISyncTriggerTarget _sync;
        private readonly HostRepository _hosts;
        private readonly GroupRepository _groups;
        private readonly TunnelRepository _tunnels;
        private readonly ISecretStore _secrets;
        private readonly SettingsRepository _settings;
        private readonly ITimerFactory _timers;
        private readonly Func<DateTimeOffset> _clock;
        private readonly ILogger _logger;

        private readonly object _gate = new object();
        private bool _started;
        private bool _disposed;
        private bool _inForeground;
        private IDisposable _pollTimer;

        private EventHandler<RepositoryChangedEventArgs> _hostsHandler;
        private EventHandler<RepositoryChangedEventArgs> _groupsHandler;
        private EventHandler<RepositoryChangedEventArgs> _tunnelsHandler;
        private EventHandler<SecretChangedEventArgs> _secretsHandler;
        private EventHandler<SettingChangedEventArgs> _settingsHandler;

        public SyncTriggers(
            ISyncTriggerTarget sync,
            HostRepository hosts,
            GroupRepository groups,
            TunnelRepository tunnels,
            ISecretStore secrets,
            SettingsRepository settings,
            ITimerFactory timers = null,
            Func<DateTimeOffset> clock = null,
            ILogger logger = null)
        {
            if (sync == null)
            {
                throw new ArgumentNullException("sync");
            }
            if (hosts == null)
            {
                throw new ArgumentNullException("hosts");
            }
            if (groups == null)
            {
                throw new ArgumentNullException("groups");
            }
            if (tunnels == null)
            {
                throw new ArgumentNullException("tunnels");
            }
            if (secrets == null)
            {
                throw new ArgumentNullException("secrets");
            }
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }
            _sync = sync;
            _hosts = hosts;
            _groups = groups;
            _tunnels = tunnels;
            _secrets = secrets;
            _settings = settings;
            _timers = timers ?? new ManualTimerFactory();
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _logger = logger;
        }

        // 诊断用：前台轮询是否在跑（验收“轮询启停”与组合根自检）。
        public bool IsPolling
        {
            get { lock (_gate) { return _pollTimer != null; } }
        }

        // 订阅仓库/凭据/设置事件（幂等）。应用启动、服务就绪后调用一次。
        public void Start()
        {
            lock (_gate)
            {
                if (_started || _disposed)
                {
                    return;
                }
                _started = true;
                _hostsHandler = OnRepositoryChanged;
                _groupsHandler = OnRepositoryChanged;
                _tunnelsHandler = OnRepositoryChanged;
                _secretsHandler = OnSecretsChanged;
                _settingsHandler = OnSettingsChanged;
            }
            _hosts.Changed += _hostsHandler;
            _groups.Changed += _groupsHandler;
            _tunnels.Changed += _tunnelsHandler;
            _secrets.Changed += _secretsHandler;
            _settings.Changed += _settingsHandler;
        }

        // 应用启动：已登录且 enabled → SyncNow（未登录/未启用时静默跳过）。
        public void NotifyStartup()
        {
            try
            {
                if (!_started || _disposed)
                {
                    return;
                }
                SyncState state = SafeState();
                if (state == null || !CanStartupSync(state))
                {
                    return;
                }
                var ignore = SyncNowSafeAsync("startup");
            }
            catch (Exception ex)
            {
                Info("startup 触发失败 " + ex.GetType().Name);
            }
        }

        // 回到前台：打开轮询；已登录 enabled autoSync 且距上次同步 > 30 s → SyncNow。
        // 同步进行中（syncing）不再重复触发（单飞本就会复用，但避免多余日志与调用）。
        public void NotifyForeground()
        {
            try
            {
                if (!_started || _disposed)
                {
                    return;
                }
                lock (_gate)
                {
                    _inForeground = true;
                }
                StartPoll();
                SyncState state = SafeState();
                if (state == null || !CanAutoSync(state))
                {
                    return;
                }
                if (state.Phase == SyncPhase.Syncing)
                {
                    return;
                }
                if (!IsForegroundStale(state))
                {
                    return;
                }
                var ignore = SyncNowSafeAsync("foreground");
            }
            catch (Exception ex)
            {
                Info("foreground 触发失败 " + ex.GetType().Name);
            }
        }

        // 进入后台：停止前台轮询（§7.5：后台不轮询；进行中的同步不取消）。
        public void NotifyBackground()
        {
            try
            {
                lock (_gate)
                {
                    _inForeground = false;
                }
                StopPoll();
            }
            catch (Exception ex)
            {
                Info("background 处理失败 " + ex.GetType().Name);
            }
        }

        // 网络恢复：已登录 enabled autoSync → SyncNow（阈值不限：断网期间的修改等它补传）。
        public void NotifyNetworkRestored()
        {
            try
            {
                if (!_started || _disposed)
                {
                    return;
                }
                SyncState state = SafeState();
                if (state == null || !CanAutoSync(state))
                {
                    return;
                }
                var ignore = SyncNowSafeAsync("network");
            }
            catch (Exception ex)
            {
                Info("network 触发失败 " + ex.GetType().Name);
            }
        }

        // 手动触发：直接 SyncNow，由调用方（MainPage 同步按钮）等待结果并经图标展示。
        // 不做 enabled/autoSync 门控——协调器内部会对未登录/未启用/锁定做廉价短路，
        // 用户显式点击时以协调器的相位结论为准。
        public Task RequestManualSyncAsync()
        {
            return _sync.SyncNowAsync();
        }

        public void Dispose()
        {
            EventHandler<RepositoryChangedEventArgs> hostsHandler;
            EventHandler<RepositoryChangedEventArgs> groupsHandler;
            EventHandler<RepositoryChangedEventArgs> tunnelsHandler;
            EventHandler<SecretChangedEventArgs> secretsHandler;
            EventHandler<SettingChangedEventArgs> settingsHandler;
            lock (_gate)
            {
                _disposed = true;
                _inForeground = false;
                hostsHandler = _hostsHandler;
                groupsHandler = _groupsHandler;
                tunnelsHandler = _tunnelsHandler;
                secretsHandler = _secretsHandler;
                settingsHandler = _settingsHandler;
                _hostsHandler = null;
                _groupsHandler = null;
                _tunnelsHandler = null;
                _secretsHandler = null;
                _settingsHandler = null;
                StopPollLocked();
            }
            if (hostsHandler != null)
            {
                try { _hosts.Changed -= hostsHandler; } catch (Exception) { }
            }
            if (groupsHandler != null)
            {
                try { _groups.Changed -= groupsHandler; } catch (Exception) { }
            }
            if (tunnelsHandler != null)
            {
                try { _tunnels.Changed -= tunnelsHandler; } catch (Exception) { }
            }
            if (secretsHandler != null)
            {
                try { _secrets.Changed -= secretsHandler; } catch (Exception) { }
            }
            if (settingsHandler != null)
            {
                try { _settings.Changed -= settingsHandler; } catch (Exception) { }
            }
        }

        // ---- 事件入口（一律不抛：仓库/设置落盘路径是同步调用事件，抛异常会拖垮写入） ----

        private void OnRepositoryChanged(object sender, RepositoryChangedEventArgs e)
        {
            try
            {
                if (!_started || _disposed)
                {
                    return;
                }
                if (e == null || e.Origin != ChangeOrigin.User)
                {
                    return;
                }
                SyncState state = SafeState();
                if (state == null || state.Preferences == null || !state.Preferences.Enabled)
                {
                    return;
                }
                var ignore = MarkDirtySafeAsync("repo");
            }
            catch (Exception ex)
            {
                Info("repo 标脏失败 " + ex.GetType().Name);
            }
        }

        private void OnSecretsChanged(object sender, SecretChangedEventArgs e)
        {
            try
            {
                if (!_started || _disposed)
                {
                    return;
                }
                if (e == null || e.Origin != ChangeOrigin.User)
                {
                    return;
                }
                if (!SecretChangedEventArgs.HasCredentialKey(e.ChangedKeys))
                {
                    return;
                }
                SyncState state = SafeState();
                if (state == null || state.Preferences == null || !state.Preferences.Enabled)
                {
                    return;
                }
                var ignore = MarkDirtySafeAsync("secrets");
            }
            catch (Exception ex)
            {
                Info("secrets 标脏失败 " + ex.GetType().Name);
            }
        }

        private void OnSettingsChanged(object sender, SettingChangedEventArgs e)
        {
            try
            {
                if (!_started || _disposed)
                {
                    return;
                }
                if (e == null || !string.Equals(e.Key, "syncPollForegroundSeconds", StringComparison.Ordinal))
                {
                    return;
                }
                bool foreground;
                lock (_gate)
                {
                    foreground = _inForeground;
                }
                if (!foreground)
                {
                    return;
                }
                StartPoll();
            }
            catch (Exception ex)
            {
                Info("poll 重建失败 " + ex.GetType().Name);
            }
        }

        private void OnPollTick()
        {
            try
            {
                if (_disposed)
                {
                    return;
                }
                bool foreground;
                lock (_gate)
                {
                    foreground = _inForeground;
                }
                if (!foreground)
                {
                    return;
                }
                SyncState state = SafeState();
                if (state == null || !CanAutoSync(state))
                {
                    return;
                }
                if (state.Phase == SyncPhase.Syncing)
                {
                    return;
                }
                var ignore = SyncNowSafeAsync("poll");
            }
            catch (Exception ex)
            {
                Info("poll 触发失败 " + ex.GetType().Name);
            }
        }

        // ---- 门控（纯函数，见 §7.5 触发表） ----

        // 已登录：signed_out（未登录）/disabled（未启用）/auth_error（会话已清空）之外都算。
        // locked（已登录但保险库锁定）按表走 SyncNow，由协调器廉价短路回 locked。
        private static bool IsSignedIn(SyncState state)
        {
            return state.Phase != SyncPhase.SignedOut
                && state.Phase != SyncPhase.Disabled
                && state.Phase != SyncPhase.AuthError;
        }

        private static bool CanStartupSync(SyncState state)
        {
            return state.Preferences != null && state.Preferences.Enabled && IsSignedIn(state);
        }

        private static bool CanAutoSync(SyncState state)
        {
            return CanStartupSync(state) && state.Preferences.AutoSync;
        }

        private bool IsForegroundStale(SyncState state)
        {
            if (string.IsNullOrEmpty(state.LastSyncedAt))
            {
                return true;
            }
            DateTimeOffset last;
            if (!DateTimeOffset.TryParse(
                state.LastSyncedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out last))
            {
                return true;
            }
            return (_clock() - last).TotalSeconds > ForegroundSyncThresholdSeconds;
        }

        // ---- 轮询（前台独占；调用方已处理异常，内部仍守卫） ----

        private void StartPoll()
        {
            try
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    StopPollLocked();
                    if (!_inForeground)
                    {
                        return;
                    }
                    int seconds = ReadPollSeconds();
                    if (seconds <= 0)
                    {
                        return;
                    }
                    long dueMs = (long)seconds * 1000L;
                    int due = dueMs > int.MaxValue ? int.MaxValue : (int)dueMs;
                    _pollTimer = _timers.SchedulePeriodic(due, OnPollTick);
                }
            }
            catch (Exception ex)
            {
                Info("poll 启动失败 " + ex.GetType().Name);
            }
        }

        private void StopPoll()
        {
            try
            {
                lock (_gate)
                {
                    StopPollLocked();
                }
            }
            catch (Exception)
            {
            }
        }

        // 调用方已持有 _gate。
        private void StopPollLocked()
        {
            IDisposable timer = _pollTimer;
            _pollTimer = null;
            if (timer != null)
            {
                try
                {
                    timer.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        private int ReadPollSeconds()
        {
            try
            {
                int seconds = _settings.SyncPollForegroundSeconds;
                return seconds < 0 ? 0 : seconds;
            }
            catch (Exception)
            {
                return FallbackPollSeconds;
            }
        }

        // ---- 安全调用（fire-and-forget：异常只记类型名，绝不抛回事件源） ----

        private SyncState SafeState()
        {
            try
            {
                return _sync.CurrentState;
            }
            catch (Exception ex)
            {
                Info("读同步状态失败 " + ex.GetType().Name);
                return null;
            }
        }

        private async Task MarkDirtySafeAsync(string source)
        {
            try
            {
                await _sync.MarkDirtyAsync().ConfigureAwait(false);
                Info("已标脏 source=" + source);
            }
            catch (Exception ex)
            {
                Info("标脏失败 source=" + source + " " + ex.GetType().Name);
            }
        }

        private async Task SyncNowSafeAsync(string source)
        {
            try
            {
                await _sync.SyncNowAsync().ConfigureAwait(false);
                Info("同步触发 source=" + source);
            }
            catch (Exception ex)
            {
                Info("同步触发失败 source=" + source + " " + ex.GetType().Name);
            }
        }

        private void Info(string message)
        {
            if (_logger != null)
            {
                _logger.Log(LogLevel.Info, "SyncTriggers", message);
            }
        }
    }
}
