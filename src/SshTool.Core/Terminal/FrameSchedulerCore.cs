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

    // opt/full-pass：ticker 空闲停下后驱动光标闪烁的一次性定时器（App 侧为 DispatcherTimer）。
    // Start 重复调用视为重新计时；回调须在 UI 线程（与帧回调同线程）触发。
    public interface IBlinkTimer
    {
        void Start(int dueMs, Action callback);
        void Stop();
    }

    // opt/full-pass 闪烁重构：
    //   旧实现把「闪烁相位翻转」也算作帧工作，导致行为取决于实际帧率——60 fps 下 30 帧空闲
    //   阈值（500 ms）先于 530 ms 闪烁到期，ticker 停下、光标不再闪；50 fps 及以下（600 ms+）
    //   闪烁先到，翻转重置空闲计数，ticker 永不停，空闲时也每帧回调（真机耗电）。
    //   现在翻转不算工作：ticker 只因内容工作保持运转；停下后若有可见视图需要闪烁
    //   （Register 的 wantsBlink），改由 IBlinkTimer 按剩余相位定时翻转并只派发一次回调，
    //   回调报告内容工作时再 Wake ticker。没有注入定时器时退化为「空闲即停、不闪」。
    public sealed class FrameSchedulerCore
    {
        public const int DefaultIdleFrames = 30;
        public const int DefaultBlinkPeriodMs = 530;

        private readonly IFrameTicker _ticker;
        private readonly IBlinkTimer _blinkTimer;
        private readonly Action _onBlinkTimer;
        private bool _blinkTimerArmed;
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
                                  Func<long> nowMs = null,
                                  IBlinkTimer blinkTimer = null)
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
            _blinkTimer = blinkTimer;
            _onBlinkTimer = OnBlinkTimer;
            _ticker.SetFrameCallback(OnFrame);
        }

        public int CallbackErrors { get; private set; }
        public bool BlinkOn { get { return _blinkOn; } }
        public bool IsRunning { get { return _running; } }
        public int Size { get { return _entries.Count; } }
        public bool IsBlinkTimerArmed { get { return _blinkTimerArmed; } }

        public void Register(string id, FrameCallback callback)
        {
            Register(id, callback, null);
        }

        // wantsBlink：该视图当前是否需要闪烁相位（聚焦 + 光标闪烁开启）；null 视为不需要。
        public void Register(string id, FrameCallback callback, Func<bool> wantsBlink)
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
            _entries[id] = new FrameEntry { Callback = callback, WantsBlink = wantsBlink, Visible = true };
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
                CancelBlinkTimer();
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
                CancelBlinkTimer();
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
            // 先翻相位再派发：本帧回调就能看到新相位（翻转本身不算工作，见类注释）
            long now = _nowMs();
            if (HasVisibleEntry() && now - _lastBlinkMs >= _blinkPeriodMs)
            {
                _blinkOn = !_blinkOn;
                _lastBlinkMs = now;
            }
            bool anyWork = DispatchVisible();
            if (anyWork)
            {
                _idleStreak = 0;
                return;
            }
            _idleStreak++;
            if (_autoSleep && _idleStreak >= _idleLimit)
            {
                StopTicker();
                ArmBlinkTimer();
            }
        }

        private bool DispatchVisible()
        {
            _dispatching = true;
            bool anyWork = false;
            try
            {
                for (int i = 0; i < _order.Count; i++)
                {
                    FrameEntry entry;
                    if (!_entries.TryGetValue(_order[i], out entry) || !entry.Visible)
                    {
                        continue;
                    }
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
            }
            finally
            {
                _dispatching = false;
            }
            return anyWork;
        }

        private bool HasVisibleEntry()
        {
            foreach (FrameEntry entry in _entries.Values)
            {
                if (entry.Visible)
                {
                    return true;
                }
            }
            return false;
        }

        private bool AnyVisibleWantsBlink()
        {
            foreach (FrameEntry entry in _entries.Values)
            {
                if (!entry.Visible || entry.WantsBlink == null)
                {
                    continue;
                }
                try
                {
                    if (entry.WantsBlink())
                    {
                        return true;
                    }
                }
                catch
                {
                    CallbackErrors++;
                }
            }
            return false;
        }

        private void ArmBlinkTimer()
        {
            if (_blinkTimer == null || _running || !AnyVisibleWantsBlink())
            {
                return;
            }
            long elapsed = _nowMs() - _lastBlinkMs;
            long due = _blinkPeriodMs - elapsed;
            if (due < 1)
            {
                due = 1;
            }
            else if (due > _blinkPeriodMs)
            {
                due = _blinkPeriodMs; // 时钟回拨保护
            }
            _blinkTimerArmed = true;
            _blinkTimer.Start((int)due, _onBlinkTimer);
        }

        private void CancelBlinkTimer()
        {
            if (_blinkTimerArmed)
            {
                _blinkTimerArmed = false;
                _blinkTimer.Stop();
            }
        }

        private void OnBlinkTimer()
        {
            if (!_blinkTimerArmed)
            {
                return; // 已被 Wake/Unregister 取消后的迟到回调
            }
            _blinkTimerArmed = false;
            if (_running || _dispatching || !AnyVisibleWantsBlink())
            {
                return;
            }
            _blinkOn = !_blinkOn;
            _lastBlinkMs = _nowMs();
            if (DispatchVisible())
            {
                Wake(); // 恰好有内容工作（例如 ContentDirty 还没到）：回到逐帧模式
                return;
            }
            ArmBlinkTimer();
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
            public Func<bool> WantsBlink;
            public bool Visible;
        }
    }
}
