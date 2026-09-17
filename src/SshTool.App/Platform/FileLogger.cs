using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SshTool.Core.Common;
using Windows.Storage;

namespace SshTool.App.Platform
{
    // X04：后台队列写 LocalFolder/logs/app.log；写前必过 LogRedactor（01-DESIGN.md §12.2）。
    // 启动时按 LogRotationPlanner 滚动（1 MiB / 保留 3）。
    public sealed class FileLogger : ILogger
    {
        private static readonly Lazy<FileLogger> LazyInstance = new Lazy<FileLogger>(() => new FileLogger());

        private readonly object _gate = new object();
        private readonly Queue<string> _pending = new Queue<string>();
        private bool _flushing;
        private bool _initialized;
        private TaskCompletionSource<bool> _idle;

        private FileLogger()
        {
        }

        public static FileLogger Instance
        {
            get { return LazyInstance.Value; }
        }

        public LogLevel MinLevel { get; set; } = LogLevel.Debug;

        public void Log(LogLevel level, string tag, string message)
        {
            if (level < MinLevel)
            {
                return;
            }
            var line = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] [{2}] {3}",
                DateTime.Now,
                level.ToString().ToUpperInvariant(),
                tag,
                LogRedactor.Redact(message ?? string.Empty));

            lock (_gate)
            {
                _pending.Enqueue(line);
                if (_flushing)
                {
                    return;
                }
                _flushing = true;
                // RunContinuationsAsynchronously：SignalIdle 在锁内完成，避免续体同步跑进锁里
                _idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            Task.Run(PumpAsync).Forget();
        }

        // 挂起前把队列写完（W10M 挂起频繁；app.log 是真机唯一诊断手段）。
        // 最多等 timeoutMs，超时就放弃，绝不拖住 SuspendingDeferral。
        public async Task FlushAsync(int timeoutMs = 2000)
        {
            Task idle;
            lock (_gate)
            {
                if (!_flushing || _idle == null)
                {
                    return;
                }
                idle = _idle.Task;
            }
            await Task.WhenAny(idle, Task.Delay(timeoutMs)).ConfigureAwait(false);
        }

        private async Task PumpAsync()
        {
            try
            {
                StorageFolder folder = await ApplicationData.Current.LocalFolder
                    .CreateFolderAsync("logs", CreationCollisionOption.OpenIfExists);
                if (!_initialized)
                {
                    _initialized = true;
                    await RotateIfNeededAsync(folder);
                }
                StorageFile file = await folder.CreateFileAsync("app.log", CreationCollisionOption.OpenIfExists);
                while (true)
                {
                    string line;
                    lock (_gate)
                    {
                        line = _pending.Count > 0 ? _pending.Dequeue() : null;
                        if (line == null)
                        {
                            _flushing = false;
                            SignalIdle();
                            return;
                        }
                    }
                    await FileIO.AppendTextAsync(file, line + "\r\n");
                }
            }
            catch
            {
                // 日志绝不能拖垮应用；失败丢弃队列。
                lock (_gate)
                {
                    _flushing = false;
                    _pending.Clear();
                    SignalIdle();
                }
            }
        }

        // 调用方已持有 _gate
        private void SignalIdle()
        {
            var idle = _idle;
            _idle = null;
            if (idle != null)
            {
                idle.TrySetResult(true);
            }
        }

        private static async Task RotateIfNeededAsync(StorageFolder folder)
        {
            var byName = new Dictionary<string, StorageFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in await folder.GetFilesAsync())
            {
                byName[f.Name] = f;
            }
            StorageFile current;
            if (!byName.TryGetValue("app.log", out current))
            {
                return;
            }
            var props = await current.GetBasicPropertiesAsync();
            var existing = new HashSet<string>(byName.Values.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var step in LogRotationPlanner.Plan(current.Path, (long)props.Size, existing))
            {
                StorageFile f;
                if (!byName.TryGetValue(System.IO.Path.GetFileName(step.SourcePath), out f))
                {
                    continue;
                }
                if (step.Type == RotationStepType.Delete)
                {
                    await f.DeleteAsync();
                }
                else
                {
                    await f.RenameAsync(System.IO.Path.GetFileName(step.TargetPath));
                }
            }
        }
    }

    internal static class TaskExtensions
    {
        // 明确丢弃任务（避免 CS4014 警告；FileLogger 内部自愈，无需观察异常）
        public static void Forget(this Task task)
        {
        }
    }
}
