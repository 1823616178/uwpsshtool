using System;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Auth;
using SshTool.Core.Sync.Vault;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // U17：SyncStatePresenter 单测 —— 覆盖全部 Screen 判定组合（Authenticated ×
    // VaultStatus × Phase 关键组合）+ 相对时间阈值 + 文案键非空。
    public class SyncStatePresenterTests
    {
        private static AuthState Authenticated()
        {
            return new AuthState
            {
                Authenticated = true,
                UserId = "u1",
                UserEmail = "a@b.com",
                DeviceId = "d1",
                DeviceName = "Lumia 950"
            };
        }

        private static SyncState State(VaultStatus vault, SyncPhase phase)
        {
            return new SyncState { Vault = vault, Phase = phase };
        }

        // ---- DetermineScreen：Authenticated × VaultStatus ----

        [Fact]
        public void DetermineScreen_NullAuth_ReturnsLogin()
        {
            var p = new SyncStatePresenter();
            Assert.Equal(SyncScreenKind.Login, p.DetermineScreen(null, State(VaultStatus.Ready, SyncPhase.Idle)));
        }

        [Fact]
        public void DetermineScreen_Unauthenticated_ReturnsLogin()
        {
            var p = new SyncStatePresenter();
            Assert.Equal(SyncScreenKind.Login, p.DetermineScreen(AuthState.Unauthenticated, State(VaultStatus.Ready, SyncPhase.Idle)));
        }

        [Fact]
        public void DetermineScreen_Authenticated_Missing_ReturnsCreateVault()
        {
            var p = new SyncStatePresenter();
            Assert.Equal(SyncScreenKind.CreateVault, p.DetermineScreen(Authenticated(), State(VaultStatus.Missing, SyncPhase.Disabled)));
        }

        [Fact]
        public void DetermineScreen_Authenticated_Locked_ReturnsUnlockVault()
        {
            var p = new SyncStatePresenter();
            Assert.Equal(SyncScreenKind.UnlockVault, p.DetermineScreen(Authenticated(), State(VaultStatus.Locked, SyncPhase.Locked)));
        }

        [Fact]
        public void DetermineScreen_Authenticated_Ready_ReturnsStatus()
        {
            var p = new SyncStatePresenter();
            Assert.Equal(SyncScreenKind.Status, p.DetermineScreen(Authenticated(), State(VaultStatus.Ready, SyncPhase.Idle)));
        }

        [Fact]
        public void DetermineScreen_Authenticated_Ready_Syncing_ReturnsStatus()
        {
            var p = new SyncStatePresenter();
            Assert.Equal(SyncScreenKind.Status, p.DetermineScreen(Authenticated(), State(VaultStatus.Ready, SyncPhase.Syncing)));
        }

        [Fact]
        public void DetermineScreen_Authenticated_Ready_Conflict_ReturnsStatus()
        {
            var p = new SyncStatePresenter();
            Assert.Equal(SyncScreenKind.Status, p.DetermineScreen(Authenticated(), State(VaultStatus.Ready, SyncPhase.Conflict)));
        }

        [Fact]
        public void DetermineScreen_NullState_MissingVault_ReturnsCreateVault()
        {
            var p = new SyncStatePresenter();
            Assert.Equal(SyncScreenKind.CreateVault, p.DetermineScreen(Authenticated(), null));
        }

        // ---- StatusCardText：全相位文案键非空 + 图标非空 ----

        private static StatusCardSpec Spec(SyncPhase phase)
        {
            return new SyncStatePresenter().StatusCardText(new SyncState { Phase = phase });
        }

        [Fact]
        public void StatusCardText_AllPhases_HaveNonEmptyTextAndIcon()
        {
            foreach (SyncPhase phase in Enum.GetValues(typeof(SyncPhase)))
            {
                var spec = Spec(phase);
                Assert.False(string.IsNullOrEmpty(spec.TextKey), "TextKey 为空: " + phase);
                Assert.False(string.IsNullOrEmpty(spec.IconKey), "IconKey 为空: " + phase);
            }
        }

        [Fact]
        public void StatusCardText_Syncing_SpinIsTrue()
        {
            var spec = Spec(SyncPhase.Syncing);
            Assert.True(spec.Spin);
        }

        [Fact]
        public void StatusCardText_NonSyncing_SpinIsFalse()
        {
            Assert.False(Spec(SyncPhase.Idle).Spin);
            Assert.False(Spec(SyncPhase.Synced).Spin);
            Assert.False(Spec(SyncPhase.Error).Spin);
            Assert.False(Spec(SyncPhase.Offline).Spin);
        }

        [Fact]
        public void StatusCardText_NullState_FallsBackToSignedOut()
        {
            var spec = new SyncStatePresenter().StatusCardText(null);
            Assert.Equal("Sync_SignedOut", spec.TextKey);
        }

        // ---- FormatRelativeTime：阈值覆盖 ----

        private readonly DateTimeOffset _now = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        // 偏移为 0 的「本地」时区：与旧用例的 UTC 日判定一致。
        private RelativeTime Rel(string iso)
        {
            return new SyncStatePresenter(() => _now, d => d.ToOffset(TimeSpan.Zero)).ComputeRelativeTime(iso);
        }

        private string Relative(string iso)
        {
            RelativeTime r = Rel(iso);
            switch (r.Kind)
            {
                case RelativeTimeKind.None: return string.Empty;
                case RelativeTimeKind.JustNow: return "now";
                case RelativeTimeKind.Minutes: return r.Value + "m";
                case RelativeTimeKind.Hours: return r.Value + "h";
                case RelativeTimeKind.Yesterday: return "yesterday";
                case RelativeTimeKind.DayBeforeYesterday: return "day-before";
                case RelativeTimeKind.Days: return r.Value + "d";
                default: return r.Local.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        [Fact]
        public void FormatRelativeTime_EmptyString_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, Relative(""));
        }

        [Fact]
        public void FormatRelativeTime_Null_ReturnsEmpty()
        {
            Assert.Equal(string.Empty, Relative(null));
        }

        [Fact]
        public void FormatRelativeTime_JustNow()
        {
            Assert.Equal("now", Relative("2026-01-15T12:00:00.000Z"));
            Assert.Equal("now", Relative("2026-01-15T11:59:30.000Z"));
        }

        [Fact]
        public void FormatRelativeTime_MinutesAgo()
        {
            Assert.Equal("5m", Relative("2026-01-15T11:55:00.000Z"));
            // 61 秒前 → 超过「刚刚」阈值（60 s），进分钟档。
            Assert.Equal("1m", Relative("2026-01-15T11:58:59.000Z"));
        }

        [Fact]
        public void FormatRelativeTime_HoursAgo()
        {
            Assert.Equal("3h", Relative("2026-01-15T09:00:00.000Z"));
            // 61 分钟前 → 超过小时阈值（3600 s），进小时档。
            Assert.Equal("1h", Relative("2026-01-15T10:58:59.000Z"));
        }

        [Fact]
        public void FormatRelativeTime_Yesterday()
        {
            Assert.Equal("yesterday", Relative("2026-01-14T10:00:00.000Z"));
        }

        [Fact]
        public void FormatRelativeTime_DayBeforeYesterday()
        {
            Assert.Equal("day-before", Relative("2026-01-13T10:00:00.000Z"));
        }

        [Fact]
        public void FormatRelativeTime_SeveralDaysAgo()
        {
            Assert.Equal("5d", Relative("2026-01-10T10:00:00.000Z"));
        }

        [Fact]
        public void FormatRelativeTime_OldDate_ReturnsShortDate()
        {
            Assert.Equal("2025-12-01", Relative("2025-12-01T10:00:00.000Z"));
        }

        [Fact]
        public void FormatRelativeTime_Future_ClampedToJustNow()
        {
            Assert.Equal("now", Relative("2026-01-15T14:00:00.000Z"));
        }

        // fix/functional-pass：按本地日历日判定。东八区：now = 01-15 07:00 本地（UTC 01-14 23:00），
        // 同步于 UTC 01-13 17:00 = 本地 01-14 01:00 → 本地是「昨天」（UTC 日判定会给出「前天」）。
        [Fact]
        public void FormatRelativeTime_UsesLocalCalendarDay()
        {
            var now = new DateTimeOffset(2026, 1, 14, 23, 0, 0, TimeSpan.Zero);
            var p = new SyncStatePresenter(() => now, d => d.ToOffset(TimeSpan.FromHours(8)));
            RelativeTime r = p.ComputeRelativeTime("2026-01-13T17:00:00.000Z");
            Assert.Equal(RelativeTimeKind.Yesterday, r.Kind);
            RelativeTime old = p.ComputeRelativeTime("2025-12-31T20:00:00.000Z");
            Assert.Equal(RelativeTimeKind.Date, old.Kind);
            Assert.Equal(new DateTime(2026, 1, 1), old.Local.Date);
        }
    }
}
