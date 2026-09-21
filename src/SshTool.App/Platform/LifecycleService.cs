using System;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.Core.Lifecycle;
using SshTool.Core.Sessions;
using SshTool.Core.Storage;
using Windows.ApplicationModel;
using Windows.ApplicationModel.ExtendedExecution;
using Windows.Foundation;
using Windows.System.Threading;
using Windows.UI.Xaml;

namespace SshTool.App.Platform
{
    // P01：应用生命周期与后台策略服务（App 层外壳）。
    //
    // 分工：判断全在 Core.BackgroundPolicy（纯状态机、可单测），本类只做两件事——
    // 把系统事件喂给策略机，把策略机吐出的动作翻译成系统调用（对齐鸿蒙端
    // BackgroundKeepAlive.ets 的纪律）。日志只记状态与计数，不记任何会话内容。
    //
    // 接线（Start/Stop）：
    //   - Application.EnteredBackground → 申请扩展执行 / 关保活则直接 405 断开；
    //   - ExtendedExecutionSession.Revoked → 同被拒：405 策略性断开；
    //   - Application.LeavingBackground / Resuming → 释放扩展执行 + 撤计时器 +
    //     405 会话原地重连 / 在线会话 ProbeNow（01-DESIGN.md §10）；
    //   - Application.Suspending → 标记「挂起前在线」（落盘由 App.xaml.cs 负责）；
    //   - SessionManager.SessionsChanged → 会话数变化（后台清零即还电）；
    //   - SettingsRepository.Changed → 开关变更即时纠正姿态。
    //
    // 本文件所有 UWP API（EnteredBackground、ExtendedExecutionSession、
    // ThreadPoolTimer）自 10240 起可用，无需 ApiInformation 守卫（min 15063 纪律）。
    public sealed class LifecycleService
    {
        private readonly SessionManager _sessions;
        private readonly SettingsRepository _settings;
        private readonly KeepAwakeService _keepAwake;
        private readonly ILogger _logger;
        private readonly object _sync = new object();

        private BackgroundPolicy _policy;
        private ExtendedExecutionSession _execution;
        private ThreadPoolTimer _backgroundTimer;
        private bool _started;

        private EnteredBackgroundEventHandler _enteredBackgroundHandler;
        private LeavingBackgroundEventHandler _leavingBackgroundHandler;
        private SuspendingEventHandler _suspendingHandler;
        private EventHandler<object> _resumingHandler;
        private EventHandler _sessionsChangedHandler;
        private EventHandler<SettingChangedEventArgs> _settingsChangedHandler;
        private NetworkMonitor _network;
        private EventHandler _networkChangedHandler;

        public LifecycleService(
            SessionManager sessions,
            SettingsRepository settings,
            KeepAwakeService keepAwake,
            ILogger logger)
        {
            if (sessions == null)
            {
                throw new ArgumentNullException("sessions");
            }
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }
            if (keepAwake == null)
            {
                throw new ArgumentNullException("keepAwake");
            }
            _sessions = sessions;
            _settings = settings;
            _keepAwake = keepAwake;
            _logger = logger;
            _policy = new BackgroundPolicy(ReadSettings());
        }

        // 只读观测（诊断用）。
        public bool IsInBackground
        {
            get { lock (_sync) { return _policy.IsInBackground; } }
        }

        // S14：前后台透出事件（同步触发器订阅：回前台补同步 + 开轮询，进后台停轮询）。
        // 在系统事件收敛完成后触发；订阅方异常被吞掉并记日志，绝不影响会话收敛路径。
        public event EventHandler ReturnedToForeground;

        public event EventHandler EnteredBackground;

        public bool IsPolicySuspended
        {
            get { lock (_sync) { return _policy.IsSuspended; } }
        }

