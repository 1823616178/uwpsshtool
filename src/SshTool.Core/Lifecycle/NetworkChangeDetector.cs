using System;

namespace SshTool.Core.Lifecycle
{
    // P02：默认网快照（轻量指纹，零 UWP 引用，netstandard1.4 可单测）。
    //
    // AdapterId：UWP NetworkAdapter.NetworkAdapterId.ToString()；null/空 = 无默认网
    //   （断网、或读不到适配器），即 Unknown。
    // ConnectivityLevel：(int)NetworkConnectivityLevel（None=0、LocalAccess=1、
    //   ConstrainedInternetAccess=2、InternetAccess=3），只做判据比较，不解释语义。
    public sealed class NetworkSnapshot
    {
        public static readonly NetworkSnapshot Unknown = new NetworkSnapshot(null, 0);

        public NetworkSnapshot(string adapterId, int connectivityLevel)
        {
            AdapterId = adapterId;
            ConnectivityLevel = connectivityLevel;
        }

        public string AdapterId { get; private set; }

        public int ConnectivityLevel { get; private set; }

        public bool IsValid
        {
            get { return !string.IsNullOrEmpty(AdapterId); }
        }
    }

    // P02：切网判据（纯逻辑，对齐鸿蒙端 NetworkWatcher.isNetworkSwitch 按
    // UWP 现实裁剪：判据是「网络适配器 id + 连接级别」，见 01-DESIGN.md §10）。
    //
    // 规则（IsSwitch）：
    //   - next 无效（null/Unknown）→ false：无效上报直接忽略，不惊动会话；
    //   - previous 无效 → true：首次观测到默认网 / 断网后恢复；
    //   - 适配器 id 变了 → true：Wi-Fi ⇄ 蜂窝/有线等切网；
    //   - 同一适配器上连接级别变了 → true（如 captive portal 导致降级）；
    //   - 否则 → false：同一张网的抖动（信号/计费属性变化会反复触发系统事件，
    //     绝不能每次都踢会话）。
    //
    // 断网本身（OnNetworkLost）只记为 Unknown，不通知：退避中的会话此时立即重连
    // 必然失败、白白消耗重试预算；在网会话靠 keepalive/ProbeNow 链路收敛即可，
    // 恢复事件到来时再统一收敛（验收：断网→恢复触发）。
    //
    // 防抖不在此处：NetworkStatusChanged 的 1 s 后沿防抖由 App 层 NetworkMonitor
    // 的计时器做（见 DebounceMilliseconds），此处只保证「有效变化才上报」。
    // 线程安全：调用方（NetworkMonitor）保证串行调用。
    public sealed class NetworkChangeDetector
    {
        // NetworkStatusChanged 防抖窗口（毫秒）：事件到来后等网络静默这么久，
        // 再读最新快照做判据。由 NetworkMonitor 的 ThreadPoolTimer 执行。
        public const int DebounceMilliseconds = 1000;

        private NetworkSnapshot _current = NetworkSnapshot.Unknown;

        public NetworkSnapshot Current
        {
            get { return _current; }
        }

        // 启动基线（Monitor.Start 时读一次当前快照）：避免启动瞬间把既有网络
        // 当成一次「切换」白白踢全部会话。
        public void Reset(NetworkSnapshot initial)
        {
            _current = initial == null ? NetworkSnapshot.Unknown : initial;
        }

        // 有效快照上报：返回是否算一次切网（调用方据此决定是否收敛会话）。
        // 无效快照直接忽略且不改 Current（真正的断网走 OnNetworkLost）。
        // 无论结果如何，有效快照都会更新 Current（同网抖动只更新不惊动）。
        public bool OnNetworkEvent(NetworkSnapshot next)
        {
            if (next == null || !next.IsValid)
            {
                return false;
            }
            bool changed = IsSwitch(_current, next);
            _current = next;
            return changed;
        }

        // 断网：记为 Unknown，不返回通知（恢复时 OnNetworkEvent 会报 true）。
        public void OnNetworkLost()
        {
            _current = NetworkSnapshot.Unknown;
        }

        public static bool IsSwitch(NetworkSnapshot previous, NetworkSnapshot next)
        {
            if (next == null || !next.IsValid)
            {
                return false;
            }
            if (previous == null || !previous.IsValid)
            {
                return true;
            }
            if (!string.Equals(previous.AdapterId, next.AdapterId, StringComparison.Ordinal))
            {
                return true;
            }
            return previous.ConnectivityLevel != next.ConnectivityLevel;
        }
    }
}
