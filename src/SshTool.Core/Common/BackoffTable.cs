using System;

namespace SshTool.Core.Common
{
    // O14：退避表的取值逻辑。
    //
    // 仓里有三张退避表，取值写法几乎逐字重复：
    //   Sessions/ReconnectScheduler  {1,2,5,10,20,30}
    //   Forwarding/TunnelManager     {1,2,5,10,20,30,60}
    //   Sync/SyncCoordinator         {1,2,5,10,30,60,300}
    // **三张表本身按协议各不相同，不要合并**（桌面端对 SSH 重连、隧道重试、同步
    // 重试的节奏要求不一样，03-SYNC-PROTOCOL §7.4 与各自注释都写明了）。这里只抽
    // 「按次数取一格、越界取最后一格」这段公共动作。
    public static class BackoffTable
    {
        // attempt 从 0 起；负数按 0 处理，超出表长按最后一格（不再增长）。
        public static int DelayFor(int[] steps, int attempt)
        {
            if (steps == null || steps.Length == 0)
            {
                return 0;
            }
            if (attempt < 0)
            {
                attempt = 0;
            }
            int index = attempt >= steps.Length ? steps.Length - 1 : attempt;
            return steps[index];
        }
    }
}