        // App.OnLaunched（服务就绪后）调用；绝不阻断启动，异常只记日志。
        public void Start()
        {
            if (_started)
            {
                return;
            }
            _started = true;
            try
            {
                lock (_sync)
                {
                    _policy = new BackgroundPolicy(ReadSettings());
                    _policy.OnSessionCountChanged(SafeActiveCount());
                }
                _enteredBackgroundHandler = OnEnteredBackground;
                _leavingBackgroundHandler = OnLeavingBackground;
                _suspendingHandler = OnSuspending;
                _resumingHandler = OnResuming;
                _sessionsChangedHandler = OnSessionsChanged;
                _settingsChangedHandler = OnSettingsChanged;
                Application app = Application.Current;
                if (app != null)
                {
                    app.EnteredBackground += _enteredBackgroundHandler;
                    app.LeavingBackground += _leavingBackgroundHandler;
                    app.Suspending += _suspendingHandler;
                    app.Resuming += _resumingHandler;
                }
                _sessions.SessionsChanged += _sessionsChangedHandler;
                _settings.Changed += _settingsChangedHandler;
                _logger?.Log(LogLevel.Info, "Lifecycle", "started");
            }
            catch (Exception ex)
            {
                _logger?.Log(LogLevel.Warning, "Lifecycle", "start failed " + ex.GetType().Name);
            }
        }

