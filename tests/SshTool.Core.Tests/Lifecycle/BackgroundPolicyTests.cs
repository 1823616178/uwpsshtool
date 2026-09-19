using SshTool.Core.Lifecycle;
using Xunit;

namespace SshTool.Core.Tests.Lifecycle
{
    // P01：BackgroundPolicy 纯状态机（对齐鸿蒙端 BackgroundKeepAlive.test.ets 按
    // W10M §10 裁剪后的分支：进入后台 / 授予 / 拒绝 / 撤销 / 回前台 / 计时到期 /
    // 会话数变化 / 设置变更 → 请求扩展执行 / 策略性断开 / 恢复重连 / 探测 / 释放）。
    public class BackgroundPolicyTests
    {
        private static BackgroundSettings On()
        {
            return new BackgroundSettings(true, 0);
        }

        private static BackgroundSettings OnWithMinutes(int minutes)
        {
            return new BackgroundSettings(true, minutes);
        }

        private static BackgroundSettings Off()
        {
            return new BackgroundSettings(false, 0);
        }

        private static string Kinds(BackgroundAction[] actions)
        {
            string[] names = new string[actions.Length];
            for (int i = 0; i < actions.Length; i++)
            {
                names[i] = actions[i].Kind.ToString();
            }
            return string.Join(",", names);
        }

        private static bool Has(BackgroundAction[] actions, BackgroundActionKind kind)
        {
            for (int i = 0; i < actions.Length; i++)
            {
                if (actions[i].Kind == kind)
                {
                    return true;
                }
            }
            return false;
        }

        private static BackgroundAction Find(BackgroundAction[] actions, BackgroundActionKind kind)
        {
            for (int i = 0; i < actions.Length; i++)
            {
                if (actions[i].Kind == kind)
                {
                    return actions[i];
                }
            }
            return null;
        }

        // 造一个「后台 + 有会话 + 扩展执行已生效」的策略机（多数用例的起点）。
        private static BackgroundPolicy BackgroundWithExecution(int minutes = 0)
        {
            var policy = new BackgroundPolicy(OnWithMinutes(minutes));
            policy.OnSessionCountChanged(2);
            policy.OnEnteredBackground();
            policy.OnExtendedExecutionGranted();
            return policy;
        }

        // ---- 前后台 ----

        [Fact]
        public void Background_WithSessions_RequestsExecution()
        {
            var policy = new BackgroundPolicy(On());
            policy.OnSessionCountChanged(1);
            BackgroundAction[] actions = policy.OnEnteredBackground();
            Assert.Equal("RequestExtendedExecution", Kinds(actions));
            Assert.True(policy.IsInBackground);
        }

        [Fact]
        public void Background_NoSessions_NoActions()
        {
            var policy = new BackgroundPolicy(On());
            Assert.Empty(policy.OnEnteredBackground());
        }

        [Fact]
        public void Background_KeepAliveOff_PolicyDisconnectDisabled()
        {
            var policy = new BackgroundPolicy(Off());
            policy.OnSessionCountChanged(1);
            BackgroundAction[] actions = policy.OnEnteredBackground();
            Assert.Equal("PolicyDisconnect", Kinds(actions));
            Assert.Equal(BackgroundPolicy.ReasonBackgroundDisabled, Find(actions, BackgroundActionKind.PolicyDisconnect).Reason);
            Assert.True(policy.IsSuspended);
        }

        [Fact]
        public void Background_Twice_Idempotent()
        {
            var policy = new BackgroundPolicy(On());
            policy.OnSessionCountChanged(1);
            policy.OnEnteredBackground();
            Assert.Empty(policy.OnEnteredBackground());
        }

        [Fact]
        public void Foreground_ReleasesExecutionAndProbes()
        {
            BackgroundPolicy policy = BackgroundWithExecution();
            BackgroundAction[] actions = policy.OnReturnedToForeground();
            Assert.Equal("ReleaseExtendedExecution,ProbeSessions", Kinds(actions));
            Assert.False(policy.IsInBackground);
        }

        [Fact]
        public void Foreground_Suspended_ResumesNotProbes()
        {
            var policy = new BackgroundPolicy(Off());
            policy.OnSessionCountChanged(1);
            policy.OnEnteredBackground();
            Assert.True(policy.IsSuspended);
            BackgroundAction[] actions = policy.OnReturnedToForeground();
            Assert.True(Has(actions, BackgroundActionKind.ResumeReconnect));
            Assert.False(Has(actions, BackgroundActionKind.ProbeSessions));
            Assert.False(policy.IsSuspended);
        }

