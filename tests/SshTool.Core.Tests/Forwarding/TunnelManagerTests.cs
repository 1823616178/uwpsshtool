using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Forwarding;
using SshTool.Core.Models;
using SshTool.Core.Sessions;
using SshTool.Core.Tests.Fakes;
using Xunit;

namespace SshTool.Core.Tests.Forwarding
{
    // F05：TunnelManager 状态机——启停、自动重连退避、autoStart、IsBusy、
    // relay 拒绝、每秒速率统计。
    public class TunnelManagerTests
    {
        private static Tunnel MakeTunnel(string id, TunnelType type = TunnelType.Local,
            bool autoReconnect = true, bool enabled = true, bool autoStart = false,
            string groupId = null)
        {
            return new Tunnel
            {
                Id = id,
                Name = "t-" + id,
                ServerId = "server-" + id,
                GroupId = groupId,
                Type = type,
                ListenHost = "127.0.0.1",
                ListenPort = 10000 + id.Length,
                DestHost = "127.0.0.1",
                DestPort = 80,
                AutoReconnect = autoReconnect,
                Enabled = enabled,
                AutoStart = autoStart
            };
        }

        private static TunnelManager MakeManager(
            FakeTunnelRuntime runtime, ManualTimerFactory timers, out ImmediateDispatcher ui)
        {
            ui = new ImmediateDispatcher();
            var manager = new TunnelManager(runtime, timers, ui, null);
            return manager;
        }

        // 重试一律失败的脚本（首启成功）。
        private static void ScriptFailuresAfterFirstStart(FakeTunnelRuntime runtime)
        {
            int calls = 0;
            runtime.OnStart = (t, c) =>
            {
                calls++;
                if (calls == 1)
                {
                    return new TunnelRuntimeStartResult
                    {
                        Code = SshErrorCode.None,
                        RouteDescription = "route"
                    };
                }
                return new TunnelRuntimeStartResult
                {
                    Code = SshErrorCode.ConnectionRefused,
                    Message = "拒绝"
                };
            };
        }

        [Fact]
        public async Task StartSuccessRunsAndIsBusy()
        {
            var runtime = new FakeTunnelRuntime();
            TunnelManager manager = MakeManager(runtime, new ManualTimerFactory(), out var ui);
            Tunnel tunnel = MakeTunnel("t1");
            manager.ApplyConfig(new[] { tunnel });

            AssertState(TunnelStateKind.Idle, manager.GetStatus("t1"));
            TunnelStartResult result = await manager.StartAsync(tunnel);

            Assert.True(result.Success);
            AssertState(TunnelStateKind.Running, manager.GetStatus("t1"));
            Assert.True(manager.IsBusy("t1"));
            Assert.Equal("route t1", manager.GetStatus("t1").Message);
            Assert.True(manager.GetStatus("t1").Stats.Since != null);
            Assert.Equal(0, manager.GetStatus("t1").Attempt);
        }

        [Fact]
        public void RelayIsRejectedBeforeAnyRuntimeCall()
        {
            var runtime = new FakeTunnelRuntime();
            ManualTimerFactory timers = new ManualTimerFactory();
            TunnelManager manager = MakeManager(runtime, timers, out var ui);
            Tunnel relay = MakeTunnel("r1", TunnelType.Relay);
            manager.ApplyConfig(new[] { relay });

            TunnelStartResult result = manager.StartAsync(relay).Result;

            Assert.False(result.Success);
            Assert.Contains("桌面端", result.Message);
            // 与桌面端一致：拒绝发生在建实例之前，状态保持 Idle。
            AssertState(TunnelStateKind.Idle, manager.GetStatus("r1"));
            Assert.False(manager.IsBusy("r1"));
            Assert.DoesNotContain("start:r1", runtime.Calls);
        }

        [Fact]
        public void FirstStartFailureGoesToErrorWithMessage()
        {
            var runtime = new FakeTunnelRuntime();
            runtime.StartResults["t1"] = new TunnelRuntimeStartResult
            {
                Code = SshErrorCode.ConnectionRefused,
                Message = "连接被拒绝"
            };
            ManualTimerFactory timers = new ManualTimerFactory();
            TunnelManager manager = MakeManager(runtime, timers, out var ui);
            Tunnel tunnel = MakeTunnel("t1");
            manager.ApplyConfig(new[] { tunnel });

            TunnelStartResult result = manager.StartAsync(tunnel).Result;

            Assert.False(result.Success);
            AssertState(TunnelStateKind.Error, manager.GetStatus("t1"));
            Assert.False(manager.IsBusy("t1"));
            // 首启失败不安排重连计时器。
            Assert.True(timers.Timers.All(t => t.Disposed || t.Periodic));
        }

