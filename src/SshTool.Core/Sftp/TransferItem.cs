using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SshTool.Core.Common;

namespace SshTool.Core.Sftp
{
    public enum TransferDirection
    {
        Upload = 0,
        Download = 1
    }

    public enum TransferState
    {
        Queued = 0,
        Running = 1,
        Completed = 2,
        Failed = 3,
        Cancelled = 4
    }

    // F02：速率滑动平均（01-DESIGN.md §11.1 传输队列：进度、速率）。
    // 保留最近窗口内的 (时间, 累计字节) 采样，速率 = 首尾差/时间差；
    // 采样不足 2 个或时间差为 0 时返回 0。时钟可注入，单测用假时钟。
    internal sealed class TransferRateEstimator
    {
        // 滑动窗口 3 s（与进度节流 200 ms 配合，约 15 个采样）。
        public const int WindowMs = 3000;

        private readonly Func<DateTime> _clock;
        private readonly Queue<RateSample> _samples = new Queue<RateSample>();

        internal struct RateSample
        {
            public DateTime At;
            public long BytesDone;
        }

        public TransferRateEstimator(Func<DateTime> clock)
        {
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public TransferRateEstimator()
            : this(null)
        {
        }

        public void AddSample(long bytesDone)
        {
            DateTime now = _clock();
            _samples.Enqueue(new RateSample { At = now, BytesDone = bytesDone });
            Trim(now);
        }

        public void Clear()
        {
            _samples.Clear();
        }

        public double BytesPerSecond
        {
            get
            {
                if (_samples.Count < 2)
                {
                    return 0;
                }
                Trim(_clock());
                if (_samples.Count < 2)
                {
                    return 0;
                }
                RateSample[] items = _samples.ToArray();
                RateSample first = items[0];
                RateSample last = items[items.Length - 1];
                double seconds = (last.At - first.At).TotalSeconds;
                if (seconds <= 0)
                {
                    return 0;
                }
                long delta = last.BytesDone - first.BytesDone;
                if (delta < 0)
                {
                    delta = 0;
                }
                return delta / seconds;
            }
        }

        private void Trim(DateTime now)
        {
            while (_samples.Count > 0)
            {
                RateSample head = _samples.Peek();
                if ((now - head.At).TotalMilliseconds <= WindowMs)
                {
                    break;
                }
                _samples.Dequeue();
            }
        }
    }

    // F02：传输队列项（02-UI-DESIGN.md §5.17 传输面板：名称、方向、进度条、
    // 速率、[取消]/[重试] 的数据底座）。
    //
    // 执行体由创建方注入（F03 传入开关 StorageFile 流 + ISftpClient 调用的
    // 闭包），队列只负责状态机与调度——Core 不接触 UWP 存储类型。
    // 状态机：Queued→Running→Completed/Failed/Cancelled；
    // Failed/Cancelled→Queued 仅经 TransferQueue.Retry（RetryCount+1）。
    public sealed class TransferItem
    {
        private readonly Func<IProgress<SftpProgress>, CancellationToken, Task<SftpResult>> _execute;
        private readonly TransferRateEstimator _estimator;
        // 单次生命周期的完成信号：Retry 时重建（旧引用者保留旧 Task）。
        private TaskCompletionSource<SftpResult> _completion =
            new TaskCompletionSource<SftpResult>();

        private TransferItem(TransferDirection direction, string remotePath,
                             string localFileName, long totalBytes,
                             Func<IProgress<SftpProgress>, CancellationToken, Task<SftpResult>> execute,
                             Func<DateTime> clock)
        {
            Id = IdGenerator.NewId();
            Direction = direction;
            RemotePath = global::SshTool.Core.Sftp.RemotePath.Normalize(remotePath);
            LocalFileName = localFileName ?? string.Empty;
            TotalBytes = totalBytes;
            _execute = execute;
            _estimator = new TransferRateEstimator(clock);
            State = TransferState.Queued;
            ErrorCode = SshErrorCode.None;
            ErrorMessage = string.Empty;
        }

        public static TransferItem CreateUpload(string remotePath, string localFileName,
                                                long totalBytes,
                                                Func<IProgress<SftpProgress>, CancellationToken, Task<SftpResult>> execute)
        {
            return CreateUpload(remotePath, localFileName, totalBytes, execute, null);
        }