        [Fact]
        public void Foreground_NoSessionsNoExecution_Empty()
        {
            var policy = new BackgroundPolicy(On());
            Assert.Empty(policy.OnReturnedToForeground());
        }

        // ---- 扩展执行授予/拒绝/撤销 ----

        [Fact]
        public void Granted_InBackground_NoActions()
        {
            var policy = new BackgroundPolicy(On());
            policy.OnSessionCountChanged(1);
            policy.OnEnteredBackground();
            Assert.Empty(policy.OnExtendedExecutionGranted());
            Assert.True(policy.IsExecutionActive);
        }

        [Fact]
        public void Granted_LateAfterForeground_ReleasesImmediately()
        {
            var policy = new BackgroundPolicy(On());
            policy.OnSessionCountChanged(1);
            policy.OnEnteredBackground();
            policy.OnReturnedToForeground(); // 系统还没批下来用户已回前台
            BackgroundAction[] actions = policy.OnExtendedExecutionGranted();
            Assert.Equal("ReleaseExtendedExecution", Kinds(actions));
        }

        [Fact]
        public void Denied_InBackground_PolicyDisconnectLost()
        {
            var policy = new BackgroundPolicy(On());
            policy.OnSessionCountChanged(1);
            policy.OnEnteredBackground();
            BackgroundAction[] actions = policy.OnExtendedExecutionDenied();
            Assert.Equal("PolicyDisconnect", Kinds(actions));
            Assert.Equal(BackgroundPolicy.ReasonExtensionLost, Find(actions, BackgroundActionKind.PolicyDisconnect).Reason);
            Assert.True(policy.IsSuspended);
        }

        [Fact]
        public void Denied_WhenKeepAliveOff_NoDuplicateDisconnect()
        {
            var policy = new BackgroundPolicy(Off());
            policy.OnSessionCountChanged(1);
            policy.OnEnteredBackground(); // 已因关闭保活而挂起
            Assert.Empty(policy.OnExtendedExecutionDenied());
        }

        [Fact]
        public void Revoked_InBackground_PolicyDisconnectLost()
        {
            BackgroundPolicy policy = BackgroundWithExecution();
            BackgroundAction[] actions = policy.OnExtendedExecutionRevoked();
            Assert.Equal("PolicyDisconnect", Kinds(actions));
            Assert.Equal(BackgroundPolicy.ReasonExtensionLost, Find(actions, BackgroundActionKind.PolicyDisconnect).Reason);
            Assert.True(policy.IsSuspended);
        }

        [Fact]
        public void Revoked_AfterForeground_NoActions()
        {
            BackgroundPolicy policy = BackgroundWithExecution();
            policy.OnReturnedToForeground();
            Assert.Empty(policy.OnExtendedExecutionRevoked());
        }

        [Fact]
        public void Suspended_DisconnectOnlyOnce()
        {
            BackgroundPolicy policy = BackgroundWithExecution();
            policy.OnExtendedExecutionRevoked();
            Assert.Empty(policy.OnExtendedExecutionDenied());
            Assert.Empty(policy.OnExtendedExecutionRevoked());
        }

        // ---- 会话数变化 ----

        [Fact]
        public void SessionCount_ZeroInBackground_ReleasesAndCancelsTimer()
        {
            BackgroundPolicy policy = BackgroundWithExecution(5);
            policy.OnEnteredBackground(); // 幂等：已申请不再重复（但补计时器不，此处已在构造时进入后台）
            BackgroundAction[] actions = policy.OnSessionCountChanged(0);
            Assert.Equal("ReleaseExtendedExecution,CancelBackgroundTimer", Kinds(actions));
            Assert.False(policy.IsTimerArmed);
        }

        [Fact]
        public void SessionCount_ZeroToOneInBackground_ReRequests()
        {
            var policy = new BackgroundPolicy(On());
            policy.OnEnteredBackground(); // 无会话：无动作
            BackgroundAction[] actions = policy.OnSessionCountChanged(1);
            Assert.Equal("RequestExtendedExecution", Kinds(actions));
        }

        [Fact]
        public void SessionCount_Same_NoActions()
        {
            BackgroundPolicy policy = BackgroundWithExecution();
            Assert.Empty(policy.OnSessionCountChanged(2));
        }

        [Fact]
        public void SessionCount_Foreground_NoActions()
        {
            var policy = new BackgroundPolicy(On());
            Assert.Empty(policy.OnSessionCountChanged(3));
        }

        [Fact]
        public void SessionCount_Negative_ClampedToZero()
        {
            var policy = new BackgroundPolicy(On());
            Assert.Empty(policy.OnSessionCountChanged(-1));
            Assert.Equal(0, policy.SessionCount);
        }

