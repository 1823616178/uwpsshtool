using SshTool.Core.Lifecycle;
using Xunit;

namespace SshTool.Core.Tests.Lifecycle
{
    // P02：NetworkChangeDetector 判据（对齐鸿蒙端 NetworkWatcher 的 isNetworkSwitch
    // 思想，按任务要点「网络适配器 id + 连接级别」裁剪；防抖在 App 层 NetworkMonitor）。
    public class NetworkChangeDetectorTests
    {
        private static NetworkSnapshot Net(string adapter, int level)
        {
            return new NetworkSnapshot(adapter, level);
        }

        // 同网抖动不触发：同一适配器 + 同一级别重复上报（含多次）→ false，且 Current 跟进。
        [Fact]
        public void SameNetwork_Jitter_NoTrigger()
        {
            var detector = new NetworkChangeDetector();
            detector.Reset(Net("adapter-1", 3));

            Assert.False(detector.OnNetworkEvent(Net("adapter-1", 3)));
            Assert.False(detector.OnNetworkEvent(Net("adapter-1", 3)));
            Assert.Equal("adapter-1", detector.Current.AdapterId);
            Assert.Equal(3, detector.Current.ConnectivityLevel);
            Assert.False(NetworkChangeDetector.IsSwitch(Net("adapter-1", 3), Net("adapter-1", 3)));
        }

        // 切网触发：适配器 id 变了（Wi-Fi ⇄ 蜂窝/有线）。
        [Fact]
        public void AdapterChange_Triggers()
        {
            var detector = new NetworkChangeDetector();
            detector.Reset(Net("wifi-1", 3));

            Assert.True(detector.OnNetworkEvent(Net("cell-9", 3)));
            Assert.Equal("cell-9", detector.Current.AdapterId);
            Assert.True(NetworkChangeDetector.IsSwitch(Net("wifi-1", 3), Net("cell-9", 3)));
        }

        // 同一适配器上连接级别变化也触发（如 captive portal 降级）。
        [Fact]
        public void LevelChange_Triggers()
        {
            Assert.True(NetworkChangeDetector.IsSwitch(Net("wifi-1", 3), Net("wifi-1", 1)));
            Assert.True(NetworkChangeDetector.IsSwitch(Net("wifi-1", 0), Net("wifi-1", 3)));

            var detector = new NetworkChangeDetector();
            detector.Reset(Net("wifi-1", 3));
            Assert.True(detector.OnNetworkEvent(Net("wifi-1", 1)));
        }

        // 断网→恢复触发：OnNetworkLost 只记录不通知；恢复后的首个有效快照触发。
        [Fact]
        public void DisconnectThenRecover_Triggers()
        {
            var detector = new NetworkChangeDetector();
            detector.Reset(Net("wifi-1", 3));

            detector.OnNetworkLost();
            Assert.False(detector.Current.IsValid);

            Assert.True(detector.OnNetworkEvent(Net("wifi-1", 3)));
            Assert.Equal("wifi-1", detector.Current.AdapterId);
        }

        // 断网本身不触发（退避中的会话此时重连必然失败，不消耗重试预算）。
        [Fact]
        public void Lost_Alone_DoesNotTrigger()
        {
            var detector = new NetworkChangeDetector();
            detector.Reset(Net("wifi-1", 3));
            detector.OnNetworkLost();

            // 无效快照上报直接忽略，且不覆写 Current（仍是 Unknown，恢复可触发）。
            Assert.False(detector.OnNetworkEvent(NetworkSnapshot.Unknown));
            Assert.False(detector.Current.IsValid);
            Assert.False(detector.OnNetworkEvent(null));
            Assert.False(NetworkChangeDetector.IsSwitch(Net("wifi-1", 3), NetworkSnapshot.Unknown));
            Assert.False(NetworkChangeDetector.IsSwitch(Net("wifi-1", 3), null));
        }

        // 首次观测到默认网触发（启动时读不到基线、之后第一次有网）。
        [Fact]
        public void FirstObservation_Triggers()
        {
            var detector = new NetworkChangeDetector();
            Assert.False(detector.Current.IsValid);
            Assert.True(detector.OnNetworkEvent(Net("wifi-1", 3)));
        }

        // 启动基线正确时，同一网络的首个事件不触发（避免启动白踢全部会话）。
        [Fact]
        public void ResetBaseline_SameNetwork_NoTrigger()
        {
            var detector = new NetworkChangeDetector();
            detector.Reset(Net("wifi-1", 3));
            Assert.False(detector.OnNetworkEvent(Net("wifi-1", 3)));
        }

        [Fact]
        public void Reset_Null_FallsBackToUnknown()
        {
            var detector = new NetworkChangeDetector();
            detector.Reset(Net("wifi-1", 3));
            detector.Reset(null);
            Assert.False(detector.Current.IsValid);
            Assert.True(detector.OnNetworkEvent(Net("wifi-1", 3)));
        }

        [Fact]
        public void DebounceWindow_IsOneSecond()
        {
            Assert.Equal(1000, NetworkChangeDetector.DebounceMilliseconds);
        }
    }
}
