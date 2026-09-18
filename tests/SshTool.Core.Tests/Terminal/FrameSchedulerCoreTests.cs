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
    }
}
