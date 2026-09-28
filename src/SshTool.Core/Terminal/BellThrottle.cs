namespace SshTool.Core.Terminal
{
    // W04（01-DESIGN §16.4）：按帧比较 BEL 计数，决定是否响铃反馈。
    // 200 ms 内的多次 BEL 合并成一次（`yes $'\a'` 这类输出不能把手机振成电钻）；
    // 计数回退（屏幕重建/重设网格）时只重设基线，不响。
    public sealed class BellThrottle
    {
        public const long MergeWindowMs = 200;

        private long _lastCount = -1;
        private long _lastRingMs = long.MinValue;

        public bool ShouldRing(long bellCount, long nowMs)
        {
            if (_lastCount < 0 || bellCount < _lastCount)
            {
                _lastCount = bellCount;
                return false;
            }
            if (bellCount == _lastCount)
            {
                return false;
            }
            _lastCount = bellCount;
            if (_lastRingMs != long.MinValue && nowMs - _lastRingMs < MergeWindowMs)
            {
                return false;
            }
            _lastRingMs = nowMs;
            return true;
        }

        public void Reset()
        {
            _lastCount = -1;
            _lastRingMs = long.MinValue;
        }
    }
}
