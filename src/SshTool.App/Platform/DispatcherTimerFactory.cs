using System;
using SshTool.App.Infrastructure;
using SshTool.Core.Sessions;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace SshTool.App.Platform
{
    // code-review-pass：Core 的 SessionManager / TunnelManager / SyncCoordinator / SyncTriggers
    // 遵循 ConfigureAwait(false) 纪律，Schedule 与 Dispose 经常在线程池或 native 完成线程上调用
    // （如重连成功后 CancelTimer、同步出错后排重试、标脏后排防抖）。DispatcherTimer 有 UI 线程
    // 亲和性，跨线程 new/Start/Stop 会抛 RPC_E_WRONG_THREAD，异常落进各自的 catch 或未观察任务，
    // 表现为「重试/防抖/重连计时器没排上」。这里改为：在 UI 线程就地建；否则封送回 UI 线程建，
    // Dispose 同理。回调始终在 UI 线程触发（与原行为一致）。
    public sealed class DispatcherTimerFactory : ITimerFactory
    {
        public IDisposable Schedule(int delayMs, Action callback)
        {
            return Start(delayMs, callback, false);
        }

        public IDisposable SchedulePeriodic(int periodMs, Action callback)
        {
            return Start(periodMs, callback, true);
        }

        private static IDisposable Start(int ms, Action callback, bool periodic)
        {
            var handle = new TimerHandle(ms < 1 ? 1 : ms, callback, periodic);
            RunOnUi(handle.Arm);
            return handle;
        }

        // 调度器尚不可用（启动极早期，仍在 UI 线程上）时按原行为就地执行。
        private static void RunOnUi(Action action)
        {
            CoreDispatcher dispatcher = DispatcherHelper.Dispatcher;
            if (dispatcher == null || dispatcher.HasThreadAccess)
            {
                action();
            }
            else
            {
                DispatcherHelper.Post(action);
            }
        }

        private sealed class TimerHandle : IDisposable
        {
            private readonly object _gate = new object();
            private readonly int _ms;
            private readonly bool _periodic;
            private Action _callback;
            private DispatcherTimer _timer;
            private bool _disposed;

            public TimerHandle(int ms, Action callback, bool periodic)
            {
                _ms = ms;
                _callback = callback;
                _periodic = periodic;
            }

            // 仅在 UI 线程调用。
            public void Arm()
            {
                lock (_gate)
                {
                    if (_disposed || _timer != null)
                    {
                        return;
                    }
                }
                var timer = new DispatcherTimer();
                timer.Interval = TimeSpan.FromMilliseconds(_ms);
                timer.Tick += OnTick;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        // 封送途中已被 Dispose：不启动。
                        timer.Tick -= OnTick;
                        return;
                    }
                    _timer = timer;
                }
                timer.Start();
            }

            private void OnTick(object sender, object e)
            {
                Action callback;
                DispatcherTimer timer;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    callback = _callback;
                    timer = _timer;
                }
                if (!_periodic && timer != null)
                {
                    timer.Stop();
                }
                if (callback != null)
                {
                    callback();
                }
            }

            public void Dispose()
            {
                DispatcherTimer timer;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    _disposed = true;
                    timer = _timer;
                    _timer = null;
                    _callback = null;
                }
                if (timer == null)
                {
                    return;
                }
                RunOnUi(() =>
                {
                    timer.Stop();
                    timer.Tick -= OnTick;
                });
            }
        }
    }
}
