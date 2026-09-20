using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Sessions;
using SshTool.Core.Sync;

namespace SshTool.Core.Forwarding
{
    // 运行时侧启动结果（App 层 NativeForwarder 填写）：Code 为 SshErrorCode
    //（None = 成功），Message 为可直接展示的诊断，RouteDescription 用于 Running
    // 态消息，BoundPort 回显实际绑定端口（本地/远端监听都恒为请求值）。
    public sealed class TunnelRuntimeStartResult
    {
        public SshErrorCode Code { get; set; }
        public string Message { get; set; }
        public int BoundPort { get; set; }
        public string RouteDescription { get; set; }

        public bool Success
        {
            get { return Code == SshErrorCode.None; }
        }
    }

    // 运行中断事件（native 会话断开/错误）。在运行时线程抛出，管理器负责收敛。
    public sealed class TunnelRuntimeDroppedEventArgs : EventArgs
    {
        public string TunnelId { get; set; }
        public string Reason { get; set; }
        public SshErrorCode ErrorCode { get; set; }
    }

    // 隧道转发运行时抽象（01-DESIGN.md §4.1：Core 不引用 Native）。实现：
    // App 层 NativeForwarderRuntime（每条隧道独占一条 native SSH 会话 + 一个
    // 转发监听）；测试用可脚本化假实现。契约（对齐桌面端 tunnel.ts）：
    //   - StartAsync 建立该隧道自己的 SSH 连接并启动监听，监听就绪（远端绑定
    //     成功）才返回成功；首启失败必须返回可展示的消息。不阻塞调用线程。
    //   - Stop 幂等且必须快速返回（只投递，不等待拆尾）；隧道重启一律换新会话。
    //   - ReadSample 读取 native 原子计数（快速返回），未运行返回 null。
    //   - 运行中掉线必须经 Dropped 上抛，不得静默。
    public interface ITunnelRuntime
    {
        Task<TunnelRuntimeStartResult> StartAsync(Tunnel tunnel, CancellationToken cancellation);
        void Stop(string tunnelId);
        TunnelStatsSample ReadSample(string tunnelId);
        event EventHandler<TunnelRuntimeDroppedEventArgs> Dropped;
    }

    // 多隧道管理器（F05；桌面端 manager.ts + tunnel.ts 状态机的 Core 移植）：
    //   - 每条隧道独立状态机 idle→connecting→running；中断走自动重连退避
    //     （1/2/5/10/20/30/60 s，MaxBackoffSeconds 封顶）或 error；
    //   - autoStart 批量开启、分组启停、配置热替换（ApplyConfig）；
    //   - 每秒 Tick 刷新速率统计（累计值来自运行时样本差值）；
    //   - 实现 S10 下行保护的 ITunnelBusyProbe.IsBusy；
    //   - relay 拒绝启动（§11.2：仅桌面端运行）。
    //
    // 线程：状态字段统一由 _gate 保护；UI 通知经 IUiDispatcher 封送；运行时
    // 事件（Dropped）可在任意线程到达。定时器由 ITimerFactory 提供（测试注入
    // ManualTimerFactory，App 注入 DispatcherTimerFactory）。
    // 脱敏：日志只记隧道 id、状态与错误类别，不记录任何凭据或地址内容。
    public sealed class TunnelManager : ITunnelBusyProbe, IDisposable
    {
        public const int TickPeriodMs = 1000;
        public const int DefaultMaxBackoffSeconds = 30;
        public const string RelayDisabledMessage = "中转隧道仅在桌面端运行";

        // 桌面端 tunnel.ts 的 BACKOFF_STEPS。
        public static readonly int[] BackoffSeconds = { 1, 2, 5, 10, 20, 30, 60 };

        private readonly ITunnelRuntime _runtime;
        private readonly ITimerFactory _timers;
        private readonly IUiDispatcher _ui;
        private readonly ILogger _logger;
        private readonly object _gate = new object();
        private readonly Dictionary<string, TunnelInstance> _instances =
            new Dictionary<string, TunnelInstance>(StringComparer.Ordinal);
        private readonly List<Tunnel> _configs = new List<Tunnel>();
        private IDisposable _tickTimer;

        public TunnelManager(
            ITunnelRuntime runtime,
            ITimerFactory timers,
            IUiDispatcher ui,
            ILogger logger = null)
        {
            if (runtime == null) throw new ArgumentNullException("runtime");
            if (timers == null) throw new ArgumentNullException("timers");
            if (ui == null) throw new ArgumentNullException("ui");
            _runtime = runtime;
            _timers = timers;
            _ui = ui;
            _logger = logger;
            MaxBackoffSeconds = DefaultMaxBackoffSeconds;
            _runtime.Dropped += OnRuntimeDropped;
            _tickTimer = _timers.SchedulePeriodic(TickPeriodMs, Tick);
        }