        // ---- 后台 N 分钟计时器 ----

        [Fact]
        public void Background_WithMinutes_ArmsTimer()
        {
            var policy = new BackgroundPolicy(OnWithMinutes(5));
            policy.OnSessionCountChanged(1);
            BackgroundAction[] actions = policy.OnEnteredBackground();
            Assert.Equal("RequestExtendedExecution,ArmBackgroundTimer", Kinds(actions));
            Assert.Equal(300, Find(actions, BackgroundActionKind.ArmBackgroundTimer).DelaySeconds);
            Assert.True(policy.IsTimerArmed);
        }

        [Fact]
        public void TimerExpiry_DisconnectsAndReleases()
        {
            BackgroundPolicy policy = BackgroundWithExecution(5);
            policy.OnEnteredBackground(); // 已在后台：幂等无动作
            BackgroundAction[] actions = policy.OnBackgroundTimerExpired();
            Assert.Equal("PolicyDisconnect,ReleaseExtendedExecution", Kinds(actions));
            Assert.Equal(BackgroundPolicy.ReasonBackgroundTimeout, Find(actions, BackgroundActionKind.PolicyDisconnect).Reason);
            Assert.True(policy.IsSuspended);
            Assert.False(policy.IsTimerArmed);
        }

        [Fact]
        public void TimerExpiry_NotArmed_NoActions()
        {
            BackgroundPolicy policy = BackgroundWithExecution();
            Assert.Empty(policy.OnBackgroundTimerExpired());
        }

        [Fact]
        public void TimerExpiry_AfterForegroundCancel_NoActions()
        {
            BackgroundPolicy policy = BackgroundWithExecution(5);
            policy.OnEnteredBackground();
            policy.OnReturnedToForeground(); // 撤计时器
            Assert.Empty(policy.OnBackgroundTimerExpired());
        }

        // ---- 设置变更 ----

        [Fact]
        public void Settings_KeepAliveOffInBackground_Disconnects()
        {
            BackgroundPolicy policy = BackgroundWithExecution();
            BackgroundAction[] actions = policy.OnSettingsChanged(Off());
            Assert.True(Has(actions, BackgroundActionKind.ReleaseExtendedExecution));
            Assert.True(Has(actions, BackgroundActionKind.PolicyDisconnect));
            Assert.Equal(BackgroundPolicy.ReasonBackgroundDisabled, Find(actions, BackgroundActionKind.PolicyDisconnect).Reason);
            Assert.True(policy.IsSuspended);
        }

        [Fact]
        public void Settings_KeepAliveOnInForeground_NoActions()
        {
            var policy = new BackgroundPolicy(On());
            policy.OnSessionCountChanged(1);
            Assert.Empty(policy.OnSettingsChanged(On()));
        }

        [Fact]
        public void Settings_MinutesToZero_CancelsTimer()
        {
            BackgroundPolicy policy = BackgroundWithExecution(5);
            policy.OnEnteredBackground();
            BackgroundAction[] actions = policy.OnSettingsChanged(On());
            Assert.Equal("CancelBackgroundTimer", Kinds(actions));
            Assert.False(policy.IsTimerArmed);
        }

        [Fact]
        public void Settings_MinutesChange_RearmsWithNewDelay()
        {
            BackgroundPolicy policy = BackgroundWithExecution(3);
            policy.OnEnteredBackground();
            BackgroundAction[] actions = policy.OnSettingsChanged(OnWithMinutes(10));
            Assert.Equal("CancelBackgroundTimer,ArmBackgroundTimer", Kinds(actions));
            Assert.Equal(600, Find(actions, BackgroundActionKind.ArmBackgroundTimer).DelaySeconds);
        }

        [Fact]
        public void Settings_Null_Throws()
        {
            var policy = new BackgroundPolicy(On());
            Assert.Throws<System.ArgumentNullException>(() => policy.OnSettingsChanged(null));
        }

        // ---- 原因文案 ----
        [Fact]
        public void Reasons_AreDistinctAndPointToReconnect()
        {
            Assert.NotEqual(BackgroundPolicy.ReasonBackgroundDisabled, BackgroundPolicy.ReasonExtensionLost);
            Assert.NotEqual(BackgroundPolicy.ReasonExtensionLost, BackgroundPolicy.ReasonBackgroundTimeout);
            Assert.Contains("自动重连", BackgroundPolicy.ReasonBackgroundDisabled);
            Assert.Contains("自动重连", BackgroundPolicy.ReasonExtensionLost);
            Assert.Contains("自动重连", BackgroundPolicy.ReasonBackgroundTimeout);
        }
    }
}
