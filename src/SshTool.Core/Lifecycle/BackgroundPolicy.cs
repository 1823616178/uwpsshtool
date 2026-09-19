using System;

namespace SshTool.Core.Lifecycle
{
    // P01：后台策略机要求外壳（LifecycleService）执行的动作种类。
    // 对齐任务要点：请求扩展执行 / 策略性断开 / 恢复重连 / 探测 / 释放，
    // 另加后台 N 分钟断开计时器的起/撤（01-DESIGN.md §10 backgroundDisconnectMinutes）。
    public enum BackgroundActionKind
    {
        RequestExtendedExecution = 0,
        ReleaseExtendedExecution = 1,
        PolicyDisconnect = 2,
        ResumeReconnect = 3,
        ProbeSessions = 4,
        ArmBackgroundTimer = 5,
        CancelBackgroundTimer = 6
    }

    // 一条动作。DelaySeconds 只对 ArmBackgroundTimer 有意义；Reason 只对
    // PolicyDisconnect 有意义（写入 SessionInfo.ErrorMessage 给用户看，均带出路）。
    public sealed class BackgroundAction
    {
        public BackgroundAction(BackgroundActionKind kind, int delaySeconds, string reason)
        {
            Kind = kind;
            DelaySeconds = delaySeconds;
            Reason = reason ?? string.Empty;
        }

        public BackgroundActionKind Kind { get; private set; }
        public int DelaySeconds { get; private set; }
        public string Reason { get; private set; }

        public static BackgroundAction Simple(BackgroundActionKind kind)
        {
            return new BackgroundAction(kind, 0, string.Empty);
        }

        public static BackgroundAction ArmTimer(int delaySeconds)
        {
            return new BackgroundAction(BackgroundActionKind.ArmBackgroundTimer, delaySeconds, string.Empty);
        }

        public static BackgroundAction Disconnect(string reason)
        {
            return new BackgroundAction(BackgroundActionKind.PolicyDisconnect, 0, reason);
        }
    }

    // 策略机读到的设置快照（来源：SettingsRepository 的两个键，01-DESIGN.md §8.3）。
    public sealed class BackgroundSettings
    {
        public BackgroundSettings(bool keepAliveInBackground, int backgroundDisconnectMinutes)
        {
            KeepAliveInBackground = keepAliveInBackground;
            BackgroundDisconnectMinutes = backgroundDisconnectMinutes < 0 ? 0 : backgroundDisconnectMinutes;
        }

        // 「后台保持连接」，默认开
        public bool KeepAliveInBackground { get; private set; }

        // 「后台 N 分钟后断开」，0 = 关（默认）
        public int BackgroundDisconnectMinutes { get; private set; }
    }

    // P01：后台保活策略机（纯状态机，零系统 API）。
    //
    // 对齐鸿蒙端 service/background/BackgroundPolicy.ets 的分层纪律
    //（判断全在策略机、系统调用全在外壳），按 W10M 现实裁剪：
    // W10M 没有 dataTransfer 长时任务，只有 ExtendedExecutionSession（可随时被拒/撤销，
    // 省电模式必撤，见 01-DESIGN.md R6），也没有短时任务宽限窗口——EE 被拒/撤销即按 §10
    // 「同被拒：策略性断开（405，保留 SessionInfo）」，回前台原地重连。
    //
    // 用法（外壳 LifecycleService 侧）：
    //   BackgroundAction[] actions = policy.OnEnteredBackground();
    //   foreach (BackgroundAction a in actions) { 按 a.Kind 执行 }
    // 每个输入方法都返回本次要执行的动作序列（可能为空），顺序即执行顺序。
    // 所有方法幂等、无副作用（除状态推进），可在任意线程调用（外壳负责串行化）。
    public sealed class BackgroundPolicy
    {
        // 断开原因文案（进 SessionInfo.ErrorMessage，故都带「回到应用后自动重连」的出路）。
        public const string ReasonBackgroundDisabled =
            "已按「后台保持连接」设置断开，回到应用后自动重连";
        public const string ReasonExtensionLost =
            "系统已收回后台执行，会话已断开，回到应用后自动重连";
        public const string ReasonBackgroundTimeout =
            "已按后台断开策略断开，回到应用后自动重连";

        private BackgroundSettings _settings;
        private bool _inBackground;
        private int _sessions;
        private bool _executionRequested;
        private bool _executionActive;
        private bool _timerArmed;
        private bool _suspended;

        public BackgroundPolicy(BackgroundSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }
            _settings = settings;
        }

        // ---- 只读观测（单测与诊断） ----

        public bool IsInBackground
        {
            get { return _inBackground; }
        }

        public int SessionCount
        {
            get { return _sessions; }
        }

        public bool IsExecutionActive
        {
            get { return _executionActive; }
        }

        public bool IsExecutionRequested
        {
            get { return _executionRequested; }
        }