        [Fact]
        public void RuntimeDropSchedulesBackoffThenRetryRecovers()
        {
            var runtime = new FakeTunnelRuntime();
            ManualTimerFactory timers = new ManualTimerFactory();
            TunnelManager manager = MakeManager(runtime, timers, out var ui);
            Tunnel tunnel = MakeTunnel("t1");
            manager.ApplyConfig(new[] { tunnel });

            var ignore = manager.StartAsync(tunnel);
            AssertState(TunnelStateKind.Running, manager.GetStatus("t1"));

            runtime.Drop("t1", "连接被重置", SshErrorCode.SocketError);
            TunnelStatusSnapshot reconnecting = manager.GetStatus("t1");
            AssertState(TunnelStateKind.Reconnecting, reconnecting);
            Assert.Equal(1, reconnecting.Attempt);
            Assert.Equal(1, reconnecting.RetryInSeconds);
            Assert.True(reconnecting.Stats.Since == null);
            Assert.Contains("t1", runtime.StoppedIds);

            timers.FirePending(); // 触发重连：默认脚本成功 → 回到 Running
            AssertState(TunnelStateKind.Running, manager.GetStatus("t1"));
            Assert.Equal(0, manager.GetStatus("t1").Attempt);
            Assert.True(manager.GetStatus("t1").Stats.Since != null);
        }

        [Fact]
        public void BackoffSequenceFollowsDesktopTable()
        {
            var runtime = new FakeTunnelRuntime();
            ManualTimerFactory timers = new ManualTimerFactory();
            TunnelManager manager = MakeManager(runtime, timers, out var ui);
            manager.MaxBackoffSeconds = 60;
            Tunnel tunnel = MakeTunnel("t1");
            manager.ApplyConfig(new[] { tunnel });

            // 首启成功，之后所有重试一律失败 → 逐档走退避表。
            ScriptFailuresAfterFirstStart(runtime);
            var ignore = manager.StartAsync(tunnel);
            AssertState(TunnelStateKind.Running, manager.GetStatus("t1"));

            int[] expected = { 1, 2, 5, 10, 20, 30, 60, 60 };
            runtime.Drop("t1", "down", SshErrorCode.SocketError);
            Assert.Equal(expected[0], LastRetryDelay(timers));
            for (int i = 1; i < expected.Length; i++)
            {
                timers.FirePending(); // 重试失败 → 下一档退避
                AssertState(TunnelStateKind.Reconnecting, manager.GetStatus("t1"));
                Assert.Equal(i + 1, manager.GetStatus("t1").Attempt);
                Assert.Equal(expected[i], LastRetryDelay(timers));
            }
        }

        [Fact]
        public void MaxBackoffCapsDelay()
        {
            var runtime = new FakeTunnelRuntime();
            ManualTimerFactory timers = new ManualTimerFactory();
            TunnelManager manager = MakeManager(runtime, timers, out var ui);
            manager.MaxBackoffSeconds = 30;
            Tunnel tunnel = MakeTunnel("t1");
            manager.ApplyConfig(new[] { tunnel });

            ScriptFailuresAfterFirstStart(runtime);
            var ignore = manager.StartAsync(tunnel);

            runtime.Drop("t1", "down", SshErrorCode.SocketError);
            for (int i = 0; i < 8; i++)
            {
                timers.FirePending();
                AssertState(TunnelStateKind.Reconnecting, manager.GetStatus("t1"));
                int delay = LastRetryDelay(timers);
                Assert.True(delay <= 30);
                if (i >= 4)
                {
                    Assert.Equal(30, delay); // 退避表第 6 档（60 s）被 30 封顶
                }
            }
        }

        [Fact]
        public void DropWithoutAutoReconnectGoesToError()
        {
            var runtime = new FakeTunnelRuntime();
            ManualTimerFactory timers = new ManualTimerFactory();
            TunnelManager manager = MakeManager(runtime, timers, out var ui);
            Tunnel tunnel = MakeTunnel("t1", autoReconnect: false);
            manager.ApplyConfig(new[] { tunnel });

            var ignore = manager.StartAsync(tunnel);
            runtime.Drop("t1", "掉了", SshErrorCode.RemoteClosed);

            AssertState(TunnelStateKind.Error, manager.GetStatus("t1"));
            Assert.Equal("掉了", manager.GetStatus("t1").Message);
            Assert.False(manager.IsBusy("t1"));
        }

