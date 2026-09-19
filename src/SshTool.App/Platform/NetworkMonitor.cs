using System;
using SshTool.Core.Common;
using SshTool.Core.Lifecycle;
using Windows.Networking.Connectivity;
using Windows.System.Threading;

namespace SshTool.App.Platform
{
    // P02：网络变化监听（App 层外壳）。
    //
    // 分工：判据全在 Core.NetworkChangeDetector（纯逻辑、可单测），本类只做三件事——
    // 订阅 NetworkInformation.NetworkStatusChanged、1 s 后沿防抖、读最新快照喂判据
    //（对齐鸿蒙端 NetworkWatcher 收拢单例广播的意图；W10M 上会话层与将来的同步层
    // S14 共用本事件，不必各自订阅系统回调）。
    //
    // 流程：系统事件（任意线程）→ 重起 1 s ThreadPoolTimer（后沿防抖：抖动期间
    // 反复重起，只取静默后的最后一次）→ 读 GetInternetConnectionProfile →
    // 有效快照喂 Detector.OnNetworkEvent（切网即触发 Changed 事件）/
    // 无效快照记 Detector.OnNetworkLost（断网本身不踢会话，恢复时再收敛）。
    //
    // Changed 事件的订阅方：LifecycleService（调 SessionManager.OnNetworkChanged
    // 收敛会话）、S14 同步触发器（网络恢复补一次同步）。事件在线程池触发，
    // 订阅方自行决定是否封送（SessionManager 内部已封送，无需 UI 线程调用）。
    //
    // 日志脱敏：只记「变化」等状态，不记适配器 id、SSID、信号等任何网络标识。
    //
    // 本文件所有 UWP API（NetworkInformation、ThreadPoolTimer）自 10240 起可用，
    // 无需 ApiInformation 守卫（min 15063 纪律）；读快照的每一步仍 try/catch，
    // 异常只记日志，绝不阻断调用方或崩溃。
    public sealed class NetworkMonitor
    {
        private readonly ILogger _logger;
        private readonly object _sync = new object();
        private readonly NetworkChangeDetector _detector = new NetworkChangeDetector();

        private ThreadPoolTimer _debounceTimer;
        private bool _started;

        public NetworkMonitor(ILogger logger)
        {
            _logger = logger;
        }

        // 切网（防抖 + 判据后确认）：会话收敛（LifecycleService）与同步补传（S14）订阅。
        public event EventHandler NetworkChanged;

        // 只读观测（诊断用）。
        public NetworkSnapshot CurrentSnapshot
        {
            get { lock (_sync) { return _detector.Current; } }
        }

        // App 启动完成后调用（AppServices.StartAsync 内）；幂等，绝不阻断启动。
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
                    // 启动基线：把既有网络记为 Current，避免启动瞬间白白踢全部会话。
                    _detector.Reset(ReadSnapshot());
                }
                NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
                _logger?.Log(LogLevel.Info, "Network", "started");
            }
            catch (Exception ex)
            {
                _logger?.Log(LogLevel.Warning, "Network", "start failed " + ex.GetType().Name);
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
                NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
            }
            catch (Exception)
            {
            }
            lock (_sync)
            {
                CancelTimerLocked();
            }
        }

        private void OnNetworkStatusChanged(object sender)
        {
            lock (_sync)
            {
                if (!_started)
                {
                    return;
                }
                CancelTimerLocked();
                try
                {
                    _debounceTimer = ThreadPoolTimer.CreateTimer(
                        OnDebounceFired,
                        TimeSpan.FromMilliseconds(NetworkChangeDetector.DebounceMilliseconds));
                }
                catch (Exception ex)
                {
                    _logger?.Log(LogLevel.Warning, "Network", "debounce arm failed " + ex.GetType().Name);
                }
            }
        }

        private void OnDebounceFired(ThreadPoolTimer timer)
        {
            bool changed;
            lock (_sync)
            {
                if (!_started || !ReferenceEquals(timer, _debounceTimer))
                {
                    return;
                }
                _debounceTimer = null;
                NetworkSnapshot snapshot;
                try
                {
                    snapshot = ReadSnapshot();
                }
                catch (Exception ex)
                {
                    _logger?.Log(LogLevel.Warning, "Network", "read snapshot failed " + ex.GetType().Name);
                    return;
                }
                if (!snapshot.IsValid)
                {
                    _detector.OnNetworkLost();
                    return;
                }
                changed = _detector.OnNetworkEvent(snapshot);
            }
            if (!changed)
            {
                return;
            }
            _logger?.Log(LogLevel.Info, "Network", "changed");
            EventHandler handler = NetworkChanged;
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
                _logger?.Log(LogLevel.Warning, "Network", "listener failed " + ex.GetType().Name);
            }
        }

        // 调用方已持有 _sync。
        private void CancelTimerLocked()
        {
            ThreadPoolTimer timer = _debounceTimer;
            _debounceTimer = null;
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

        // 当前默认网快照：拿不到（断网/无适配器）即 Unknown。只在锁内或启动时调用。
        // 注意：本方法不抛异常——每一步单独保护，SSID 等标识一律不读不记。
        private NetworkSnapshot ReadSnapshot()
        {
            ConnectionProfile profile = null;
            try
            {
                profile = NetworkInformation.GetInternetConnectionProfile();
            }
            catch (Exception)
            {
                return NetworkSnapshot.Unknown;
            }
            if (profile == null)
            {
                return NetworkSnapshot.Unknown;
            }
            string adapterId = null;
            try
            {
                NetworkAdapter adapter = profile.NetworkAdapter;
                if (adapter != null)
                {
                    adapterId = adapter.NetworkAdapterId.ToString();
                }
            }
            catch (Exception)
            {
                adapterId = null;
            }
            if (string.IsNullOrEmpty(adapterId))
            {
                return NetworkSnapshot.Unknown;
            }
            int level = 0;
            try
            {
                level = (int)profile.GetNetworkConnectivityLevel();
            }
            catch (Exception)
            {
                level = 0;
            }
            return new NetworkSnapshot(adapterId, level);
        }
    }
}
