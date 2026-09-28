namespace SshTool.Core.Lifecycle
{
    // W04（01-DESIGN §16.4）：应用锁的判定。冷启动必锁；后台停留满 GraceMs 再返回才锁
    // （切出去回个消息就要求验证太烦人）。纯状态机，时钟由调用方注入。
    public sealed class AppLockPolicy
    {
        public const long GraceMs = 60000;

        private long _backgroundSinceMs = -1;

        public bool IsLocked { get; private set; }

        public bool OnColdStart(bool enabled)
        {
            IsLocked = enabled;
            return IsLocked;
        }

        public void OnEnteredBackground(long nowMs)
        {
            _backgroundSinceMs = nowMs;
        }

        // 返回是否需要锁定（已锁定时保持锁定）。
        public bool OnReturnedToForeground(bool enabled, long nowMs)
        {
            long since = _backgroundSinceMs;
            _backgroundSinceMs = -1;
            if (!enabled)
            {
                IsLocked = false;
                return false;
            }
            if (since >= 0 && nowMs - since >= GraceMs)
            {
                IsLocked = true;
            }
            return IsLocked;
        }

        public void OnUnlocked()
        {
            IsLocked = false;
        }
    }
}
