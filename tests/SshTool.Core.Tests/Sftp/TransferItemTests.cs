using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Sftp;
using Xunit;

namespace SshTool.Core.Tests.Sftp
{
    // F02：队列项状态机与速率滑动平均。
    public class TransferItemTests
    {
        private sealed class FakeClock
        {
            public DateTime Now = new DateTime(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc);

            public DateTime Get()
            {
                return Now;
            }

            public void Advance(TimeSpan span)
            {
                Now = Now.Add(span);
            }
        }

        private static TransferItem Upload(FakeClock clock)
        {
            return TransferItem.CreateUpload("/a/f.bin", "f.bin", 1000,
                (progress, ct) => Task.FromResult(SftpResult.Success()), () => clock.Get());
        }

        [Fact]
        public void NewItem_IsQueuedWithNormalizedPath()
        {
            var clock = new FakeClock();
            TransferItem item = TransferItem.CreateDownload("/a//b/../f.bin", "f.bin", 500,
                (progress, ct) => Task.FromResult(SftpResult.Success()), () => clock.Get());
            Assert.Equal(TransferState.Queued, item.State);
            Assert.Equal("/a/f.bin", item.RemotePath);
            Assert.Equal(TransferDirection.Download, item.Direction);
            Assert.Equal(0, item.RetryCount);
            Assert.Equal(SshErrorCode.None, item.ErrorCode);
            Assert.False(item.Completion.IsCompleted);
        }

        [Fact]
        public void Rate_NoSamples_ReturnsZero()
        {
            TransferItem item = Upload(new FakeClock());
            Assert.Equal(0, item.BytesPerSecond);
        }

        [Fact]
        public void Rate_SingleSample_ReturnsZero()
        {
            var clock = new FakeClock();
            TransferItem item = Upload(clock);
            item.SetRunning();
            item.ReportProgress(100);
            Assert.Equal(0, item.BytesPerSecond);
        }

        [Fact]
        public void Rate_TwoSamples_ReturnsBytesPerSecond()
        {
            var clock = new FakeClock();
            TransferItem item = Upload(clock);
            item.SetRunning();
            item.ReportProgress(0);
            clock.Advance(TimeSpan.FromSeconds(2));
            item.ReportProgress(1000);
            Assert.Equal(500, item.BytesPerSecond, 3);
        }

        [Fact]
        public void Rate_SlidingWindow_DropsOldSamples()
        {
            var clock = new FakeClock();
            TransferItem item = Upload(clock);
            item.SetRunning();
            item.ReportProgress(0);
            clock.Advance(TimeSpan.FromSeconds(1));
            item.ReportProgress(1000);   // 1000 B/s 段
            clock.Advance(TimeSpan.FromMilliseconds(3100)); // 首两个采样滑出 3 s 窗
            item.ReportProgress(1000);   // 无增量
            Assert.Equal(0, item.BytesPerSecond, 3);
        }

        [Fact]
        public void Rate_NegativeDelta_ClampsToZero()
        {
            var clock = new FakeClock();
            TransferItem item = Upload(clock);
            item.SetRunning();
            item.ReportProgress(1000);
            clock.Advance(TimeSpan.FromSeconds(1));
            item.ReportProgress(100); // 重试复位外的异常回退不应算出负速率
            Assert.Equal(0, item.BytesPerSecond, 3);
        }

        [Fact]
        public void Finish_Cancelled_ForcesCancelledCode()
        {
            var clock = new FakeClock();
            TransferItem item = Upload(clock);
            item.SetRunning();
            // 执行体返回成功但外层已取消 → 仍记取消。
            item.SetFinished(SftpResult.Success(), true);
            Assert.Equal(TransferState.Cancelled, item.State);
            Assert.Equal(SshErrorCode.SftpCancelled, item.ErrorCode);
            Assert.True(item.Completion.IsCompleted);
        }

        [Fact]
        public void Retry_IncrementsCountAndRequeues()
        {
            var clock = new FakeClock();
            TransferItem item = Upload(clock);
            item.SetRunning();
            item.SetFailed(SftpResult.Fail(SshErrorCode.SftpTransferFailed, "x"));
            Assert.Equal(TransferState.Failed, item.State);
            item.ResetForRetry();
            Assert.Equal(TransferState.Queued, item.State);
            Assert.Equal(1, item.RetryCount);
            Assert.Equal(SshErrorCode.None, item.ErrorCode);
        }
    }
}