        public void Stop()
        {
            if (!_started)
            {
                return;
            }
            _started = false;
            try
            {
                Application app = Application.Current;
                if (app != null)
                {
                    if (_enteredBackgroundHandler != null)
                    {
                        app.EnteredBackground -= _enteredBackgroundHandler;
                    }
                    if (_leavingBackgroundHandler != null)
                    {
                        app.LeavingBackground -= _leavingBackgroundHandler;
                    }
                    if (_suspendingHandler != null)
                    {
                        app.Suspending -= _suspendingHandler;
                    }
                    if (_resumingHandler != null)
                    {
                        app.Resuming -= _resumingHandler;
                    }
                }
                if (_sessionsChangedHandler != null)
                {
                    _sessions.SessionsChanged -= _sessionsChangedHandler;
                }
                if (_settingsChangedHandler != null)
                {
                    _settings.Changed -= _settingsChangedHandler;
                }
                EventHandler networkHandler;
                NetworkMonitor network;
                lock (_sync)
                {
                    networkHandler = _networkChangedHandler;
                    network = _network;
                    _networkChangedHandler = null;
                    _network = null;
                    CancelTimerLocked();
                    ReleaseExecutionLocked();
                }
                if (network != null && networkHandler != null)
                {
                    try
                    {
                        network.NetworkChanged -= networkHandler;
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Log(LogLevel.Warning, "Lifecycle", "stop failed " + ex.GetType().Name);
            }
        }

        // P02：接入网络变化（AppServices 在 NetworkMonitor.Start 之前调用）。
        // Monitor 已做 1 s 防抖与「适配器 id + 连接级别」判据，此处只做收敛执行：
        // 退避中的会话立即重连、已连接的 ProbeNow（见 SessionManager.OnNetworkChanged）。
        // S14 同步触发器直接订阅 NetworkMonitor.NetworkChanged，本类不转发现象。
        public void WatchNetwork(NetworkMonitor monitor)
        {
            if (monitor == null)
            {
                return;
            }
            lock (_sync)
            {
                if (_network != null)
                {
                    return;
                }
                _network = monitor;
                _networkChangedHandler = OnNetworkChanged;
            }
            monitor.NetworkChanged += _networkChangedHandler;
        }

        // ---- 系统事件入口 ----

        private async void OnEnteredBackground(object sender, EnteredBackgroundEventArgs e)
        {
            Deferral deferral = null;
            try
            {
                deferral = e.GetDeferral();
            }
            catch (Exception)
            {
                // 拿不到 deferral 也继续：尽力申请，不阻塞系统挂起。
            }
            try
            {
                _keepAwake.OnBackground();
                BackgroundAction[] actions;
                lock (_sync)
                {
                    actions = _policy.OnEnteredBackground();
                }
                bool wantExecution = false;
                for (int i = 0; i < actions.Length; i++)
                {
                    if (actions[i].Kind == BackgroundActionKind.RequestExtendedExecution)
                    {
                        wantExecution = true;
                    }
                    else
                    {
                        Execute(actions[i]);
                    }
                }
                // 扩展执行申请是异步的，用后台 deferral 覆盖它：挂起前必须拿到
                // Allowed/Denied 的确切结论，否则进程冻结后会话静默失联。
                if (wantExecution)
                {
                    await RequestExecutionAsync().ConfigureAwait(true);
                }
                _logger?.Log(LogLevel.Info, "Lifecycle", "background sessions=" + SafeActiveCount());
                RaiseEnteredBackground();
            }
            catch (Exception ex)
            {
                _logger?.Log(LogLevel.Warning, "Lifecycle", "background failed " + ex.GetType().Name);
            }
            finally
            {
                if (deferral != null)
                {
                    try
                    {
                        deferral.Complete();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private void OnLeavingBackground(object sender, LeavingBackgroundEventArgs e)
        {
            ForegroundCore("leaving-background");
        }

        private void OnResuming(object sender, object e)
        {
            ForegroundCore("resuming");
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            // 落盘由 App.xaml.cs 的 OnSuspending 负责；此处只打内存标记，
            // 供回前台时区分「重连 / 探测」（01-DESIGN.md §10）。
            try
            {
                _sessions.MarkOnlineBeforeSuspend();
            }
            catch (Exception ex)
            {
                _logger?.Log(LogLevel.Warning, "Lifecycle", "mark suspend failed " + ex.GetType().Name);
            }
            // K03：挂起时清除 agent 内存（§12.1）。只记条数，无敏感内容。
            try
            {
                _sessions.LockAgentKeys();
            }
            catch (Exception ex)
            {
                _logger?.Log(LogLevel.Warning, "Lifecycle", "agent lock failed " + ex.GetType().Name);
            }
        }

        // P02：切网收敛（线程池线程触发；SessionManager 内部已做 UI 封送，
        // 此处直接调用。日志只记会话计数）。
        private void OnNetworkChanged(object sender, EventArgs e)
        {
            int n;
            try
            {
                n = _sessions.OnNetworkChanged();
            }
            catch (Exception ex)
            {
                _logger?.Log(LogLevel.Warning, "Lifecycle", "network converge failed " + ex.GetType().Name);
                return;
            }
            _logger?.Log(LogLevel.Info, "Lifecycle", "network changed sessions=" + n);
        }

        private void OnSessionsChanged(object sender, EventArgs e)
        {
            BackgroundAction[] actions;
            lock (_sync)
            {
                actions = _policy.OnSessionCountChanged(SafeActiveCount());
            }
            Run(actions);
            try
            {
                _keepAwake.NotifySessionsChanged();
            }
            catch (Exception ex)
            {
                _logger?.Log(LogLevel.Warning, "Lifecycle", "keepawake refresh failed " + ex.GetType().Name);
            }
        }

        private void OnSettingsChanged(object sender, SettingChangedEventArgs e)
        {
            if (e == null || (!string.Equals(e.Key, "keepAliveInBackground", StringComparison.Ordinal)
                && !string.Equals(e.Key, "backgroundDisconnectMinutes", StringComparison.Ordinal)
                && !string.Equals(e.Key, "keepScreenOn", StringComparison.Ordinal)))
            {
                return;
            }
            BackgroundAction[] actions;
            lock (_sync)
            {
                actions = _policy.OnSettingsChanged(ReadSettings());
            }
            Run(actions);
            try
            {
                _keepAwake.RefreshSettings();
            }
            catch (Exception ex)
            {
                _logger?.Log(LogLevel.Warning, "Lifecycle", "keepawake refresh failed " + ex.GetType().Name);
            }
        }

        // ---- 扩展执行 ----

        private async Task RequestExecutionAsync()
        {
            ExtendedExecutionSession session;
            lock (_sync)
            {
                if (_execution != null)
                {
                    return;
                }
                session = new ExtendedExecutionSession();
                session.Reason = ExtendedExecutionReason.Unspecified;
                session.Description = "SSH 会话后台保活";
                session.Revoked += OnExecutionRevoked;
            }
            BackgroundAction[] follow;
            bool allowed = false;
            try
            {
                ExtendedExecutionResult result = await session.RequestExtensionAsync();
                lock (_sync)
                {
                    if (result == ExtendedExecutionResult.Allowed)
                    {
                        allowed = true;
                        _execution = session;
                        follow = _policy.OnExtendedExecutionGranted();
                    }
                    else
                    {
                        follow = _policy.OnExtendedExecutionDenied();
                    }
                }
                _logger?.Log(LogLevel.Info, "Lifecycle", "execution result=" + result);
            }
            catch (Exception ex)
            {
                _logger?.Log(LogLevel.Warning, "Lifecycle", "execution request failed " + ex.GetType().Name);
                lock (_sync)
                {
                    follow = _policy.OnExtendedExecutionDenied();
                }
            }
            if (!allowed)
            {
                try
                {
                    session.Revoked -= OnExecutionRevoked;
                    session.Dispose();
                }
                catch (Exception)
                {
                }
            }
            Run(follow);
        }

        private void OnExecutionRevoked(object sender, ExtendedExecutionRevokedEventArgs args)
        {
            ExtendedExecutionSession session = sender as ExtendedExecutionSession;
            BackgroundAction[] follow;
            lock (_sync)
            {
                if (session != null && _execution != null && session != _execution)
                {
                    // 陈旧会话的撤销：已有更新的持有，不惊动策略机。
                    follow = new BackgroundAction[0];
                }
                else
                {
                    if (session != null && session == _execution)
                    {
                        _execution = null;
                    }
                    follow = _policy.OnExtendedExecutionRevoked();
                }
            }
            if (session != null)
            {
                try
                {
                    session.Revoked -= OnExecutionRevoked;
                    session.Dispose();
                }
                catch (Exception)
                {
                }
            }
            // RevokedReason 是系统枚举，无 PII，可记。
            _logger?.Log(LogLevel.Info, "Lifecycle", "execution revoked=" + args.Reason);
            Run(follow);
        }

        // ---- 动作执行 ----

        private void Run(BackgroundAction[] actions)
        {
            if (actions == null)
            {
                return;
            }
            for (int i = 0; i < actions.Length; i++)
            {
                if (actions[i].Kind == BackgroundActionKind.RequestExtendedExecution)
                {
                    // 同步入口（会话数/设置变化）上的补申请：尽力而为，不阻塞调用方。
                    // EnteredBackground 路径由 deferral 覆盖，此处不会重复（策略机幂等）。
                    RequestExecutionAsync().Forget("Lifecycle.RequestExecution", AppLog.Logger);
                }
                else
                {
                    Execute(actions[i]);
                }
            }
        }

        private void Execute(BackgroundAction action)
        {
            try
            {
                switch (action.Kind)
                {
                    case BackgroundActionKind.ReleaseExtendedExecution:
                        lock (_sync)
                        {
                            ReleaseExecutionLocked();
                        }
                        _logger?.Log(LogLevel.Info, "Lifecycle", "execution released");
                        break;
                    case BackgroundActionKind.PolicyDisconnect:
                        {
                            int n = _sessions.SuspendAllForPolicy(action.Reason);
                            _logger?.Log(LogLevel.Info, "Lifecycle", "policy disconnect n=" + n);
                            break;
                        }
                    case BackgroundActionKind.ResumeReconnect:
                        {
                            int n = _sessions.ResumeSuspended();
                            _logger?.Log(LogLevel.Info, "Lifecycle", "policy resume n=" + n);
                            break;
                        }
                    case BackgroundActionKind.ProbeSessions:
                        {
                            int n = _sessions.ProbeAll();
                            _logger?.Log(LogLevel.Info, "Lifecycle", "probe n=" + n);
                            break;
                        }
                    case BackgroundActionKind.ArmBackgroundTimer:
                        ArmTimer(action.DelaySeconds);
                        break;
                    case BackgroundActionKind.CancelBackgroundTimer:
                        lock (_sync)
                        {
                            CancelTimerLocked();
                        }
                        break;
                    default:
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger?.Log(LogLevel.Warning, "Lifecycle", "action failed " + action.Kind + " " + ex.GetType().Name);
            }
        }

        private void ForegroundCore(string source)
        {
            try
            {
                _keepAwake.OnForeground();
                BackgroundAction[] actions;
                lock (_sync)
                {
                    actions = _policy.OnReturnedToForeground();
                }
                Run(actions);
                _sessions.ClearSuspendMarks();
                _logger?.Log(LogLevel.Info, "Lifecycle", "foreground via " + source);
                RaiseReturnedToForeground();
            }
            catch (Exception ex)
            {
                _logger?.Log(LogLevel.Warning, "Lifecycle", "foreground failed " + ex.GetType().Name);
            }
        }

        // ---- 计时器（后台 N 分钟断开） ----

        private void ArmTimer(int delaySeconds)
        {
            if (delaySeconds < 1)
            {
                delaySeconds = 1;
            }
            lock (_sync)
            {
                CancelTimerLocked();
                try
                {
                    _backgroundTimer = ThreadPoolTimer.CreateTimer(
                        OnBackgroundTimer, TimeSpan.FromSeconds(delaySeconds));
                    _logger?.Log(LogLevel.Info, "Lifecycle", "background timer armed s=" + delaySeconds);
                }
                catch (Exception ex)
                {
                    _logger?.Log(LogLevel.Warning, "Lifecycle", "timer arm failed " + ex.GetType().Name);
                }
            }
        }

        private void OnBackgroundTimer(ThreadPoolTimer timer)
        {
            BackgroundAction[] follow;
            lock (_sync)
            {
                _backgroundTimer = null;
                follow = _policy.OnBackgroundTimerExpired();
            }
            Run(follow);
        }

        // 调用方已持有 _sync。
        private void CancelTimerLocked()
        {
            ThreadPoolTimer timer = _backgroundTimer;
            _backgroundTimer = null;
            if (timer != null)
            {
                try
                {
                    timer.Cancel();
                }
                catch (Exception)
                {
                }
            }
        }

        // 调用方已持有 _sync。
        private void ReleaseExecutionLocked()
        {
            ExtendedExecutionSession session = _execution;
            _execution = null;
            if (session != null)
            {
                try
                {
                    session.Revoked -= OnExecutionRevoked;
                    session.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        // ---- 设置与计数快照（读失败即用安全默认，绝不阻断生命周期路径） ----

        private void RaiseReturnedToForeground()
        {
            EventHandler handler = ReturnedToForeground;
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger?.Log(LogLevel.Warning, "Lifecycle", "foreground listener failed " + ex.GetType().Name);
            }
        }

        private void RaiseEnteredBackground()
        {
            EventHandler handler = EnteredBackground;
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger?.Log(LogLevel.Warning, "Lifecycle", "background listener failed " + ex.GetType().Name);
            }
        }

        private BackgroundSettings ReadSettings()
        {
            bool keepAlive = true;
            int minutes = 0;
            try
            {
                keepAlive = _settings.KeepAliveInBackground;
            }
            catch (Exception)
            {
            }
            try
            {
                minutes = _settings.BackgroundDisconnectMinutes;
            }
            catch (Exception)
            {
            }
            return new BackgroundSettings(keepAlive, minutes);
        }

        private int SafeActiveCount()
        {
            try
            {
                return _sessions.ActiveSessionCount;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
