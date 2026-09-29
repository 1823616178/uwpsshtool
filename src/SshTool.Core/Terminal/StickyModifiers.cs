using System;

namespace SshTool.Core.Terminal
{
    public enum ModifierKey
    {
        Ctrl = 0,
        Alt = 1,
        Shift = 2
    }

    // 键条修饰键三态（02-UI-DESIGN §5.6：普通 → 单次激活 → 锁定）
    public enum StickyState
    {
        Off = 0,
        OneShot = 1,
        Locked = 2
    }

    // 粘滞修饰键状态机（01-DESIGN §7.5 键条）：
    // 点按 Off→OneShot→Locked→Off 循环（OneShot 时再点 = 双击 → 锁定）；
    // 长按任意态 → Locked 切换（Locked 时长按 = 释放）；OneShot 用后即自动释放。
    public sealed class StickyModifiers
    {
        private StickyState _ctrl;
        private StickyState _alt;
        private StickyState _shift;

        public event EventHandler Changed;

        public StickyState Ctrl
        {
            get { return _ctrl; }
        }

        public StickyState Alt
        {
            get { return _alt; }
        }

        public StickyState Shift
        {
            get { return _shift; }
        }

        public void Tap(ModifierKey key)
        {
            var next = StateOf(key) == StickyState.Off ? StickyState.OneShot
                : StateOf(key) == StickyState.OneShot ? StickyState.Locked
                : StickyState.Off;
            SetState(key, next);
        }

        public void LongPress(ModifierKey key)
        {
            SetState(key, StateOf(key) == StickyState.Locked ? StickyState.Off : StickyState.Locked);
        }

        // 用当前粘滞状态包装按键：OneShot/Locked 都视为激活；OneShot 用后自动释放
        public KeyChord Wrap(TerminalKey key)
        {
            return Wrap(key, '\0');
        }

        public KeyChord Wrap(TerminalKey key, char character)
        {
            var chord = new KeyChord(key, character,
                _ctrl != StickyState.Off, _alt != StickyState.Off, _shift != StickyState.Off);
            bool released = ReleaseOneShots();
            if (released)
            {
                RaiseChanged();
            }
            return chord;
        }

        public void Reset()
        {
            if (_ctrl != StickyState.Off || _alt != StickyState.Off || _shift != StickyState.Off)
            {
                _ctrl = _alt = _shift = StickyState.Off;
                RaiseChanged();
            }
        }

        public StickyState StateOf(ModifierKey key)
        {
            switch (key)
            {
                case ModifierKey.Ctrl: return _ctrl;
                case ModifierKey.Alt: return _alt;
                default: return _shift;
            }
        }

        private void SetState(ModifierKey key, StickyState state)
        {
            if (StateOf(key) == state)
            {
                return;
            }
            switch (key)
            {
                case ModifierKey.Ctrl: _ctrl = state; break;
                case ModifierKey.Alt: _alt = state; break;
                default: _shift = state; break;
            }
            RaiseChanged();
        }

        private bool ReleaseOneShots()
        {
            bool released = false;
            if (_ctrl == StickyState.OneShot) { _ctrl = StickyState.Off; released = true; }
            if (_alt == StickyState.OneShot) { _alt = StickyState.Off; released = true; }
            if (_shift == StickyState.OneShot) { _shift = StickyState.Off; released = true; }
            return released;
        }

        private void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
