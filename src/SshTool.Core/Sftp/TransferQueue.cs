using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SshTool.Core.Common;

namespace SshTool.Core.Sftp
{
    // F02：传输队列（01-DESIGN.md §11.1：进度、速率、取消、失败重试、断点续传）。
    //
    // 并发恒为 1（单泵串行：SFTP 会话在传输中独占 I/O 线程，多路并行无收益
    // 且 native 侧 EAGAIN 切片会互相挤占，见 F01 线程契约）。
    // 调度：Enqueue 入队即确保泵运转；泵按 FIFO 取首个 Queued 执行；
    // 无 Queued 时泵退出（下次 Enqueue/Retry 再起）。
    // 取消：Queued 项直接标 Cancelled；Running 项经 CTS（执行体须响应 ct，
    // NativeSftpClient 在块间检查并 Cancel 飞行块）。
    // 重试：仅 Failed/Cancelled 可 Retry（回 Queued，RetryCount+1；BytesDone
    // 保留供 UI 显示，执行体按 resumeOffset 自行决定续传起点）。
    // 事件 ItemChanged 在状态变迁与进度上报时触发（进度已由执行体按
    // ProgressThrottleMs 节流）；订阅方如需碰 UI 须自行封送。
    public sealed class TransferQueue : IDisposable
    {
        private readonly object _sync = new object();
        private readonly List<TransferItem> _items = new List<TransferItem>();
        private Task _pump;
        private CancellationTokenSource _runningCts;
        private TransferItem _runningItem;
        private bool _disposed;

        public event EventHandler<TransferItem> ItemChanged;

        // 快照（UI 绑定用；返回拷贝，遍历安全）。
        public IReadOnlyList<TransferItem> Snapshot()
        {
            lock (_sync)
            {
                return new List<TransferItem>(_items).AsReadOnly();
            }
        }

        public void Enqueue(TransferItem item)
        {
            if (item == null)
            {
                throw new ArgumentNullException("item");
            }
            lock (_sync)
            {
                ThrowIfDisposed();
                _items.Add(item);
                EnsurePumpLocked();
            }
            RaiseChanged(item);
        }

        // 取消：Queued→Cancelled（true）；Running→发 CTS（true，落定为
        // Cancelled 仍经泵收尾）；终态返回 false。
        public bool Cancel(string id)
        {
            TransferItem item = null;
            CancellationTokenSource cts = null;
            lock (_sync)
            {
                item = FindLocked(id);
                if (item == null)
                {
                    return false;
                }
                if (item.State == TransferState.Queued)
                {
                    item.SetCancelledBeforeStart();
                }
                else if (item.State == TransferState.Running && item == _runningItem)
                {
                    cts = _runningCts;
                }
                else
                {
                    return false;
                }
            }
            if (cts != null)
            {
                cts.Cancel();
                return true;
            }
            RaiseChanged(item);
            return true;
        }

        // 重试：Failed/Cancelled→Queued（RetryCount+1）并确保泵运转。
        public bool Retry(string id)
        {
            TransferItem item;
            lock (_sync)
            {
                ThrowIfDisposed();
                item = FindLocked(id);
                if (item == null)
                {
                    return false;
                }
                if (item.State != TransferState.Failed && item.State != TransferState.Cancelled)
                {
                    return false;
                }
                item.ResetForRetry();
                EnsurePumpLocked();
            }
            RaiseChanged(item);
            return true;
        }

        // 清理终态项（Completed/Failed/Cancelled），返回清理个数。
        public int ClearFinished()
        {
            lock (_sync)
            {
                int removed = 0;
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    TransferState state = _items[i].State;
                    if (state == TransferState.Completed || state == TransferState.Failed
                        || state == TransferState.Cancelled)
                    {
                        _items.RemoveAt(i);
                        removed++;
                    }
                }
                return removed;
            }
        }