        public int MaxBackoffSeconds { get; set; }

        public event EventHandler StatusesChanged;

        // 当前配置快照（ApplyConfig 存入的克隆）。
        public IReadOnlyList<Tunnel> Configurations
        {
            get
            {
                lock (_gate)
                {
                    return _configs.ToArray();
                }
            }
        }

        // 全量状态列表：配置里的每条隧道一条（无实例的为 Idle），与桌面端
        // statuses() 一致。
        public IReadOnlyList<TunnelStatusSnapshot> Statuses
        {
            get
            {
                lock (_gate)
                {
                    var list = new List<TunnelStatusSnapshot>(_configs.Count);
                    for (int i = 0; i < _configs.Count; i++)
                    {
                        list.Add(BuildStatusLocked(_configs[i].Id));
                    }
                    return list;
                }
            }
        }

        public TunnelStatusSnapshot GetStatus(string tunnelId)
        {
            if (tunnelId == null)
            {
                return null;
            }
            lock (_gate)
            {
                return BuildStatusLocked(tunnelId);
            }
        }

        // S10 下行保护：connecting/running/reconnecting 的隧道不允许被同步清空。
        public bool IsBusy(string tunnelId)
        {
            if (tunnelId == null)
            {
                return false;
            }
            lock (_gate)
            {
                TunnelInstance instance;
                if (!_instances.TryGetValue(tunnelId, out instance) || instance == null)
                {
                    return false;
                }
                return IsBusyLocked(instance);
            }
        }

        // 启动一条隧道。relay 拒绝；已在运行/连接中则幂等返回当前状态。
        // 首启失败返回失败结果（状态置 Error），重试失败进入退避链。
        public async Task<TunnelStartResult> StartAsync(Tunnel tunnel)
        {
            if (tunnel == null)
            {
                throw new ArgumentNullException("tunnel");
            }
            if (tunnel.Type == TunnelType.Relay)
            {
                Log(LogLevel.Info, "relay 拒绝启动 " + tunnel.Id);
                return TunnelStartResult.Fail(RelayDisabledMessage);
            }
            TunnelInstance instance = EnsureInstance(tunnel);
            lock (_gate)
            {
                if (IsBusyLocked(instance) || instance.Starting)
                {
                    return TunnelStartResult.Ok(); // 已在运行/连接中：幂等
                }
                instance.Starting = true;
                instance.ManualStop = false;
                instance.Attempt = 0;
                instance.RetryInSeconds = 0;
                instance.Stats.Reset();
            }
            TunnelRuntimeStartResult result = await StartRuntimeAsync(instance).ConfigureAwait(false);
            return FinalizeStart(instance, result);
        }

        // 停止一条隧道（幂等）：取消重连计时、停运行时、回 Idle。
        public void Stop(string tunnelId, string reason = null)
        {
            if (tunnelId == null)
            {
                return;
            }
            lock (_gate)
            {
                TunnelInstance instance;
                if (!_instances.TryGetValue(tunnelId, out instance) || instance == null)
                {
                    return;
                }
                instance.ManualStop = true;
                CancelRetryTimerLocked(instance);
                StopRuntimeLocked(instance, reason ?? "已手动停止");
            }
            RaiseStatuses();
        }

        public void StopGroup(string groupId, string reason = null)
        {
            Tunnel[] targets = GroupTargets(groupId);
            for (int i = 0; i < targets.Length; i++)
            {
                Stop(targets[i].Id, reason);
            }
        }

        public void StopAll(string reason = null)
        {
            TunnelInstance[] all;
            lock (_gate)
            {
                all = new TunnelInstance[_instances.Count];
                _instances.Values.CopyTo(all, 0);
            }
            for (int i = 0; i < all.Length; i++)
            {
                Stop(all[i].Config.Id, reason ?? "已手动停止");
            }
        }