        public bool IsTimerArmed
        {
            get { return _timerArmed; }
        }

        public bool IsSuspended
        {
            get { return _suspended; }
        }

        // ---- 前后台 ----

        // 切后台：有会话 + 允许保活 → 申请扩展执行（+ N 分钟计时器）；
        // 有会话 + 不允许保活 → 直接策略性断开（不等系统冻结后静默失联）；
        // 无会话 / 已挂起 → 无动作（后台白占资源是被系统整治的头号原因）。
        public BackgroundAction[] OnEnteredBackground()
        {
            _inBackground = true;
            if (_sessions <= 0 || _suspended)
            {
                return NoActions();
            }
            if (!_settings.KeepAliveInBackground)
            {
                return SuspendNow(ReasonBackgroundDisabled);
            }
            return RequestExecutionIfNeeded();
        }

        // 回前台：释放扩展执行（前台由系统天然保活，占着纯属耗电）+ 撤计时器；
        // 被挂起过 → 原地恢复（恢复本身就会重连，不必再探测）；
        // 否则有会话 → 做一次探测收敛（后台冻结期间链路可能已死，见 §10）。
        public BackgroundAction[] OnReturnedToForeground()
        {
            _inBackground = false;
            var actions = new System.Collections.Generic.List<BackgroundAction>();
            if (_executionActive || _executionRequested)
            {
                _executionActive = false;
                _executionRequested = false;
                actions.Add(BackgroundAction.Simple(BackgroundActionKind.ReleaseExtendedExecution));
            }
            if (_timerArmed)
            {
                _timerArmed = false;
                actions.Add(BackgroundAction.Simple(BackgroundActionKind.CancelBackgroundTimer));
            }
            if (_suspended)
            {
                _suspended = false;
                actions.Add(BackgroundAction.Simple(BackgroundActionKind.ResumeReconnect));
            }
            else if (_sessions > 0)
            {
                actions.Add(BackgroundAction.Simple(BackgroundActionKind.ProbeSessions));
            }
            return actions.ToArray();
        }

        // ---- 扩展执行生命周期 ----

        // 申请成功。注意迟到的成功回调：申请是异步的，用户可能在系统批下来之前
        // 就回了前台——此时补一次释放，否则扩展执行会一直挂着白耗电（同鸿蒙端处理）。
        public BackgroundAction[] OnExtendedExecutionGranted()
        {
            _executionRequested = false;
            _executionActive = true;
            if (!_inBackground)
            {
                _executionActive = false;
                return Single(BackgroundActionKind.ReleaseExtendedExecution);
            }
            return NoActions();
        }

        // 申请被拒：直接策略性断开，别假装还活着（进程随时会被冻结）。
        public BackgroundAction[] OnExtendedExecutionDenied()
        {
            _executionRequested = false;
            _executionActive = false;
            if (!NeedsBackgroundKeep())
            {
                return NoActions();
            }
            return SuspendNowWithTimerCancel(ReasonExtensionLost);
        }

        // 被系统撤销：同被拒（01-DESIGN.md §10）。
        public BackgroundAction[] OnExtendedExecutionRevoked()
        {
            _executionRequested = false;
            _executionActive = false;
            if (!NeedsBackgroundKeep())
            {
                return NoActions();
            }
            return SuspendNowWithTimerCancel(ReasonExtensionLost);
        }

        // ---- 会话数变化 ----

        // 后台且清零 → 立刻释放扩展执行 + 撤计时器（最后一个会话已关，再占着既费电
        // 又容易被系统盯上）；后台从零变有 → 补申请。挂起中不再申请。
        public BackgroundAction[] OnSessionCountChanged(int count)
        {
            int next = count < 0 ? 0 : count;
            if (next == _sessions)
            {
                return NoActions();
            }
            _sessions = next;
            if (!_inBackground || _suspended)
            {
                return NoActions();
            }
            var actions = new System.Collections.Generic.List<BackgroundAction>();
            if (_sessions == 0)
            {
                if (_executionActive || _executionRequested)
                {
                    _executionActive = false;
                    _executionRequested = false;
                    actions.Add(BackgroundAction.Simple(BackgroundActionKind.ReleaseExtendedExecution));
                }
                if (_timerArmed)
                {
                    _timerArmed = false;
                    actions.Add(BackgroundAction.Simple(BackgroundActionKind.CancelBackgroundTimer));
                }
                return actions.ToArray();
            }
            if (_settings.KeepAliveInBackground && !_executionActive && !_executionRequested)
            {
                _executionRequested = true;
                actions.Add(BackgroundAction.Simple(BackgroundActionKind.RequestExtendedExecution));
            }
            return actions.ToArray();
        }

        // ---- 后台 N 分钟计时器 ----

