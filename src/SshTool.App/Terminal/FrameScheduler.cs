using System;
using SshTool.App.Infrastructure;
using SshTool.Core.Terminal;
using Windows.UI.Xaml.Media;

namespace SshTool.App.Terminal
{
    // T04：CompositionTarget.Rendering 帧源 + 全局调度器（01-DESIGN §7.2）。
    public sealed class FrameScheduler
    {
        private static readonly object Gate = new object();
        private static FrameScheduler _instance;

        private readonly RenderingTicker _ticker = new RenderingTicker();
        private readonly FrameSchedulerCore _core;

        private FrameScheduler()
        {
            _core = new FrameSchedulerCore(_ticker, true,
                FrameSchedulerCore.DefaultIdleFrames,
                FrameSchedulerCore.DefaultBlinkPeriodMs,
                () => Environment.TickCount);
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