        public void Dispose()
        {
            List<TransferItem> queued;
            CancellationTokenSource cts;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                queued = new List<TransferItem>();
                foreach (TransferItem item in _items)
                {
                    if (item.State == TransferState.Queued)
                    {
                        item.SetCancelledBeforeStart();
                        queued.Add(item);
                    }
                }
                cts = _runningCts;
            }
            if (cts != null)
            {
                cts.Cancel();
            }
            foreach (TransferItem item in queued)
            {
                RaiseChanged(item);
            }
        }

        // ---- 泵（并发 1：同一时刻最多一个 _pump 任务存活） ----

        private void EnsurePumpLocked()
        {
            if (_disposed)
            {
                return;
            }
            if (_pump != null && !_pump.IsCompleted)
            {
                return;
            }
            _pump = Task.Run(() => PumpAsync());
        }

        private TransferItem TakeNextLocked()
        {
            foreach (TransferItem item in _items)
            {
                if (item.State == TransferState.Queued)
                {
                    return item;
                }
            }
            return null;
        }

        private async Task PumpAsync()
        {
            for (;;)
            {
                TransferItem item;
                CancellationTokenSource cts;
                lock (_sync)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    item = TakeNextLocked();
                    if (item == null)
                    {
                        return;
                    }
                    cts = new CancellationTokenSource();
                    _runningCts = cts;
                    _runningItem = item;
                    item.SetRunning();
                }
                RaiseChanged(item);
                await RunOneAsync(item, cts.Token).ConfigureAwait(false);
                lock (_sync)
                {
                    _runningCts = null;
                    _runningItem = null;
                }
                cts.Dispose();
            }
        }

        private async Task RunOneAsync(TransferItem item, CancellationToken token)
        {
            SftpResult result;
            bool cancelled;
            try
            {
                var progress = new ProgressForItem(this, item);
                result = await item.Execute(progress, token).ConfigureAwait(false);
                if (result == null)
                {
                    result = SftpResult.Fail(SshErrorCode.Unknown, string.Empty);
                }
                cancelled = token.IsCancellationRequested
                    || result.Code == SshErrorCode.SftpCancelled;
            }
            catch (OperationCanceledException)
            {
                result = SftpResult.Fail(SshErrorCode.SftpCancelled, string.Empty);
                cancelled = true;
            }
            catch (Exception ex)
            {
                // 执行体异常（IO 错误等）→失败项，不炸泵。只记类型名（脱敏）。
                result = SftpResult.Fail(SshErrorCode.SftpTransferFailed, ex.GetType().Name);
                cancelled = false;
            }
            lock (_sync)
            {
                if (cancelled)
                {
                    item.SetFinished(result, true);
                }
                else if (result.Ok)
                {
                    item.SetFinished(result, false);
                }
                else
                {
                    item.SetFailed(result);
                }
            }
            RaiseChanged(item);
        }

        private TransferItem FindLocked(string id)
        {
            foreach (TransferItem item in _items)
            {
                if (item.Id == id)
                {
                    return item;
                }
            }
            return null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("TransferQueue");
            }
        }

        private void RaiseChanged(TransferItem item)
        {
            EventHandler<TransferItem> handler = ItemChanged;
            if (handler != null)
            {
                handler(this, item);
            }
        }

        // 执行体的进度回调：更新 BytesDone/速率并转发 ItemChanged。
        private sealed class ProgressForItem : IProgress<SftpProgress>
        {
            private readonly TransferQueue _queue;
            private readonly TransferItem _item;

            public ProgressForItem(TransferQueue queue, TransferItem item)
            {
                _queue = queue;
                _item = item;
            }

            public void Report(SftpProgress value)
            {
                if (value == null)
                {
                    return;
                }
                lock (_queue._sync)
                {
                    if (_item.State != TransferState.Running)
                    {
                        return;
                    }
                    _item.ReportProgress(value.BytesDone);
                }
                _queue.RaiseChanged(_item);
            }
        }
    }
}