        // 计时到期：优雅断开并释放扩展执行（省电，用户要的是「别整夜挂着」）。
        public BackgroundAction[] OnBackgroundTimerExpired()
        {
            if (!_timerArmed)
            {
                return NoActions();
            }
            _timerArmed = false;
            var actions = new System.Collections.Generic.List<BackgroundAction>();
            if (!_inBackground || _sessions <= 0 || _suspended)
            {
                return actions.ToArray();
            }
            actions.Add(BackgroundAction.Disconnect(ReasonBackgroundTimeout));
            _suspended = true;
            if (_executionActive || _executionRequested)
            {
                _executionActive = false;
                _executionRequested = false;
                actions.Add(BackgroundAction.Simple(BackgroundActionKind.ReleaseExtendedExecution));
            }
            return actions.ToArray();
        }

        // ---- 设置变更 ----

        // 用户在设置页改了开关：立刻按新设置纠正当前姿态，不必等下一次前后台切换。
        public BackgroundAction[] OnSettingsChanged(BackgroundSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }
            BackgroundSettings previous = _settings;
            _settings = settings;
            var actions = new System.Collections.Generic.List<BackgroundAction>();

            // 计时器分钟数：关掉就撤销；改了分钟数且正 arming → 按新值重起。
            if (settings.BackgroundDisconnectMinutes <= 0)
            {
                if (_timerArmed)
                {
                    _timerArmed = false;
                    actions.Add(BackgroundAction.Simple(BackgroundActionKind.CancelBackgroundTimer));
                }
            }
            else if (_inBackground && _sessions > 0 && !_suspended)
            {
                if (!_timerArmed || settings.BackgroundDisconnectMinutes != previous.BackgroundDisconnectMinutes)
                {
                    if (_timerArmed)
                    {
                        actions.Add(BackgroundAction.Simple(BackgroundActionKind.CancelBackgroundTimer));
                    }
                    _timerArmed = true;
                    actions.Add(BackgroundAction.ArmTimer(settings.BackgroundDisconnectMinutes * 60));
                }
            }

            if (!_inBackground || _suspended || _sessions <= 0)
            {
                return actions.ToArray();
            }
            if (!settings.KeepAliveInBackground)
            {
                if (_executionActive || _executionRequested)
                {
                    _executionActive = false;
                    _executionRequested = false;
                    actions.Add(BackgroundAction.Simple(BackgroundActionKind.ReleaseExtendedExecution));
                }
                BackgroundAction[] suspend = SuspendNowWithTimerCancel(ReasonBackgroundDisabled);
                for (int i = 0; i < suspend.Length; i++)
                {
                    actions.Add(suspend[i]);
                }
                return actions.ToArray();
            }
            if (!_executionActive && !_executionRequested)
            {
                _executionRequested = true;
                actions.Add(BackgroundAction.Simple(BackgroundActionKind.RequestExtendedExecution));
            }
            return actions.ToArray();
        }

        // ---- 内部 ----

        // 此刻是否该占着扩展执行：切后台、有活会话、没被策略挂起、用户开着保活。
        private bool NeedsBackgroundKeep()
        {
            return _inBackground && _sessions > 0 && !_suspended && _settings.KeepAliveInBackground;
        }

        private BackgroundAction[] RequestExecutionIfNeeded()
        {
            var actions = new System.Collections.Generic.List<BackgroundAction>();
            if (!_executionActive && !_executionRequested)
            {
                _executionRequested = true;
                actions.Add(BackgroundAction.Simple(BackgroundActionKind.RequestExtendedExecution));
            }
            if (_settings.BackgroundDisconnectMinutes > 0 && !_timerArmed)
            {
                _timerArmed = true;
                actions.Add(BackgroundAction.ArmTimer(_settings.BackgroundDisconnectMinutes * 60));
            }
            return actions.ToArray();
        }

        // 优雅断开 + 记挂起（幂等：已挂起则无动作）。
        private BackgroundAction[] SuspendNow(string reason)
        {
            if (_suspended)
            {
                return NoActions();
            }
            _suspended = true;
            return new BackgroundAction[] { BackgroundAction.Disconnect(reason) };
        }

        // SuspendNow + 若计时器在跑则一并撤销（挂起后计时器已无意义，避免空转一轮）。
        private BackgroundAction[] SuspendNowWithTimerCancel(string reason)
        {
            var actions = new System.Collections.Generic.List<BackgroundAction>();
            BackgroundAction[] suspend = SuspendNow(reason);
            for (int i = 0; i < suspend.Length; i++)
            {
                actions.Add(suspend[i]);
            }
            if (_timerArmed)
            {
                _timerArmed = false;
                actions.Add(BackgroundAction.Simple(BackgroundActionKind.CancelBackgroundTimer));
            }
            return actions.ToArray();
        }

        private static BackgroundAction[] NoActions()
        {
            return new BackgroundAction[0];
        }

        private static BackgroundAction[] Single(BackgroundActionKind kind)
        {
            return new BackgroundAction[] { BackgroundAction.Simple(kind) };
        }
    }
}