        // 分组启动：只启动 Enabled 且未在运行的隧道（与桌面端 startGroup 一致），
        // 返回「名称：消息」错误列表（空列表 = 全部成功）。
        public async Task<IReadOnlyList<string>> StartGroupAsync(string groupId)
        {
            var errors = new List<string>();
            Tunnel[] targets = GroupTargets(groupId);
            for (int i = 0; i < targets.Length; i++)
            {
                Tunnel config = targets[i];
                if (!config.Enabled)
                {
                    continue;
                }
                if (IsBusy(config.Id))
                {
                    continue;
                }
                TunnelStartResult result = await StartAsync(config).ConfigureAwait(false);
                if (!result.Success)
                {
                    errors.Add((config.Name ?? config.Id) + "：" + result.Message);
                }
            }
            return errors;
        }

        // 程序启动后自动开启标记了 AutoStart 的隧道（桌面端 startAutoStart）。
        // 单条失败只记日志、不中断其余隧道；返回错误列表。
        public async Task<IReadOnlyList<string>> StartAutoStartAsync()
        {
            var errors = new List<string>();
            Tunnel[] configs;
            lock (_gate)
            {
                configs = _configs.ToArray();
            }
            for (int i = 0; i < configs.Length; i++)
            {
                Tunnel config = configs[i];
                if (!config.AutoStart || !config.Enabled)
                {
                    continue;
                }
                TunnelStartResult result = await StartAsync(config).ConfigureAwait(false);
                if (!result.Success)
                {
                    errors.Add((config.Name ?? config.Id) + "：" + result.Message);
                }
            }
            return errors;
        }