        [Fact]
        public void StopReturnsToIdleAndSuppressesReconnect()
        {
            var runtime = new FakeTunnelRuntime();
            ManualTimerFactory timers = new ManualTimerFactory();
            TunnelManager manager = MakeManager(runtime, timers, out var ui);
            Tunnel tunnel = MakeTunnel("t1");
            manager.ApplyConfig(new[] { tunnel });

            var ignore = manager.StartAsync(tunnel);
            manager.Stop("t1", "已手动停止");

            AssertState(TunnelStateKind.Idle, manager.GetStatus("t1"));
            Assert.False(manager.IsBusy("t1"));
            // 迟到的 Dropped 不回跳。
            runtime.Drop("t1", "late", SshErrorCode.SocketError);
            AssertState(TunnelStateKind.Idle, manager.GetStatus("t1"));
        }

        [Fact]
        public async Task StartWhileBusyIsIdempotent()
        {
            var runtime = new FakeTunnelRuntime();
            runtime.StartDelayMs = 30;
            ManualTimerFactory timers = new ManualTimerFactory();
            TunnelManager manager = MakeManager(runtime, timers, out var ui);
            Tunnel tunnel = MakeTunnel("t1");
            manager.ApplyConfig(new[] { tunnel });

            Task<TunnelStartResult> first = manager.StartAsync(tunnel);
            TunnelStartResult second = await manager.StartAsync(tunnel);
            await first;

            Assert.True(second.Success);
            AssertState(TunnelStateKind.Running, manager.GetStatus("t1"));
            Assert.Equal(1, runtime.Started.Count(t => t.Id == "t1"));
        }

        [Fact]
        public void ApplyConfigRemovesDeletedTunnelAndStopsIt()
        {
            var runtime = new FakeTunnelRuntime();
            ManualTimerFactory timers = new ManualTimerFactory();
            TunnelManager manager = MakeManager(runtime, timers, out var ui);
            Tunnel keep = MakeTunnel("keep");
            Tunnel gone = MakeTunnel("gone");
            manager.ApplyConfig(new[] { keep, gone });
            var ignore = manager.StartAsync(gone);

            manager.ApplyConfig(new[] { keep });

            AssertState(TunnelStateKind.Idle, manager.GetStatus("gone"));
            Assert.Contains("gone", runtime.StoppedIds);
            Assert.Equal(1, manager.Configurations.Count);
        }

        [Fact]
        public void AutoStartOnlyEnabledAndMarked()
        {
            var runtime = new FakeTunnelRuntime();
            runtime.StartResults["bad"] = new TunnelRuntimeStartResult
            {
                Code = SshErrorCode.ConnectionRefused,
                Message = "拒绝"
            };
            ManualTimerFactory timers = new ManualTimerFactory();
            TunnelManager manager = MakeManager(runtime, timers, out var ui);
            manager.ApplyConfig(new[]
            {
                MakeTunnel("auto", autoStart: true),
                MakeTunnel("notmarked", autoStart: false),
                MakeTunnel("disabled", autoStart: true, enabled: false),
                MakeTunnel("bad", autoStart: true)
            });

            IReadOnlyList<string> errors = manager.StartAutoStartAsync().Result;

            Assert.Equal(2, runtime.Started.Count(t => t.AutoStart && t.Enabled));
            Assert.DoesNotContain("start:notmarked", runtime.Calls);
            Assert.DoesNotContain("start:disabled", runtime.Calls);
            Assert.Equal(1, errors.Count);
            Assert.StartsWith("t-bad：", errors[0]);
        }

        [Fact]
        public void StatusesCoverAllConfigsIncludingIdle()
        {
            var runtime = new FakeTunnelRuntime();
            ManualTimerFactory timers = new ManualTimerFactory();
            TunnelManager manager = MakeManager(runtime, timers, out var ui);
            Tunnel a = MakeTunnel("a");
            Tunnel b = MakeTunnel("b");
            manager.ApplyConfig(new[] { a, b });
            var ignore = manager.StartAsync(a);

            IReadOnlyList<TunnelStatusSnapshot> statuses = manager.Statuses;

            Assert.Equal(2, statuses.Count);
            AssertState(TunnelStateKind.Running, statuses.First(s => s.TunnelId == "a"));
            AssertState(TunnelStateKind.Idle, statuses.First(s => s.TunnelId == "b"));
        }

