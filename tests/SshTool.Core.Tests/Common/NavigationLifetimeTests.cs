using System;
using System.Collections.Generic;
using SshTool.Core.Common;
using Xunit;

namespace SshTool.Core.Tests.Common
{
    // R01 (C-02)：页面导航世代。OnNavigatedTo Begin 开新世代，OnNavigatedFrom End
    // 取消 Token 并按注册反序执行 Track 的拆除动作；End 幂等。
    public class NavigationLifetimeTests
    {
        [Fact]
        public void Begin_ReturnsIncreasingGenerations()
        {
            var life = new NavigationLifetime();
            int first = life.Begin();
            int second = life.Begin();
            Assert.True(second > first);
        }

        [Fact]
        public void IsCurrent_TrueOnlyForActiveGeneration()
        {
            var life = new NavigationLifetime();
            int generation = life.Begin();
            Assert.True(life.IsCurrent(generation));
            Assert.False(life.IsCurrent(generation + 1));
        }

        [Fact]
        public void End_MakesGenerationNotCurrent()
        {
            var life = new NavigationLifetime();
            int generation = life.Begin();
            life.End();
            Assert.False(life.IsCurrent(generation));
        }

        [Fact]
        public void End_RunsTeardownsInReverseOrder()
        {
            var life = new NavigationLifetime();
            life.Begin();
            var order = new List<string>();
            life.Track(() => order.Add("first"));
            life.Track(() => order.Add("second"));
            life.Track(() => order.Add("third"));
            life.End();
            Assert.Equal(new[] { "third", "second", "first" }, order);
        }

        [Fact]
        public void End_IsIdempotent_TeardownsRunOnce()
        {
            var life = new NavigationLifetime();
            life.Begin();
            int calls = 0;
            life.Track(() => calls++);
            life.End();
            life.End();
            Assert.Equal(1, calls);
        }

        [Fact]
        public void End_TeardownThrowing_DoesNotSkipRest()
        {
            var life = new NavigationLifetime();
            life.Begin();
            bool ran = false;
            life.Track(() => ran = true);
            life.Track(() => { throw new InvalidOperationException(); });
            life.End();
            Assert.True(ran);
        }

        [Fact]
        public void BeginAfterEnd_StartsFreshState()
        {
            var life = new NavigationLifetime();
            int old = life.Begin();
            int staleCalls = 0;
            life.Track(() => staleCalls++);
            life.End();

            int current = life.Begin();
            Assert.False(life.IsCurrent(old));
            Assert.True(life.IsCurrent(current));
            Assert.False(life.Token.IsCancellationRequested);
            // 旧世代的拆除动作已随 End 清空，不带入新世代。
            life.End();
            Assert.Equal(1, staleCalls);
        }

        [Fact]
        public void End_CancelsToken()
        {
            var life = new NavigationLifetime();
            life.Begin();
            Assert.False(life.Token.IsCancellationRequested);
            life.End();
            Assert.True(life.Token.IsCancellationRequested);
        }

        [Fact]
        public void Track_AfterEnd_RunsImmediately()
        {
            var life = new NavigationLifetime();
            life.Begin();
            life.End();
            bool ran = false;
            life.Track(() => ran = true);
            Assert.True(ran);
        }

        [Fact]
        public void Begin_WithoutExplicitEnd_RunsPreviousTeardownsOnce()
        {
            var life = new NavigationLifetime();
            life.Begin();
            int calls = 0;
            life.Track(() => calls++);
            // Begin 隐含一次 End：旧世代拆除动作执行且只执行一次。
            life.Begin();
            life.End();
            Assert.Equal(1, calls);
        }

        [Fact]
        public void Track_Null_IsIgnored()
        {
            var life = new NavigationLifetime();
            life.Begin();
            life.Track(null);
            // 静默忽略不抛；End 同样正常。
            life.End();
        }
    }
}
