using System;
using System.Collections.Generic;

namespace SshTool.Core.Terminal
{
    // T04：帧调度纯逻辑（01-DESIGN §7.2）。帧源经 IFrameTicker 注入；时钟经 Func<long> 注入。

    public delegate bool FrameCallback();

    public interface IFrameTicker
    {
        void SetFrameCallback(Action callback);
        void Start();
        void Stop();
    }

    public sealed class FrameSchedulerCore
    {
        public const int DefaultIdleFrames = 30;
        public const int DefaultBlinkPeriodMs = 530;

        private readonly IFrameTicker _ticker;
        private readonly bool _autoSleep;
        private readonly int _idleLimit;
        private readonly int _blinkPeriodMs;
        private readonly Func<long> _nowMs;
        private readonly Dictionary<string, FrameEntry> _entries = new Dictionary<string, FrameEntry>();
        private readonly List<string> _order = new List<string>();
        private bool _running;
        private int _idleStreak;
        private bool _dispatching;
        private long _lastBlinkMs;
        private bool _blinkOn = true;

        public FrameSchedulerCore(IFrameTicker ticker, bool autoSleep = true,
                                  int idleFramesBeforeSleep = DefaultIdleFrames,
                                  int blinkPeriodMs = DefaultBlinkPeriodMs,
                                  Func<long> nowMs = null)
        {
            if (ticker == null)
            {
                throw new ArgumentNullException(nameof(ticker));
            }
            _ticker = ticker;
            _autoSleep = autoSleep;
            _idleLimit = idleFramesBeforeSleep < 1 ? 1 : idleFramesBeforeSleep;
            _blinkPeriodMs = blinkPeriodMs < 1 ? 1 : blinkPeriodMs;
            _nowMs = nowMs ?? (() => 0L);
            _ticker.SetFrameCallback(OnFrame);
        }

        public int CallbackErrors { get; private set; }
        public bool BlinkOn { get { return _blinkOn; } }
        public bool IsRunning { get { return _running; } }
        public int Size { get { return _entries.Count; } }

        public void Register(string id, FrameCallback callback)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException("id 不能为空", nameof(id));
            }
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }
            if (_entries.ContainsKey(id))
            {
                throw new InvalidOperationException("重复注册 id=" + id);
            }
            _entries[id] = new FrameEntry { Callback = callback, Visible = true };
            _order.Add(id);
            Wake();
        }

        public bool Unregister(string id)
        {
            if (!_entries.Remove(id))
            {
                return false;
            }
            _order.Remove(id);
            if (_entries.Count == 0)
            {
                StopTicker();
            }
            return true;
        }

        public void SetVisible(string id, bool visible)
        {
            FrameEntry entry;
            if (!_entries.TryGetValue(id, out entry))
            {
                return;
            }
            entry.Visible = visible;
            if (visible)
            {
                Wake();
            }
        }

        public void Wake()
        {
            if (!_running && _entries.Count > 0)
            {
                _running = true;
                _idleStreak = 0;
                _lastBlinkMs = _nowMs();
                _ticker.Start();
            }
        }

        public bool Has(string id)
        {
            return _entries.ContainsKey(id);
        }

        public bool IsVisible(string id)
        {
            FrameEntry entry;
            return _entries.TryGetValue(id, out entry) && entry.Visible;
        }

        private void OnFrame()
        {
            if (_dispatching)
            {
                return;
            }
            _dispatching = true;
            bool anyWork = false;
            try
            {
                long now = _nowMs();
                bool anyVisible = false;
                for (int i = 0; i < _order.Count; i++)
                {
                    FrameEntry entry = _entries[_order[i]];
                    if (!entry.Visible)
                    {
                        continue;
                    }
                    anyVisible = true;
                    bool worked = true;
                    try
                    {
                        worked = entry.Callback();
                    }
                    catch
                    {
                        CallbackErrors++;
                    }
                    anyWork = anyWork || worked;
                }
                if (anyVisible && now - _lastBlinkMs >= _blinkPeriodMs)
                {
                    _blinkOn = !_blinkOn;
                    _lastBlinkMs = now;
                    anyWork = true;
                }
            }
            finally
            {
                _dispatching = false;
            }
            if (anyWork)
            {
                _idleStreak = 0;
                return;
            }
            _idleStreak++;
            if (_autoSleep && _idleStreak >= _idleLimit)
            {
                StopTicker();
            }
        }

        private void StopTicker()
        {
            if (_running)
            {
                _ticker.Stop();
                _running = false;
            }
            _idleStreak = 0;
        }

        private sealed class FrameEntry
        {
            public FrameCallback Callback;
            public bool Visible;
        }
    }
}
