using System;
using System.Globalization;
using System.Threading.Tasks;
using SshTool.Core.Models;
using SshTool.Core.Sessions;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Vault;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // S14 验收：Sync 来源不标脏、片段/外观不标脏、前台阈值、轮询启停。
    // FakeTarget 代替 SyncCoordinator（ISyncTriggerTarget 窄接口），仓库与凭据用真实实现，
    // 定时用 ManualTimerFactory，时钟手动推进。
    public class SyncTriggersTests
    {
        private sealed class FakeTarget : SshTool.Core.Sync.ISyncTriggerTarget
        {
            public SyncState State = NewState(SyncPhase.Idle, true, true, null);

            public int MarkDirtyCalls;

            public int SyncNowCalls;

            public Func<Task> OnSyncNow;

            public SyncState CurrentState
            {
                get { return State.Clone(); }
            }

            public Task MarkDirtyAsync()
            {
                MarkDirtyCalls++;
                return Task.CompletedTask;
            }

            public Task SyncNowAsync()
            {
                SyncNowCalls++;
                Func<Task> hook = OnSyncNow;
                return hook != null ? hook() : Task.CompletedTask;
            }
        }

        private sealed class Fixture : IDisposable
        {
            public readonly FakeTarget Target = new FakeTarget();
            public readonly InMemoryFileSystem Fs = new InMemoryFileSystem();
            public readonly HostRepository Hosts;
            public readonly GroupRepository Groups;
            public readonly TunnelRepository Tunnels;
            public readonly SnippetRepository Snippets;
            public readonly AppearanceRepository Appearances;
            public readonly InMemorySecretStore Secrets = new InMemorySecretStore();
            public readonly SettingsRepository Settings;
            public readonly ManualTimerFactory Timers = new ManualTimerFactory();
            public DateTimeOffset Now = new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

            public readonly SshTool.Core.Sync.SyncTriggers Triggers;

            public Fixture()
            {
                Hosts = new HostRepository(Fs);
                Groups = new GroupRepository(Fs);
                Tunnels = new TunnelRepository(Fs);
                Snippets = new SnippetRepository(Fs);
                Appearances = new AppearanceRepository(Fs);
                Settings = new SettingsRepository(new InMemorySettingsStore());
                Triggers = new SshTool.Core.Sync.SyncTriggers(
                    Target, Hosts, Groups, Tunnels, Secrets, Settings, Timers, () => Now);
                Triggers.Start();
            }

            public void Dispose()
            {
                Triggers.Dispose();
            }

            public string Iso(DateTimeOffset value)
            {
                return value.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            }

            public void SetState(SyncPhase phase, bool enabled, bool autoSync, string lastSyncedAt)
            {
                Target.State = NewState(phase, enabled, autoSync, lastSyncedAt);
            }
        }

        private static SyncState NewState(SyncPhase phase, bool enabled, bool autoSync, string lastSyncedAt)
        {
            return new SyncState
            {
                Phase = phase,
                Vault = VaultStatus.Ready,
                Preferences = new SyncPreferences
                {
                    Enabled = enabled,
                    AutoSync = autoSync,
                    SyncPasswords = false,
                    SyncPrivateKeys = false
                },
                Revision = "1",
                KeyVersion = 1,
                Dirty = false,
                LastSyncedAt = lastSyncedAt,
                Message = ""
            };
        }

        private static Host NewHost(string id)
        {
            return new Host
            {
                Id = id,
                Name = id,
                HostName = "example.com",
                Port = 22,
                Username = "root",
                AuthType = AuthType.Password,
                HostFingerprint = "",
                Keepalive = 30
            };
        }

        private static HostGroup NewGroup(string id)
        {
            return new HostGroup { Id = id, Name = id, Color = "#4F8CFF", Order = 0, Collapsed = false };
        }

        private static Tunnel NewTunnel(string id)
        {
            return new Tunnel
            {
                Id = id,
                Name = id,
                ServerId = "h1",
                GroupId = null,
                Type = TunnelType.Local,
                ListenHost = "127.0.0.1",
                ListenPort = 8080,
                DestHost = "127.0.0.1",
                DestPort = 80,
                DestServerId = "",
                AutoReconnect = true,
                Enabled = true,
                AutoStart = false
            };
        }

        private static async Task WaitForAsync(Func<int> get, int expected)
        {
            for (int i = 0; i < 100; i++)
            {
                if (get() >= expected)
                {
                    return;
                }
                await Task.Delay(20);
            }
            Assert.True(get() >= expected, "等待触发超时，实际=" + get() + " 期望>=" + expected);
        }

        // ---- 仓库 Changed 接线 ----

        [Fact]
        public async Task HostUserChange_MarksDirty()
        {
            using (var f = new Fixture())
            {
                await f.Hosts.AddAsync(NewHost("h1"));
                await WaitForAsync(() => f.Target.MarkDirtyCalls, 1);
            }
        }

        [Fact]
        public async Task GroupUserChange_MarksDirty()
        {
            using (var f = new Fixture())
            {
                await f.Groups.AddAsync(NewGroup("g1"));
                await WaitForAsync(() => f.Target.MarkDirtyCalls, 1);
            }
        }

        [Fact]
        public async Task TunnelUserChange_MarksDirty()
        {
            using (var f = new Fixture())
            {
                await f.Tunnels.AddAsync(NewTunnel("t1"));
                await WaitForAsync(() => f.Target.MarkDirtyCalls, 1);
            }
        }

        [Fact]
        public async Task RepoSyncOrigin_DoesNotMarkDirty()
        {
            using (var f = new Fixture())
            {
                await f.Hosts.AddAsync(NewHost("h1"), ChangeOrigin.Sync);
                await f.Groups.AddAsync(NewGroup("g1"), ChangeOrigin.Sync);
                await f.Tunnels.AddAsync(NewTunnel("t1"), ChangeOrigin.Sync);
                await Task.Delay(150);
                Assert.Equal(0, f.Target.MarkDirtyCalls);
            }
        }

        [Fact]
        public async Task SnippetAndAppearanceUserChange_DoNotMarkDirty()
        {
            using (var f = new Fixture())
            {
                await f.Snippets.AddAsync(new Snippet { Id = "s1", Name = "s1" });
                await f.Appearances.AddAsync(new AppearanceProfile { Id = "a1", Name = "a1" });
                await Task.Delay(150);
                Assert.Equal(0, f.Target.MarkDirtyCalls);
            }
        }

        [Fact]
        public async Task RepoChange_WhenDisabled_DoesNotMarkDirty()
        {
            using (var f = new Fixture())
            {
                f.SetState(SyncPhase.Disabled, false, true, null);
                await f.Hosts.AddAsync(NewHost("h1"));
                await Task.Delay(150);
                Assert.Equal(0, f.Target.MarkDirtyCalls);
            }
        }

        // ---- 凭据 Changed 接线 ----

        [Fact]
        public async Task CredentialUserChange_MarksDirty()
        {
            using (var f = new Fixture())
            {
                await f.Secrets.SetAsync(SecretKeys.HostPassword("h1"), "s3cret");
                await WaitForAsync(() => f.Target.MarkDirtyCalls, 1);
            }
        }

        [Fact]
        public async Task CredentialSyncOrigin_DoesNotMarkDirty()
        {
            using (var f = new Fixture())
            {
                await f.Secrets.SetAsync(SecretKeys.HostPassword("h1"), "s3cret", ChangeOrigin.Sync);
                await f.Secrets.RemoveByPrefixAsync(SecretKeys.HostPrefix("h1"), ChangeOrigin.Sync);
                await Task.Delay(150);
                Assert.Equal(0, f.Target.MarkDirtyCalls);
            }
        }

        [Fact]
        public async Task NonCredentialSecretKey_DoesNotMarkDirty()
        {
            using (var f = new Fixture())
            {
                await f.Secrets.SetAsync("misc:note", "x");
                await Task.Delay(150);
                Assert.Equal(0, f.Target.MarkDirtyCalls);
            }
        }

        // ---- 启动 / 前台 / 网络 ----

        [Fact]
        public void Startup_WhenEnabledAndIdle_Syncs()
        {
            using (var f = new Fixture())
            {
                f.Triggers.NotifyStartup();
                Assert.Equal(1, f.Target.SyncNowCalls);
            }
        }

        [Fact]
        public async Task Startup_WhenSignedOut_DoesNotSync()
        {
            using (var f = new Fixture())
            {
                f.SetState(SyncPhase.SignedOut, false, true, null);
                f.Triggers.NotifyStartup();
                await Task.Delay(150);
                Assert.Equal(0, f.Target.SyncNowCalls);
            }
        }

        [Fact]
        public void Foreground_WhenStale_Syncs()
        {
            using (var f = new Fixture())
            {
                f.SetState(SyncPhase.Idle, true, true, f.Iso(f.Now.AddSeconds(-60)));
                f.Triggers.NotifyForeground();
                Assert.Equal(1, f.Target.SyncNowCalls);
                Assert.True(f.Triggers.IsPolling);
            }
        }

        [Fact]
        public async Task Foreground_WhenFresh_DoesNotSyncButPolls()
        {
            using (var f = new Fixture())
            {
                f.SetState(SyncPhase.Synced, true, true, f.Iso(f.Now.AddSeconds(-10)));
                f.Triggers.NotifyForeground();
                await Task.Delay(150);
                Assert.Equal(0, f.Target.SyncNowCalls);
                Assert.True(f.Triggers.IsPolling);
            }
        }

        [Fact]
        public async Task Foreground_ExactlyAtThreshold_DoesNotSync()
        {
            using (var f = new Fixture())
            {
                f.SetState(SyncPhase.Idle, true, true, f.Iso(f.Now.AddSeconds(-30)));
                f.Triggers.NotifyForeground();
                await Task.Delay(150);
                Assert.Equal(0, f.Target.SyncNowCalls);
            }
        }

        [Fact]
        public void Foreground_WhenNeverSynced_Syncs()
        {
            using (var f = new Fixture())
            {
                f.SetState(SyncPhase.Idle, true, true, null);
                f.Triggers.NotifyForeground();
                Assert.Equal(1, f.Target.SyncNowCalls);
            }
        }

        [Fact]
        public async Task Foreground_WhenAutoSyncOff_DoesNotSync()
        {
            using (var f = new Fixture())
            {
                f.SetState(SyncPhase.Idle, true, false, f.Iso(f.Now.AddHours(-1)));
                f.Triggers.NotifyForeground();
                await Task.Delay(150);
                Assert.Equal(0, f.Target.SyncNowCalls);
            }
        }

        [Fact]
        public void NetworkRestored_WhenAutoSync_Syncs()
        {
            using (var f = new Fixture())
            {
                f.Triggers.NotifyNetworkRestored();
                Assert.Equal(1, f.Target.SyncNowCalls);
            }
        }

        [Fact]
        public async Task NetworkRestored_WhenAutoSyncOff_DoesNotSync()
        {
            using (var f = new Fixture())
            {
                f.SetState(SyncPhase.Idle, true, false, null);
                f.Triggers.NotifyNetworkRestored();
                await Task.Delay(150);
                Assert.Equal(0, f.Target.SyncNowCalls);
            }
        }

        // ---- 轮询启停 ----

        [Fact]
        public void Poll_StopsOnBackground()
        {
            using (var f = new Fixture())
            {
                f.Triggers.NotifyForeground();
                Assert.True(f.Triggers.IsPolling);
                f.Triggers.NotifyBackground();
                Assert.False(f.Triggers.IsPolling);
            }
        }

        [Fact]
        public void Poll_DisabledWhenIntervalZero()
        {
            using (var f = new Fixture())
            {
                f.Settings.SyncPollForegroundSeconds = 0;
                f.Triggers.NotifyForeground();
                Assert.False(f.Triggers.IsPolling);
            }
        }

        [Fact]
        public async Task Poll_TickSyncs_WithoutThreshold()
        {
            using (var f = new Fixture())
            {
                // 刚同步过（前台阈值内不补），但轮询 tick 不受阈值限制。
                f.SetState(SyncPhase.Idle, true, true, f.Iso(f.Now.AddSeconds(-5)));
                f.Triggers.NotifyForeground();
                Assert.Equal(0, f.Target.SyncNowCalls);
                f.Timers.FirePending();
                await WaitForAsync(() => f.Target.SyncNowCalls, 1);
            }
        }

        [Fact]
        public void Poll_IntervalChangeRearms()
        {
            using (var f = new Fixture())
            {
                f.Triggers.NotifyForeground();
                Assert.True(f.Triggers.IsPolling);
                f.Settings.SyncPollForegroundSeconds = 120;
                ManualTimer live = null;
                foreach (ManualTimer t in f.Timers.Timers)
                {
                    if (t.Periodic && !t.Disposed)
                    {
                        live = t;
                    }
                }
                Assert.NotNull(live);
                Assert.Equal(120000, live.DelayMs);
                f.Settings.SyncPollForegroundSeconds = 0;
                Assert.False(f.Triggers.IsPolling);
            }
        }

        // ---- 手动触发 ----

        [Fact]
        public async Task Manual_AwaitsSyncNow()
        {
            using (var f = new Fixture())
            {
                await f.Triggers.RequestManualSyncAsync();
                Assert.Equal(1, f.Target.SyncNowCalls);
            }
        }

        [Fact]
        public async Task Manual_PropagatesError()
        {
            using (var f = new Fixture())
            {
                f.Target.OnSyncNow = () => Task.FromException(new InvalidOperationException("nope"));
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => f.Triggers.RequestManualSyncAsync());
            }
        }

        // ---- 凭据存储事件行为（触发器的输入契约） ----

        [Fact]
        public async Task SecretStore_RaisesChanged_WithKeysAndOrigin()
        {
            using (var f = new Fixture())
            {
                SecretChangedEventArgs got = null;
                f.Secrets.Changed += (s, e) => { got = e; };
                await f.Secrets.SetAsync(SecretKeys.HostPassword("h1"), "x");
                Assert.NotNull(got);
                Assert.Equal(ChangeOrigin.User, got.Origin);
                Assert.True(SecretChangedEventArgs.HasCredentialKey(got.ChangedKeys));
            }
        }

        [Fact]
        public async Task SecretStore_RemoveMissingKey_RaisesNothing()
        {
            using (var f = new Fixture())
            {
                int calls = 0;
                f.Secrets.Changed += (s, e) => { calls++; };
                await f.Secrets.RemoveAsync("host:nobody:password");
                await f.Secrets.RemoveByPrefixAsync("host:nobody:");
                await Task.Delay(100);
                Assert.Equal(0, calls);
            }
        }
    }
}
