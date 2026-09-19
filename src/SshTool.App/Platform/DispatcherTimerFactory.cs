using System;
using SshTool.Core.Sessions;
using Windows.UI.Xaml;

namespace SshTool.App.Platform
{
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
            var timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(ms < 1 ? 1 : ms);
            EventHandler<object> handler = null;
            handler = (s, e) =>
            {
                if (!periodic)
                {
                    timer.Stop();
                }
                if (callback != null)
                {
                    callback();
                }
            };
            timer.Tick += handler;
            timer.Start();
            return new TimerDisposable(timer, handler);
        }

        private sealed class TimerDisposable : IDisposable
        {
            private DispatcherTimer _timer;
            private EventHandler<object> _handler;

            public TimerDisposable(DispatcherTimer timer, EventHandler<object> handler)
            {
                _timer = timer;
                _handler = handler;
            }

            public void Dispose()
            {
                if (_timer != null)
                {
                    _timer.Stop();
                    _timer.Tick -= _handler;
                    _timer = null;
                    _handler = null;
                }
            }
        }
    }
}