        [Fact]
        public void TickRefreshesRatesFromRuntimeSamples()
        {
            var runtime = new FakeTunnelRuntime();
            ManualTimerFactory timers = new ManualTimerFactory();
            TunnelManager manager = MakeManager(runtime, timers, out var ui);
            Tunnel tunnel = MakeTunnel("t1");
            manager.ApplyConfig(new[] { tunnel });
            var ignore = manager.StartAsync(tunnel);

            runtime.Samples["t1"] = new TunnelStatsSample
            {
                ActiveConnections = 2,
                TotalConnections = 5,
                BytesIn = 100,
                BytesOut = 40
            };
            timers.FirePending();
            TunnelStats first = manager.GetStatus("t1").Stats;
            Assert.Equal(2UL, first.ActiveConnections);
            Assert.Equal(5UL, first.TotalConnections);
            Assert.Equal(100UL, first.BytesIn);
            Assert.Equal(40UL, first.BytesOut);
            Assert.Equal(100UL, first.RateIn);
            Assert.Equal(40UL, first.RateOut);

            runtime.Samples["t1"] = new TunnelStatsSample
            {
                ActiveConnections = 1,
                TotalConnections = 5,
                BytesIn = 160,
                BytesOut = 40
            };
            timers.FirePending();
            TunnelStats second = manager.GetStatus("t1").Stats;
            Assert.Equal(60UL, second.RateIn);
            Assert.Equal(0UL, second.RateOut);
            Assert.Equal(1UL, second.ActiveConnections);
        }

        [Fact]
        public void IsBusyProbeMatchesStates()
        {
            var runtime = new FakeTunnelRuntime();
            runtime.StartDelayMs = 30;
            ManualTimerFactory timers = new ManualTimerFactory();
            TunnelManager manager = MakeManager(runtime, timers, out var ui);
            Tunnel tunnel = MakeTunnel("t1");
            manager.ApplyConfig(new[] { tunnel });

            Task<TunnelStartResult> pending = manager.StartAsync(tunnel);
            Assert.True(manager.IsBusy("t1")); // connecting
            Task.WaitAll(pending);
            Assert.True(manager.IsBusy("t1")); // running

            manager.Stop("t1");
            Assert.False(manager.IsBusy("t1"));
            Assert.False(manager.IsBusy("unknown"));
        }

        [Fact]
        public void StopGroupStopsOnlyMatchingGroup()
        {
            var runtime = new FakeTunnelRuntime();
            ManualTimerFactory timers = new ManualTimerFactory();
            TunnelManager manager = MakeManager(runtime, timers, out var ui);
            manager.ApplyConfig(new[]
            {
                MakeTunnel("a", groupId: "g1"),
                MakeTunnel("b", groupId: "g2"),
                MakeTunnel("c", groupId: null)
            });
            var one = manager.StartAsync(manager.Configurations.First(t => t.Id == "a")).Result;
            var two = manager.StartAsync(manager.Configurations.First(t => t.Id == "b")).Result;

            manager.StopGroup("g1");

            AssertState(TunnelStateKind.Idle, manager.GetStatus("a"));
            AssertState(TunnelStateKind.Running, manager.GetStatus("b"));
            AssertState(TunnelStateKind.Idle, manager.GetStatus("c"));
        }

        [Fact]
        public void DisposeStopsEverything()
        {
            var runtime = new FakeTunnelRuntime();
            ManualTimerFactory timers = new ManualTimerFactory();
            TunnelManager manager = MakeManager(runtime, timers, out var ui);
            manager.ApplyConfig(new[] { MakeTunnel("t1") });
            var ignore = manager.StartAsync(manager.Configurations.First());

            manager.Dispose();

            Assert.Contains("t1", runtime.StoppedIds);
            Assert.Empty(manager.Configurations);
            Assert.False(manager.IsBusy("t1"));
        }

        private static int LastRetryDelay(ManualTimerFactory timers)
        {
            ManualTimer timer = timers.Timers.Last(t => !t.Periodic);
            Assert.False(timer.Disposed);
            return timer.DelayMs / TunnelManager.TickPeriodMs;
        }

        private static void AssertState(TunnelStateKind expected, TunnelStatusSnapshot status)
        {
            Assert.NotNull(status);
            Assert.Equal(expected, status.State);
        }
    }
}
