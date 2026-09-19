using System.Collections.Generic;
using SshTool.Core.Lifecycle;
using Xunit;

namespace SshTool.Core.Tests.Lifecycle
{
    // P01：KeepAwakePolicy 全组合 + DisplayRequestTracker 成对保护。
    public class KeepAwakePolicyTests
    {
        // ---- ShouldKeepScreenOn：显式真值表（3 档 × 可见 × 会话数） ----

        [Theory]
        [InlineData("never", true, 0, false)]
        [InlineData("never", true, 1, false)]
        [InlineData("never", true, 5, false)]
        [InlineData("never", false, 3, false)]
        [InlineData("session", true, 0, false)]
        [InlineData("session", true, 1, true)]
        [InlineData("session", true, 5, true)]
        [InlineData("session", false, 3, false)]
        [InlineData("always", true, 0, true)]
        [InlineData("always", true, 2, true)]
        [InlineData("always", false, 3, false)]
        public void ShouldKeepScreenOn_Table(string mode, bool visible, int sessions, bool expected)
        {
            Assert.Equal(expected, KeepAwakePolicy.ShouldKeepScreenOn(mode, visible, sessions));
        }

        // ---- 全组合矩阵：非法输入 fail-safe 关断，且永不抛异常 ----

        [Fact]
        public void ShouldKeepScreenOn_FullMatrix()
        {
            string[] modes = { "never", "session", "always", null, "", "bogus", "SESSION", "Always", " session" };
            bool[] visibles = { false, true };
            int[] counts = { -5, -1, 0, 1, 2, 100 };
            foreach (string mode in modes)
            {
                foreach (bool visible in visibles)
                {
                    foreach (int n in counts)
                    {
                        bool actual = KeepAwakePolicy.ShouldKeepScreenOn(mode, visible, n);
                        Assert.Equal(Expected(mode, visible, n), actual);
                    }
                }
            }
        }

        private static bool Expected(string mode, bool visible, int n)
        {
            if (!visible)
            {
                return false;
            }
            if (mode == KeepAwakePolicy.ModeAlways)
            {
                return true;
            }
            if (mode == KeepAwakePolicy.ModeSession)
            {
                return n > 0;
            }
            return false;
        }

        [Theory]
        [InlineData("never", "never")]
        [InlineData("session", "session")]
        [InlineData("always", "always")]
        [InlineData(null, "session")]
        [InlineData("", "session")]
        [InlineData("bogus", "session")]
        [InlineData("SESSION", "session")]
        public void NormalizeMode_Table(string input, string expected)
        {
            Assert.Equal(expected, KeepAwakePolicy.NormalizeMode(input));
        }

        // ---- DisplayRequestTracker：成对调用计数保护 ----

        private sealed class FakeGateway : IDisplayKeepAwakeGateway
        {
            public readonly List<string> Calls = new List<string>();
            public bool ThrowOnActive;

            public void RequestActive()
            {
                if (ThrowOnActive)
                {
                    throw new System.InvalidOperationException("no display");
                }
                Calls.Add("Active");
            }

            public void RequestRelease()
            {
                Calls.Add("Release");
            }
        }

        [Fact]
        public void Tracker_NullGateway_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() => new DisplayRequestTracker(null));
        }

        [Fact]
        public void Tracker_AcquireRelease_PairsSystemCalls()
        {
            var gateway = new FakeGateway();
            var tracker = new DisplayRequestTracker(gateway);
            tracker.Acquire();
            tracker.Acquire();
            tracker.Acquire();
            Assert.Single(gateway.Calls); // 0→1 只下发一次
            Assert.Equal(3, tracker.ActiveCount);
            tracker.Release();
            tracker.Release();
            Assert.Single(gateway.Calls); // 未归零不释放
            tracker.Release();
            Assert.Equal(new List<string> { "Active", "Release" }, gateway.Calls);
            Assert.Equal(0, tracker.ActiveCount);
        }

        [Fact]
        public void Tracker_ReleaseAtZero_NoSystemCall()
        {
            var gateway = new FakeGateway();
            var tracker = new DisplayRequestTracker(gateway);
            tracker.Release();
            tracker.ReleaseAll();
            Assert.Empty(gateway.Calls);
        }

        [Fact]
        public void Tracker_ReleaseAll_SingleRelease()
        {
            var gateway = new FakeGateway();
            var tracker = new DisplayRequestTracker(gateway);
            tracker.Acquire();
            tracker.Acquire();
            tracker.ReleaseAll();
            Assert.Equal(new List<string> { "Active", "Release" }, gateway.Calls);
            Assert.Equal(0, tracker.ActiveCount);
            tracker.ReleaseAll(); // 幂等
            Assert.Equal(2, gateway.Calls.Count);
        }

        [Fact]
        public void Tracker_Refresh_Idempotent()
        {
            var gateway = new FakeGateway();
            var tracker = new DisplayRequestTracker(gateway);
            tracker.Refresh(true);
            tracker.Refresh(true);
            Assert.Equal(new List<string> { "Active" }, gateway.Calls);
            tracker.Refresh(false);
            tracker.Refresh(false);
            Assert.Equal(new List<string> { "Active", "Release" }, gateway.Calls);
        }

        [Fact]
        public void Tracker_GatewayThrow_DoesNotCorruptCount()
        {
            var gateway = new FakeGateway { ThrowOnActive = true };
            var tracker = new DisplayRequestTracker(gateway);
            Assert.Throws<System.InvalidOperationException>(() => tracker.Acquire());
            Assert.Equal(0, tracker.ActiveCount);
            gateway.ThrowOnActive = false;
            tracker.Acquire();
            Assert.Equal(1, tracker.ActiveCount);
            Assert.Equal(new List<string> { "Active" }, gateway.Calls);
        }
    }
}
