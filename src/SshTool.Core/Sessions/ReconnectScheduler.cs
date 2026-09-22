using SshTool.Core.Common;

namespace SshTool.Core.Sessions
{
    // 01-DESIGN.md §9.3：退避 1→2→5→10→20→30 s；2xx 与 303 不自动重连。
    public static class ReconnectScheduler
    {
        public static readonly int[] BackoffSeconds = { 1, 2, 5, 10, 20, 30 };

        public static int DelaySeconds(int attemptIndex)
        {
            return Common.BackoffTable.DelayFor(BackoffSeconds, attemptIndex);
        }

        public static bool ShouldReconnect(SshErrorCode code)
        {
            int n = (int)code;
            if (n >= 200 && n < 300)
            {
                return false;
            }
            if (code == SshErrorCode.HostKeyMismatch)
            {
                return false;
            }
            if (code == SshErrorCode.None)
            {
                return false;
            }
            return true;
        }
    }
}
