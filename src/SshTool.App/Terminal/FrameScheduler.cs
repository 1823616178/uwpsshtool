using System;
using SshTool.App.Infrastructure;
using SshTool.Core.Terminal;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace SshTool.App.Terminal
{
    // T04：CompositionTarget.Rendering 帧源 + 全局调度器（01-DESIGN §7.2）。
    public sealed class FrameScheduler
    {
        private static readonly object Gate = new object();
        private static FrameScheduler _instance;

        private readonly RenderingTicker _ticker = new RenderingTicker();
        private readonly DispatcherBlinkTimer _blinkTimer = new DispatcherBlinkTimer();
        private readonly FrameSchedulerCore _core;

        private FrameScheduler()
        {
            _core = new FrameSchedulerCore(_ticker, true,
                FrameSchedulerCore.DefaultIdleFrames,
                FrameSchedulerCore.DefaultBlinkPeriodMs,
                () => Environment.TickCount,
                _blinkTimer);
        }

        public static FrameScheduler Instance
        {
            get
            {
                lock (Gate)
                {
                    if (_instance == null)
                    {
                        _instance = new FrameScheduler();
                    }
                    return _instance;
                }
            }
        }

        public FrameSchedulerCore Core { get { return _core; } }

        public void Register(string id, FrameCallback callback)
        {
            DispatcherHelper.Post(() => _core.Register(id, callback));
        }

        // opt/full-pass：wantsBlink 为 true 时，ticker 空闲停下后由闪烁定时器驱动光标闪烁
        public void Register(string id, FrameCallback callback, Func<bool> wantsBlink)
        {
            DispatcherHelper.Post(() => _core.Register(id, callback, wantsBlink));
        }

        public bool Unregister(string id)
        {
            if (DispatcherHelper.HasThreadAccess)
            {
                return _core.Unregister(id);
            }
            DispatcherHelper.Post(() => _core.Unregister(id));
            return true;
        }

        public void SetVisible(string id, bool visible)
        {
            DispatcherHelper.Post(() => _core.SetVisible(id, visible));
        }

        public void Wake()
        {
            DispatcherHelper.Post(() => _core.Wake());
        }

        // opt/full-pass：一次性 DispatcherTimer（UI 线程触发，与 CompositionTarget.Rendering 同线程）。
        // 单例调度器持有，懒创建：首次 Start 一定发生在 UI 线程（帧回调/调度器 Post 内）。
        private sealed class DispatcherBlinkTimer : IBlinkTimer
        {
            private DispatcherTimer _timer;
            private Action _callback;
            private bool _subscribed;

            public void Start(int dueMs, Action callback)
            {
                if (_timer == null)
                {
                    _timer = new DispatcherTimer();
                }
                Stop();
                _callback = callback;
                _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, dueMs));
                _timer.Tick += OnTick;
                _subscribed = true;
                _timer.Start();
            }

            public void Stop()
            {
                if (_timer == null)
                {
                    return;
                }
                _timer.Stop();
                if (_subscribed)
                {
                    _timer.Tick -= OnTick;
                    _subscribed = false;
                }
            }

            private void OnTick(object sender, object e)
            {
                Action callback = _callback;
                Stop(); // 一次性
                if (callback != null)
                {
                    callback();
                }
            }
        }

        private sealed class RenderingTicker : IFrameTicker
        {
            private Action _callback = () => { };
            private bool _running;

            public void SetFrameCallback(Action callback)
            {
                _callback = callback ?? (() => { });
            }

            public void Start()
            {
                if (_running)
                {
                    return;
                }
                _running = true;
                CompositionTarget.Rendering += OnRendering;
            }

            public void Stop()
            {
                if (!_running)
                {
                    return;
                }
                _running = false;
                CompositionTarget.Rendering -= OnRendering;
            }

            private void OnRendering(object sender, object e)
            {
                _callback();
            }
        }
    }
}