        // 配置变更后调用：移除已删除隧道的实例（先停止），更新存量实例的暂存
        // 配置（运行中的保持现状，下次重启生效——与桌面端 update 语义一致）。
        public void ApplyConfig(IReadOnlyList<Tunnel> tunnels)
        {
            if (tunnels == null)
            {
                throw new ArgumentNullException("tunnels");
            }
            var removed = new List<string>();
            lock (_gate)
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                _configs.Clear();
                for (int i = 0; i < tunnels.Count; i++)
                {
                    Tunnel clone = tunnels[i] == null ? null : tunnels[i].Clone();
                    if (clone == null || string.IsNullOrEmpty(clone.Id))
                    {
                        continue;
                    }
                    ids.Add(clone.Id);
                    _configs.Add(clone);
                    TunnelInstance existing;
                    if (_instances.TryGetValue(clone.Id, out existing) && existing != null)
                    {
                        existing.Config = clone;
                    }
                }
                foreach (KeyValuePair<string, TunnelInstance> pair in _instances)
                {
                    if (!ids.Contains(pair.Key))
                    {
                        removed.Add(pair.Key);
                    }
                }
                for (int i = 0; i < removed.Count; i++)
                {
                    TunnelInstance instance = _instances[removed[i]];
                    instance.ManualStop = true;
                    CancelRetryTimerLocked(instance);
                    StopRuntimeLocked(instance, "配置已删除");
                    _instances.Remove(removed[i]);
                }
            }
            RaiseStatuses();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_tickTimer != null)
                {
                    _tickTimer.Dispose();
                    _tickTimer = null;
                }
            }
            StopAll("管理器已释放");
            lock (_gate)
            {
                _configs.Clear();
                _instances.Clear();
            }
        }

        // ---------------------------------------------------------------- 内部

        private TunnelInstance EnsureInstance(Tunnel tunnel)
        {
            lock (_gate)
            {
                TunnelInstance instance;
                if (_instances.TryGetValue(tunnel.Id, out instance) && instance != null)
                {
                    instance.Config = ResolveConfigLocked(tunnel.Id) ?? tunnel.Clone();
                    return instance;
                }
                instance = new TunnelInstance(ResolveConfigLocked(tunnel.Id) ?? tunnel.Clone());
                _instances[tunnel.Id] = instance;
                return instance;
            }
        }

        // 配置表优先（ApplyConfig 后以最新配置启动），退回调用方传入的克隆。
        private Tunnel ResolveConfigLocked(string tunnelId)
        {
            for (int i = 0; i < _configs.Count; i++)
            {
                if (string.Equals(_configs[i].Id, tunnelId, StringComparison.Ordinal))
                {
                    return _configs[i];
                }
            }
            return null;
        }

        private Tunnel[] GroupTargets(string groupId)
        {
            lock (_gate)
            {
                var targets = new List<Tunnel>();
                for (int i = 0; i < _configs.Count; i++)
                {
                    if (string.Equals(_configs[i].GroupId, groupId, StringComparison.Ordinal))
                    {
                        targets.Add(_configs[i]);
                    }
                }
                return targets.ToArray();
            }
        }

        private static bool IsBusyLocked(TunnelInstance instance)
        {
            return instance.State == TunnelStateKind.Connecting
                || instance.State == TunnelStateKind.Running
                || instance.State == TunnelStateKind.Reconnecting;
        }

        // 一次「连接 + 建监听」尝试：attempt == 0 为首启，失败置 Error 并把消息
        // 返回调用方；attempt > 0 的重试失败继续退避（与桌面端 connect 里
        // reconnecting 态 + handleFailure(first) 语义一致）。
        private async Task<TunnelRuntimeStartResult> StartRuntimeAsync(TunnelInstance instance)
        {
            lock (_gate)
            {
                instance.State = instance.Attempt > 0
                    ? TunnelStateKind.Reconnecting
                    : TunnelStateKind.Connecting;
                instance.Message = "正在连接…";
            }
            RaiseStatuses();
            try
            {
                return await _runtime.StartAsync(instance.Config, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new TunnelRuntimeStartResult
                {
                    Code = SshErrorCode.InternalError,
                    Message = ex.GetType().Name,
                    RouteDescription = string.Empty
                };
            }
        }

        private TunnelStartResult FinalizeStart(TunnelInstance instance, TunnelRuntimeStartResult result)
        {
            string message = result == null ? string.Empty : (result.Message ?? string.Empty);
            lock (_gate)
            {
                instance.Starting = false;
                if (instance.ManualStop)
                {
                    // 启动期间被手动停止：回收运行时并回 Idle。
                    StopRuntimeLocked(instance, "已手动停止");
                    RaiseStatuses();
                    return TunnelStartResult.Ok();
                }
                if (result != null && result.Success)
                {
                    instance.Attempt = 0;
                    instance.RetryInSeconds = 0;
                    CancelRetryTimerLocked(instance);
                    instance.Stats.Since = DateTime.UtcNow;
                    instance.State = TunnelStateKind.Running;
                    instance.Message = string.IsNullOrEmpty(result.RouteDescription)
                        ? "隧道已建立"
                        : result.RouteDescription;
                    RaiseStatuses();
                    return TunnelStartResult.Ok();
                }
                instance.Stats.MarkStopped();
                _runtime.Stop(instance.Config.Id);
                if (result != null && instance.Attempt == 0)
                {
                    instance.State = TunnelStateKind.Error;
                    instance.Message = message;
                    RaiseStatuses();
                    return TunnelStartResult.Fail(message);
                }
                if (result != null && instance.Config.AutoReconnect)
                {
                    // 重试失败：继续退避链（消息已由 ScheduleRetryLocked 改写）。
                    ScheduleRetryLocked(instance, message);
                    RaiseStatuses();
                    return TunnelStartResult.Fail(message);
                }
                instance.State = TunnelStateKind.Error;
                instance.Message = result == null ? "启动失败" : message;
                RaiseStatuses();
                return TunnelStartResult.Fail(instance.Message);
            }
        }

        private void OnRuntimeDropped(object sender, TunnelRuntimeDroppedEventArgs e)
        {
            if (e == null || e.TunnelId == null)
            {
                return;
            }
            lock (_gate)
            {
                TunnelInstance instance;
                if (!_instances.TryGetValue(e.TunnelId, out instance) || instance == null)
                {
                    return;
                }
                if (instance.ManualStop || instance.Starting
                    || instance.State == TunnelStateKind.Idle
                    || instance.State == TunnelStateKind.Error)
                {
                    return; // 用户已停/启动失败路径自会收敛/终态不回跳
                }
                string reason = e.Reason ?? "隧道中断";
                StopRuntimeLocked(instance, reason);
                if (instance.Config.AutoReconnect)
                {
                    ScheduleRetryLocked(instance, reason);
                }
                else
                {
                    instance.State = TunnelStateKind.Error;
                    instance.Message = reason;
                }
            }
            RaiseStatuses();
        }

        // 桌面端 scheduleRetry：退避表按 attempt 取档、封顶 MaxBackoff；
        // RetryInSeconds 仅作展示倒计时（由 Tick 递减），重连由一次性计时器驱动。
        private void ScheduleRetryLocked(TunnelInstance instance, string reason)
        {
            CancelRetryTimerLocked(instance);
            int index = Math.Min(instance.Attempt, BackoffSeconds.Length - 1);
            int delay = Math.Min(BackoffSeconds[index], Math.Max(1, MaxBackoffSeconds));
            instance.Attempt += 1;
            instance.RetryInSeconds = delay;
            instance.State = TunnelStateKind.Reconnecting;
            instance.Message = reason + "，" + delay.ToString(CultureInfo.InvariantCulture)
                + " 秒后重连";
            instance.RetryTimer = null;
            IDisposable timer = null;
            timer = _timers.Schedule(
                delay * TickPeriodMs,
                () => OnRetryTimer(instance, timer));
            instance.RetryTimer = timer;
        }

        // 一次性计时器触发：仅接受当前在册的计时器（测试环境的手动计时器可能
        // 被反复触发，过期的丢弃，防止同一次退避被重复重连）。
        private void OnRetryTimer(TunnelInstance instance, IDisposable timer)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(instance.RetryTimer, timer))
                {
                    return; // 过期重复触发：丢弃
                }
                instance.RetryTimer = null;
                instance.RetryInSeconds = 0;
                if (instance.ManualStop || instance.State != TunnelStateKind.Reconnecting)
                {
                    return;
                }
            }
            var ignore = RetryCoreAsync(instance);
        }

        private async Task RetryCoreAsync(TunnelInstance instance)
        {
            TunnelRuntimeStartResult result = await StartRuntimeAsync(instance).ConfigureAwait(false);
            TunnelStartResult outcome = FinalizeStart(instance, result);
            if (!outcome.Success)
            {
                Log(LogLevel.Debug, "重连失败 " + outcome.Message);
            }
        }

        // 停运行时并回 Idle（累计统计保留）。约定 ITunnelRuntime.Stop 快速返回。
        private void StopRuntimeLocked(TunnelInstance instance, string reason)
        {
            instance.State = TunnelStateKind.Idle;
            instance.Message = reason ?? string.Empty;
            instance.Stats.MarkStopped();
            _runtime.Stop(instance.Config.Id);
        }

        private void CancelRetryTimerLocked(TunnelInstance instance)
        {
            if (instance.RetryTimer != null)
            {
                instance.RetryTimer.Dispose();
                instance.RetryTimer = null;
            }
            instance.RetryInSeconds = 0;
        }

        // 每秒：运行中的刷新速率（样本差值）、退避倒计时递减、有变化才广播。
        private void Tick()
        {
            bool changed = false;
            lock (_gate)
            {
                foreach (KeyValuePair<string, TunnelInstance> pair in _instances)
                {
                    TunnelInstance instance = pair.Value;
                    if (instance.State == TunnelStateKind.Running)
                    {
                        instance.Stats.Apply(_runtime.ReadSample(pair.Key));
                        changed = true;
                    }
                    else if (instance.State == TunnelStateKind.Reconnecting
                        && instance.RetryInSeconds > 0)
                    {
                        instance.RetryInSeconds -= 1;
                        changed = true;
                    }
                }
            }
            if (changed)
            {
                RaiseStatuses();
            }
        }

        private TunnelStatusSnapshot BuildStatusLocked(string tunnelId)
        {
            TunnelInstance instance;
            if (_instances.TryGetValue(tunnelId, out instance) && instance != null)
            {
                return new TunnelStatusSnapshot
                {
                    TunnelId = tunnelId,
                    State = instance.State,
                    Message = instance.Message ?? string.Empty,
                    RetryInSeconds = instance.RetryInSeconds,
                    Attempt = instance.Attempt,
                    Stats = instance.Stats
                };
            }
            return new TunnelStatusSnapshot
            {
                TunnelId = tunnelId,
                State = TunnelStateKind.Idle,
                Message = string.Empty,
                Stats = new TunnelStats()
            };
        }

        private void RaiseStatuses()
        {
            EventHandler handler = StatusesChanged;
            if (handler == null)
            {
                return;
            }
            _ui.Post(() =>
            {
                EventHandler posted = StatusesChanged;
                if (posted != null)
                {
                    posted(this, EventArgs.Empty);
                }
            });
        }

        private void Log(LogLevel level, string message)
        {
            if (_logger != null)
            {
                _logger.Log(level, "Tunnel", message);
            }
        }

        // 单条隧道的运行态（状态机字段 + 一次性重连计时器 + 统计）。仅由
        // TunnelManager 在 _gate 内访问。
        private sealed class TunnelInstance
        {
            public TunnelInstance(Tunnel config)
            {
                Config = config;
            }

            public Tunnel Config;
            public TunnelStateKind State = TunnelStateKind.Idle;
            public string Message = string.Empty;
            public int Attempt;
            public int RetryInSeconds;
            public readonly TunnelStats Stats = new TunnelStats();
            public bool ManualStop = true;
            public bool Starting;
            public IDisposable RetryTimer;
        }
    }
}
