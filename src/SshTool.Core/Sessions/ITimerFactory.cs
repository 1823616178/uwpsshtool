using System;

namespace SshTool.Core.Sessions
{
    public interface ITimerFactory
    {
        IDisposable Schedule(int delayMs, Action callback);
        IDisposable SchedulePeriodic(int periodMs, Action callback);
    }

    public sealed class ManualTimer : IDisposable
    {
        public ManualTimer(Action callback, int delayMs, bool periodic)
        {
            Callback = callback;
            DelayMs = delayMs;
            Periodic = periodic;
        }

        public Action Callback { get; private set; }
        public int DelayMs { get; private set; }
        public bool Periodic { get; private set; }
        public bool Disposed { get; private set; }

        public void Fire()
        {
            if (!Disposed && Callback != null)
            {
                Callback();
            }
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    public sealed class ManualTimerFactory : ITimerFactory
    {
        public readonly System.Collections.Generic.List<ManualTimer> Timers =
            new System.Collections.Generic.List<ManualTimer>();

        public IDisposable Schedule(int delayMs, Action callback)
        {
            var timer = new ManualTimer(callback, delayMs, false);
            Timers.Add(timer);
            return timer;
        }

        public IDisposable SchedulePeriodic(int periodMs, Action callback)
        {
            var timer = new ManualTimer(callback, periodMs, true);
            Timers.Add(timer);
            return timer;
        }

        public void FirePending()
        {
            ManualTimer[] snapshot = Timers.ToArray();
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (!snapshot[i].Disposed)
                {
                    snapshot[i].Fire();
                }
            }
        }
    }
}
