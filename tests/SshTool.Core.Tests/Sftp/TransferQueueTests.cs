using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Sftp;
using Xunit;

namespace SshTool.Core.Tests.Sftp
{
    // F02：传输队列状态机（排队/进行/完成/失败/取消，并发 1，失败重试）。
    public class TransferQueueTests : IDisposable
    {
        private readonly TransferQueue _queue = new TransferQueue();

        public void Dispose()
        {
            _queue.Dispose();
        }

        private static TransferItem QuickItem(SshErrorCode code, string id = null)
        {
            SftpResult result = code == SshErrorCode.None
                ? SftpResult.Success()
                : SftpResult.Fail(code, "boom");
            return TransferItem.CreateUpload("/r/f", "f", 10,
                (progress, ct) => Task.FromResult(result));
        }

        private static async Task<SftpResult> WaitDone(TransferItem item)
        {
            Task completed = await Task.WhenAny(item.Completion, Task.Delay(5000));
            Assert.Same(item.Completion, completed);
            return await item.Completion;
        }

        [Fact]
        public async Task Enqueue_Success_Completes()
        {
            TransferItem item = QuickItem(SshErrorCode.None);
            _queue.Enqueue(item);
            Assert.Equal(TransferState.Queued, item.State); // 入队瞬间（泵异步起）
            SftpResult result = await WaitDone(item);
            Assert.True(result.Ok);
            Assert.Equal(TransferState.Completed, item.State);
            Assert.Single(_queue.Snapshot());
        }

        [Fact]
        public async Task Enqueue_Failure_MarksFailed()
        {
            TransferItem item = QuickItem(SshErrorCode.SftpTransferFailed);
            _queue.Enqueue(item);
            await WaitDone(item);
            Assert.Equal(TransferState.Failed, item.State);
            Assert.Equal(SshErrorCode.SftpTransferFailed, item.ErrorCode);
            Assert.Equal("boom", item.ErrorMessage);
        }

        [Fact]
        public async Task Execute_Throws_MarksFailedWithoutKillingPump()
        {
            TransferItem bad = TransferItem.CreateUpload("/r/a", "a", 1,
                (progress, ct) => { throw new InvalidOperationException("io"); });
            TransferItem good = QuickItem(SshErrorCode.None);
            _queue.Enqueue(bad);
            _queue.Enqueue(good);
            await WaitDone(bad);
            Assert.Equal(TransferState.Failed, bad.State);
            Assert.Equal(SshErrorCode.SftpTransferFailed, bad.ErrorCode);
            await WaitDone(good);
            Assert.Equal(TransferState.Completed, good.State);
        }

        [Fact]
        public async Task Execute_ReturnsNull_MarksFailed()
        {
            TransferItem item = TransferItem.CreateUpload("/r/a", "a", 1,
                (progress, ct) => Task.FromResult<SftpResult>(null));
            _queue.Enqueue(item);
            await WaitDone(item);
            Assert.Equal(TransferState.Failed, item.State);
        }

        [Fact]
        public async Task Cancel_Queued_NeverRuns()
        {
            var gate = new TaskCompletionSource<bool>();
            TransferItem first = TransferItem.CreateUpload("/r/1", "1", 1,
                async (progress, ct) =>
                {
                    await gate.Task;
                    return SftpResult.Success();
                });
            bool secondRan = false;
            TransferItem second = TransferItem.CreateUpload("/r/2", "2", 1,
                (progress, ct) =>
                {
                    secondRan = true;
                    return Task.FromResult(SftpResult.Success());
                });
            _queue.Enqueue(first);
            _queue.Enqueue(second);
            Assert.True(_queue.Cancel(second.Id));
            await Task.Delay(100);
            Assert.Equal(TransferState.Cancelled, second.State);
            Assert.Equal(SshErrorCode.SftpCancelled, second.ErrorCode);
            gate.TrySetResult(true);
            await WaitDone(first);
            Assert.False(secondRan);
        }

        [Fact]
        public async Task Cancel_Running_MarksCancelled()
        {
            TransferItem item = TransferItem.CreateUpload("/r/f", "f", 1,
                async (progress, ct) =>
                {
                    await Task.Delay(30000, ct);
                    return SftpResult.Success();
                });
            _queue.Enqueue(item);
            // 等泵真正起跑再取消。
            for (int i = 0; i < 100 && item.State != TransferState.Running; i++)
            {
                await Task.Delay(20);
            }
            Assert.Equal(TransferState.Running, item.State);
            Assert.True(_queue.Cancel(item.Id));
            await WaitDone(item);
            Assert.Equal(TransferState.Cancelled, item.State);
        }

