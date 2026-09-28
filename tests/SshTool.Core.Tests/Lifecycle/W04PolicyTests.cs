using SshTool.Core.Lifecycle;
using SshTool.Core.Sessions;
using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Lifecycle
{
    // W04：应用锁、断线通知与响铃节流的判定。
    public class AppLockPolicyTests
    {
        [Fact]
        public void ColdStart_LocksOnlyWhenEnabled()
        {
            Assert.True(new AppLockPolicy().OnColdStart(true));
            Assert.False(new AppLockPolicy().OnColdStart(false));
        }

        [Fact]
        public void ShortTripToBackground_DoesNotLock()
        {
            var policy = new AppLockPolicy();
            policy.OnColdStart(true);
            policy.OnUnlocked();
            policy.OnEnteredBackground(1000);
            Assert.False(policy.OnReturnedToForeground(true, 1000 + AppLockPolicy.GraceMs - 1));
        }

        [Fact]
        public void LongStayInBackground_Locks()
        {
            var policy = new AppLockPolicy();
            policy.OnEnteredBackground(0);
            Assert.True(policy.OnReturnedToForeground(true, AppLockPolicy.GraceMs));
            Assert.True(policy.IsLocked);
            policy.OnUnlocked();
            Assert.False(policy.IsLocked);
        }

        [Fact]
        public void Disabled_NeverLocks_AndClearsPendingLock()
        {
            var policy = new AppLockPolicy();
            policy.OnColdStart(true);
            policy.OnEnteredBackground(0);
            Assert.False(policy.OnReturnedToForeground(false, AppLockPolicy.GraceMs * 10));
            Assert.False(policy.IsLocked);
        }

        [Fact]
        public void StillLocked_StaysLockedAfterShortTrip()
        {
            var policy = new AppLockPolicy();
            policy.OnColdStart(true);
            policy.OnEnteredBackground(0);
            Assert.True(policy.OnReturnedToForeground(true, 10));
        }
    }

    public class DisconnectNotifyPolicyTests
    {
        private static bool Ask(DisconnectNotifyPolicy p, SessionUiState from, SessionUiState to,
            bool enabled = true, bool background = true, bool userClosed = false, bool suspended = false, long now = 0, string id = "s1")
        {
            return p.ShouldNotify(id, from, to, enabled, background, userClosed, suspended, now);
        }

        [Theory]
        [InlineData(SessionUiState.Reconnecting)]
        [InlineData(SessionUiState.Disconnected)]
        [InlineData(SessionUiState.Error)]
        public void ConnectedToDropped_InBackground_Notifies(SessionUiState to)
        {
            Assert.True(Ask(new DisconnectNotifyPolicy(), SessionUiState.Connected, to));
        }

        [Fact]
        public void Foreground_Disabled_UserClosed_PolicySuspended_DoNotNotify()
        {
            var p = new DisconnectNotifyPolicy();
            Assert.False(Ask(p, SessionUiState.Connected, SessionUiState.Error, background: false));
            Assert.False(Ask(p, SessionUiState.Connected, SessionUiState.Error, enabled: false));
            Assert.False(Ask(p, SessionUiState.Connected, SessionUiState.Disconnected, userClosed: true));
            Assert.False(Ask(p, SessionUiState.Connected, SessionUiState.Disconnected, suspended: true));
        }

        [Fact]
        public void NotFromConnected_OrNotDropped_DoesNotNotify()
        {
            var p = new DisconnectNotifyPolicy();
            Assert.False(Ask(p, SessionUiState.Reconnecting, SessionUiState.Error));
            Assert.False(Ask(p, SessionUiState.Connected, SessionUiState.Closed));
            Assert.False(Ask(p, SessionUiState.Connecting, SessionUiState.Connected));
        }

        [Fact]
        public void SameSession_DedupedWithinWindow_OthersIndependent()
        {
            var p = new DisconnectNotifyPolicy();
            Assert.True(Ask(p, SessionUiState.Connected, SessionUiState.Reconnecting, now: 0));
            Assert.False(Ask(p, SessionUiState.Connected, SessionUiState.Reconnecting, now: DisconnectNotifyPolicy.DedupeMs - 1));
            Assert.True(Ask(p, SessionUiState.Connected, SessionUiState.Reconnecting, now: 5, id: "s2"));
            Assert.True(Ask(p, SessionUiState.Connected, SessionUiState.Reconnecting, now: DisconnectNotifyPolicy.DedupeMs));
            p.Forget("s2");
            Assert.True(Ask(p, SessionUiState.Connected, SessionUiState.Error, now: 6, id: "s2"));
        }
    }

    public class BellThrottleTests
    {
        [Fact]
        public void FirstObservation_OnlySetsBaseline()
        {
            var t = new BellThrottle();
            Assert.False(t.ShouldRing(3, 0));
            Assert.True(t.ShouldRing(4, 10));
        }

        [Fact]
        public void BurstWithinWindow_RingsOnce()
        {
            var t = new BellThrottle();
            t.ShouldRing(0, 0);
            Assert.True(t.ShouldRing(1, 100));
            Assert.False(t.ShouldRing(5, 100 + BellThrottle.MergeWindowMs - 1));
            Assert.True(t.ShouldRing(6, 100 + BellThrottle.MergeWindowMs));
        }

        [Fact]
        public void NoChange_DoesNotRing_CounterReset_RebasesSilently()
        {
            var t = new BellThrottle();
            t.ShouldRing(10, 0);
            Assert.False(t.ShouldRing(10, 1000));
            Assert.False(t.ShouldRing(2, 2000)); // 屏幕重建：计数回退
            Assert.True(t.ShouldRing(3, 3000));
        }
    }
}
