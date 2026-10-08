using System;
using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    public class FrameSchedulerCoreTests
    {
        private sealed class ManualTicker : IFrameTicker
        {
            public int StartCount;
            public int StopCount;
            public bool Running;
            private Action _cb = () => { };

            public void SetFrameCallback(Action callback)
            {
                _cb = callback ?? (() => { });
            }

            public void Start()
            {
                Running = true;
                StartCount++;
            }

            public void Stop()
            {
                Running = false;
                StopCount++;
            }

            public void Tick(int count = 1)
            {
                for (int i = 0; i < count && Running; i++)
                {
                    _cb();
                }
            }
        }

        [Fact]
        public void Register_StartsLoop_AndDispatchesInOrder()
        {
            var ticker = new ManualTicker();
            var core = new FrameSchedulerCore(ticker, true, 90, 100000, () => 0);
            var order = new System.Collections.Generic.List<string>();
            core.Register("c", () => { order.Add("c"); return false; });
            core.Register("a", () => { order.Add("a"); return false; });
            Assert.True(core.IsRunning);
            ticker.Tick(1);
            Assert.Equal(new[] { "c", "a" }, order);
        }

        [Fact]
        public void DuplicateOrEmptyId_Throws()
        {
            var core = new FrameSchedulerCore(new ManualTicker());
            core.Register("a", () => false);
            Assert.Throws<InvalidOperationException>(() => core.Register("a", () => false));
            Assert.Throws<ArgumentException>(() => core.Register("", () => false));
            Assert.Equal(1, core.Size);
        }

        [Fact]
        public void InvisibleView_IsNotPulled()
        {
            var ticker = new ManualTicker();
            var core = new FrameSchedulerCore(ticker, true, 90, 100000, () => 0);
            int a = 0, b = 0;
            core.Register("a", () => { a++; return false; });
            core.Register("b", () => { b++; return false; });
            core.SetVisible("b", false);
            ticker.Tick(3);
            Assert.Equal(3, a);
            Assert.Equal(0, b);
            core.SetVisible("b", true);
            ticker.Tick(1);
            Assert.Equal(1, b);
        }

        [Fact]
        public void IdleFrames_Unsubscribe_WakeResubscribes()
        {
            var ticker = new ManualTicker();
            var core = new FrameSchedulerCore(ticker, true, 3, 100000, () => 0);
            int calls = 0;
            core.Register("a", () => { calls++; return false; });
            ticker.Tick(2);
            Assert.True(core.IsRunning);
            ticker.Tick(1);
            Assert.False(core.IsRunning);
            int atSleep = calls;
            ticker.Tick(5);
            Assert.Equal(atSleep, calls);
            core.Wake();
            Assert.True(core.IsRunning);
            Assert.Equal(2, ticker.StartCount);
        }

        [Fact]
        public void WorkResetsIdleStreak()
        {
            var ticker = new ManualTicker();
            var core = new FrameSchedulerCore(ticker, true, 3, 100000, () => 0);
            bool work = false;
            core.Register("a", () => work);
            ticker.Tick(2);
            work = true;
            ticker.Tick(1);
            work = false;
            ticker.Tick(2);
            Assert.True(core.IsRunning);
            ticker.Tick(1);
            Assert.False(core.IsRunning);
        }

        [Fact]
        public void BlinkFlipsAtInjectedClock()
        {
            long now = 0;
            var ticker = new ManualTicker();
            var core = new FrameSchedulerCore(ticker, true, 30, 530, () => now);
            core.Register("a", () => false);
            Assert.True(core.BlinkOn);
            ticker.Tick(1);
            Assert.True(core.BlinkOn);
            now = 530;
            ticker.Tick(1);
            Assert.False(core.BlinkOn);
            now = 1060;
            ticker.Tick(1);
            Assert.True(core.BlinkOn);
        }

        [Fact]
        public void LastUnregister_StopsLoop()
        {
            var ticker = new ManualTicker();
            var core = new FrameSchedulerCore(ticker);
            core.Register("a", () => false);
            Assert.False(core.Unregister("ghost"));
            Assert.True(core.Unregister("a"));
            Assert.False(core.IsRunning);
            Assert.Equal(1, ticker.StopCount);
        }

        [Fact]
        public void CallbackException_IsolatedAndCounted()
        {
            var ticker = new ManualTicker();
            var core = new FrameSchedulerCore(ticker, true, 90, 100000, () => 0);
            int b = 0;
            core.Register("a", () => { throw new InvalidOperationException(); });
            core.Register("b", () => { b++; return false; });
            ticker.Tick(1);
            Assert.Equal(1, core.CallbackErrors);
            Assert.Equal(1, b);
            Assert.True(core.IsRunning);
        }

        // ---- opt/full-pass：闪烁不算工作 + 空闲后闪烁定时器；按真实帧率模拟 ----

        private sealed class FakeBlinkTimer : IBlinkTimer
        {
            public bool Armed;
            public long FireAt;
            public int StartCount;
            public Func<long> Now = () => 0;
            private Action _cb;

            public void Start(int dueMs, Action callback)
            {
                Armed = true;
                StartCount++;
                FireAt = Now() + dueMs;
                _cb = callback;
            }

            public void Stop()
            {
                Armed = false;
            }

            public void Fire()
            {
                Armed = false;
                _cb();
            }
        }

        private sealed class Sim
        {
            public long Now;
            public readonly ManualTicker Ticker = new ManualTicker();
            public readonly FakeBlinkTimer Timer = new FakeBlinkTimer();
            public FrameSchedulerCore Core;
            public int TickerFrames;
            public int TimerFires;
            public readonly System.Collections.Generic.List<long> FlipTimes = new System.Collections.Generic.List<long>();

            public Sim(bool withTimer = true)
            {
                Timer.Now = () => Now;
                Core = new FrameSchedulerCore(Ticker, true, FrameSchedulerCore.DefaultIdleFrames,
                    FrameSchedulerCore.DefaultBlinkPeriodMs, () => Now, withTimer ? Timer : null);
            }

            // 事件驱动推进：ticker 运转时按 frameMs 出帧；停下后跳到定时器到期点
            public void Run(double fps, long untilMs)
            {
                double frameMs = 1000.0 / fps;
                double t = Now;
                while (true)
                {
                    bool last = Core.BlinkOn;
                    if (Ticker.Running)
                    {
                        t += frameMs;
                        if (t > untilMs) { break; }
                        Now = (long)t;
                        TickerFrames++;
                        Ticker.Tick(1);
                    }
                    else if (Timer.Armed)
                    {
                        if (Timer.FireAt > untilMs) { break; }
                        Now = Timer.FireAt;
                        t = Now;
                        TimerFires++;
                        Timer.Fire();
                    }
                    else
                    {
                        break;
                    }
                    if (Core.BlinkOn != last)
                    {
                        FlipTimes.Add(Now);
                    }
                }
                Now = untilMs;
            }
        }

        [Theory]
        [InlineData(60)]
        [InlineData(50)]
        [InlineData(30)]
        public void RealFps_IdleTickerStops_ButCursorKeepsBlinking(int fps)
        {
            var sim = new Sim();
            int callbacks = 0;
            sim.Core.Register("tv", () => { callbacks++; return false; }, () => true);
            sim.Run(fps, 5300);

            // 旧实现：60 fps 下 500 ms 后停下且不再闪；50/30 fps 下每帧都跑（5 秒 250/150 帧）
            Assert.False(sim.Core.IsRunning);
            Assert.True(sim.TickerFrames <= FrameSchedulerCore.DefaultIdleFrames, "ticker frames=" + sim.TickerFrames);
            Assert.InRange(sim.FlipTimes.Count, 9, 10);
            long prev = 0;
            foreach (long flip in sim.FlipTimes)
            {
                // 每次翻转间隔 = 530 ms（ticker 内翻转最多晚一帧）
                Assert.InRange(flip - prev, FrameSchedulerCore.DefaultBlinkPeriodMs,
                    FrameSchedulerCore.DefaultBlinkPeriodMs + (long)Math.Ceiling(1000.0 / fps));
                prev = flip;
            }
            // 每次定时器翻转都派发一次回调，让视图重画光标
            Assert.Equal(sim.TickerFrames + sim.TimerFires, callbacks);
            Assert.True(sim.Timer.Armed);
        }

        [Theory]
        [InlineData(60)]
        [InlineData(50)]
        [InlineData(30)]
        public void RealFps_NoBlinkWanted_StopsAndStaysQuiet(int fps)
        {
            var sim = new Sim();
            sim.Core.Register("tv", () => false, () => false);
            sim.Run(fps, 5000);
            Assert.False(sim.Core.IsRunning);
            Assert.Equal(FrameSchedulerCore.DefaultIdleFrames, sim.TickerFrames);
            Assert.Equal(0, sim.Timer.StartCount);
        }

        [Fact]
        public void BlinkFlip_IsNotWork_WithoutTimerTickerStillSleeps()
        {
            var sim = new Sim(withTimer: false);
            sim.Core.Register("tv", () => false, () => true);
            sim.Run(30, 5000); // 30 fps：旧实现在这里因 530 ms 翻转永不休眠
            Assert.False(sim.Core.IsRunning);
            Assert.Equal(FrameSchedulerCore.DefaultIdleFrames, sim.TickerFrames);
        }

        [Fact]
        public void Wake_CancelsBlinkTimer_AndLateFireIsIgnored()
        {
            var sim = new Sim();
            int callbacks = 0;
            sim.Core.Register("tv", () => { callbacks++; return false; }, () => true);
            sim.Run(60, 600);
            Assert.True(sim.Core.IsBlinkTimerArmed);
            sim.Core.Wake();
            Assert.False(sim.Core.IsBlinkTimerArmed);
            Assert.False(sim.Timer.Armed);
            Assert.True(sim.Core.IsRunning);
            int before = callbacks;
            bool blink = sim.Core.BlinkOn;
            sim.Timer.Fire(); // 迟到的回调
            Assert.Equal(before, callbacks);
            Assert.Equal(blink, sim.Core.BlinkOn);
        }

        [Fact]
        public void TimerFire_WithContentWork_ReturnsToTicker()
        {
            var sim = new Sim();
            bool work = false;
            sim.Core.Register("tv", () => work, () => true);
            sim.Run(60, 600);
            Assert.False(sim.Core.IsRunning);
            work = true;
            sim.Now = sim.Timer.FireAt;
            sim.Timer.Fire();
            Assert.True(sim.Core.IsRunning);
            Assert.False(sim.Core.IsBlinkTimerArmed);
        }

        [Fact]
        public void HiddenView_DoesNotKeepBlinkTimerAlive()
        {
            var sim = new Sim();
            sim.Core.Register("tv", () => false, () => true);
            sim.Run(60, 600);
            Assert.True(sim.Timer.Armed);
            sim.Core.SetVisible("tv", false); // 不可见：不 Wake
            sim.Now = sim.Timer.FireAt;
            sim.Timer.Fire();
            Assert.False(sim.Timer.Armed);
            Assert.False(sim.Core.IsBlinkTimerArmed);
        }

        [Fact]
        public void Unregister_Last_CancelsBlinkTimer()
        {
            var sim = new Sim();
            sim.Core.Register("tv", () => false, () => true);
            sim.Run(60, 600);
            Assert.True(sim.Timer.Armed);
            sim.Core.Unregister("tv");
            Assert.False(sim.Timer.Armed);
        }
    }
}
