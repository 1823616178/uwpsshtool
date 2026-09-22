using SshTool.Core.Common;
using SshTool.Core.Forwarding;
using SshTool.Core.Sessions;
using Xunit;

namespace SshTool.Core.Tests.Common
{
    // O14：退避表取值。三张表按协议各不相同，这里只固定「取值动作」的语义。
    public class BackoffTableTests
    {
        private static readonly int[] Steps = { 1, 2, 5, 10 };

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 2)]
        [InlineData(2, 5)]
        [InlineData(3, 10)]
        public void DelayFor_ReturnsStepAtIndex(int attempt, int expected)
        {
            Assert.Equal(expected, BackoffTable.DelayFor(Steps, attempt));
        }

        // 超出表长不再增长，停在最后一格。
        [Theory]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(1000)]
        public void DelayFor_ClampsPastEnd(int attempt)
        {
            Assert.Equal(10, BackoffTable.DelayFor(Steps, attempt));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(-100)]
        public void DelayFor_NegativeAttempt_TreatedAsFirst(int attempt)
        {
            Assert.Equal(1, BackoffTable.DelayFor(Steps, attempt));
        }

        [Fact]
        public void DelayFor_NullOrEmptyTable_ReturnsZero()
        {
            Assert.Equal(0, BackoffTable.DelayFor(null, 0));
            Assert.Equal(0, BackoffTable.DelayFor(new int[0], 3));
        }

        // 抽取之后，两处调用方的实际序列必须与抽取前一致。
        [Fact]
        public void ReconnectScheduler_SequenceUnchanged()
        {
            var seen = new[]
            {
                ReconnectScheduler.DelaySeconds(0), ReconnectScheduler.DelaySeconds(1),
                ReconnectScheduler.DelaySeconds(2), ReconnectScheduler.DelaySeconds(3),
                ReconnectScheduler.DelaySeconds(4), ReconnectScheduler.DelaySeconds(5),
                ReconnectScheduler.DelaySeconds(6)
            };
            Assert.Equal(new[] { 1, 2, 5, 10, 20, 30, 30 }, seen);
        }

        [Fact]
        public void TunnelManager_TableUnchanged()
        {
            Assert.Equal(new[] { 1, 2, 5, 10, 20, 30, 60 }, TunnelManager.BackoffSeconds);
        }
    }
}