        [Fact]
        public async Task Concurrency_IsOne_SecondWaitsForFirst()
        {
            var releaseFirst = new TaskCompletionSource<bool>();
            bool secondStartedWhileFirstRunning = false;
            TransferItem first = null;
            TransferItem second = null;
            first = TransferItem.CreateUpload("/r/1", "1", 1,
                async (progress, ct) =>
                {
                    await releaseFirst.Task;
                    return SftpResult.Success();
                });
            second = TransferItem.CreateUpload("/r/2", "2", 1,
                (progress, ct) =>
                {
                    secondStartedWhileFirstRunning = (first.State == TransferState.Running);
                    return Task.FromResult(SftpResult.Success());
                });
            _queue.Enqueue(first);
            _queue.Enqueue(second);
            for (int i = 0; i < 100 && first.State != TransferState.Running; i++)
            {
                await Task.Delay(20);
            }
            Assert.Equal(TransferState.Running, first.State);
            Assert.Equal(TransferState.Queued, second.State);
            releaseFirst.TrySetResult(true);
            await WaitDone(first);
            await WaitDone(second);
            Assert.False(secondStartedWhileFirstRunning);
            Assert.Equal(TransferState.Completed, first.State);
            Assert.Equal(TransferState.Completed, second.State);
        }

        [Fact]
        public async Task Fifo_OrderIsEnqueueOrder()
        {
            var order = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                string name = "f" + i;
                _queue.Enqueue(TransferItem.CreateUpload("/r/" + name, name, 1,
                    (progress, ct) =>
                    {
                        lock (order)
                        {
                            order.Add(name);
                        }
                        return Task.FromResult(SftpResult.Success());
                    }));
            }
            foreach (TransferItem item in _queue.Snapshot())
            {
                await WaitDone(item);
            }
            Assert.Equal(new List<string> { "f0", "f1", "f2" }, order);
        }

        [Fact]
        public async Task Retry_Failed_RerunsAndCompletes()
        {
            int calls = 0;
            TransferItem item = TransferItem.CreateUpload("/r/f", "f", 1,
                (progress, ct) =>
                {
                    calls++;
                    return Task.FromResult(calls < 2
                        ? SftpResult.Fail(SshErrorCode.SftpTransferFailed, "x")
                        : SftpResult.Success());
                });
            _queue.Enqueue(item);
            await WaitDone(item);
            Assert.Equal(TransferState.Failed, item.State);
            Assert.True(_queue.Retry(item.Id));
            Assert.Equal(1, item.RetryCount);
            Assert.Equal(TransferState.Queued, item.State);
            await WaitDone(item);
            Assert.Equal(TransferState.Completed, item.State);
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task Retry_NonFailed_ReturnsFalse()
        {
            TransferItem item = QuickItem(SshErrorCode.None);
            _queue.Enqueue(item);
            Assert.False(_queue.Retry(item.Id)); // Running/Queued 不可重试
            await WaitDone(item);
            Assert.False(_queue.Retry(item.Id)); // Completed 不可重试
        }

        [Fact]
        public async Task Progress_UpdatesBytesDoneAndFiresChanged()
        {
            int changed = 0;
            _queue.ItemChanged += (sender, changedItem) => { changed++; };
            TransferItem item = TransferItem.CreateUpload("/r/f", "f", 100,
                (progress, ct) =>
                {
                    progress.Report(new SftpProgress(40, 100));
                    progress.Report(new SftpProgress(100, 100));
                    return Task.FromResult(SftpResult.Success());
                });
            _queue.Enqueue(item);
            await WaitDone(item);
            Assert.Equal(100, item.BytesDone);
            Assert.True(changed >= 4); // 入队/起跑/2×进度/完成
        }

        [Fact]
        public void ClearFinished_RemovesTerminalItems()
        {
            TransferItem done = QuickItem(SshErrorCode.None);
            _queue.Enqueue(done);
            SpinUntil(() => done.State == TransferState.Completed);
            Assert.Equal(1, _queue.ClearFinished());
            Assert.Empty(_queue.Snapshot());
        }

        private static void SpinUntil(Func<bool> condition)
        {
            for (int i = 0; i < 250 && !condition(); i++)
            {
                Thread.Sleep(20);
            }
            Assert.True(condition());
        }
    }
}