        public static TransferItem CreateDownload(string remotePath, string localFileName,
                                                  long totalBytes,
                                                  Func<IProgress<SftpProgress>, CancellationToken, Task<SftpResult>> execute)
        {
            return CreateDownload(remotePath, localFileName, totalBytes, execute, null);
        }

        internal static TransferItem CreateUpload(string remotePath, string localFileName,
                                                  long totalBytes,
                                                  Func<IProgress<SftpProgress>, CancellationToken, Task<SftpResult>> execute,
                                                  Func<DateTime> clock)
        {
            if (execute == null)
            {
                throw new ArgumentNullException("execute");
            }
            return new TransferItem(TransferDirection.Upload, remotePath, localFileName,
                                    totalBytes, execute, clock);
        }

        internal static TransferItem CreateDownload(string remotePath, string localFileName,
                                                    long totalBytes,
                                                    Func<IProgress<SftpProgress>, CancellationToken, Task<SftpResult>> execute,
                                                    Func<DateTime> clock)
        {
            if (execute == null)
            {
                throw new ArgumentNullException("execute");
            }
            return new TransferItem(TransferDirection.Download, remotePath, localFileName,
                                    totalBytes, execute, clock);
        }

        public string Id { get; }
        public TransferDirection Direction { get; }
        public string RemotePath { get; }
        public string LocalFileName { get; }
        // 总字节（-1 未知，如 Stat 失败仍坚持下载时）。
        public long TotalBytes { get; }
        public long BytesDone { get; private set; }
        public TransferState State { get; private set; }
        public SshErrorCode ErrorCode { get; private set; }
        public string ErrorMessage { get; private set; }
        public int RetryCount { get; private set; }

        public double BytesPerSecond
        {
            get { return _estimator.BytesPerSecond; }
        }

        // 完成任务：Completed/Failed/Cancelled 落定时带 SftpResult。
        public Task<SftpResult> Completion
        {
            get { return _completion.Task; }
        }

        internal Func<IProgress<SftpProgress>, CancellationToken, Task<SftpResult>> Execute
        {
            get { return _execute; }
        }

        internal void SetRunning()
        {
            State = TransferState.Running;
            ErrorCode = SshErrorCode.None;
            ErrorMessage = string.Empty;
            _estimator.Clear();
        }

        internal void ReportProgress(long bytesDone)
        {
            BytesDone = bytesDone;
            _estimator.AddSample(bytesDone);
        }

        internal void SetFinished(SftpResult result, bool cancelled)
        {
            if (cancelled)
            {
                ErrorCode = SshErrorCode.SftpCancelled;
                ErrorMessage = result != null ? result.Message : string.Empty;
                State = TransferState.Cancelled;
            }
            else
            {
                ErrorCode = result != null ? result.Code : SshErrorCode.None;
                ErrorMessage = result != null ? result.Message : string.Empty;
                State = TransferState.Completed;
            }
            // 完成时速率定格：保留窗口内采样即可（不再追加 0 速采样）。
            _completion.TrySetResult(result ?? SftpResult.Success());
        }

        internal void SetFailed(SftpResult result)
        {
            ErrorCode = result != null ? result.Code : SshErrorCode.Unknown;
            ErrorMessage = result != null ? result.Message : string.Empty;
            State = TransferState.Failed;
            _completion.TrySetResult(result ?? SftpResult.Fail(SshErrorCode.Unknown, string.Empty));
        }

        internal void SetCancelledBeforeStart()
        {
            ErrorCode = SshErrorCode.SftpCancelled;
            ErrorMessage = string.Empty;
            State = TransferState.Cancelled;
            _completion.TrySetResult(SftpResult.Fail(SshErrorCode.SftpCancelled, string.Empty));
        }

        internal void ResetForRetry()
        {
            RetryCount++;
            State = TransferState.Queued;
            ErrorCode = SshErrorCode.None;
            ErrorMessage = string.Empty;
            _completion = new TaskCompletionSource<SftpResult>();
            _estimator.Clear();
        }
    }
}
