using System;

namespace SshTool.Core.Forwarding
{
    // 运行时侧的一次性计数快照（01-DESIGN.md §11.2；与 native fwd/pump.h 的
    // TunnelStatsSnapshot 同构，方向已换算成「隧道↔本机」语义）。字段与
    // TunnelStats 累计口径一致：BytesIn = 隧道流向本机（通道→本地 socket），
    // BytesOut = 本机流向隧道（本地 socket→通道）。
    public sealed class TunnelStatsSample
    {
        public ulong ActiveConnections { get; set; }
        public ulong TotalConnections { get; set; }
        public ulong BytesIn { get; set; }
        public ulong BytesOut { get; set; }
    }

    // 每条隧道的运行统计：活跃/累计连接、上下行累计字节与每秒速率、运行起点
    //（与桌面端 tunnel.ts 的 TunnelStats 字段一一对应；速率由每秒 Tick 用
    // 「本次累计 - 上次累计」差值刷新，间隔不等于 1 s 时仍按差值展示）。
    // 累计值以运行时（native）计数为准，本类只保存快照与差值，隧道重启时
    // 由 TunnelManager 调 Reset 归零。
    public sealed class TunnelStats
    {
        public ulong ActiveConnections { get; private set; }
        public ulong TotalConnections { get; private set; }
        public ulong BytesIn { get; private set; }
        public ulong BytesOut { get; private set; }
        public ulong RateIn { get; private set; }
        public ulong RateOut { get; private set; }
        public DateTime? Since { get; set; }

        // 用新样本刷新累计值并按差值刷新速率；sample 为 null 表示运行时已不可读
        //（速率清零，累计保留最后值）。
        public void Apply(TunnelStatsSample sample)
        {
            if (sample == null)
            {
                RateIn = 0;
                RateOut = 0;
                return;
            }
            RateIn = Delta(sample.BytesIn, BytesIn);
            RateOut = Delta(sample.BytesOut, BytesOut);
            ActiveConnections = sample.ActiveConnections;
            TotalConnections = sample.TotalConnections;
            BytesIn = sample.BytesIn;
            BytesOut = sample.BytesOut;
        }

        // 运行中断/停止：活跃连接清零、运行起点清空；累计保留供 UI 查看上次成绩。
        public void MarkStopped()
        {
            ActiveConnections = 0;
            Since = null;
            RateIn = 0;
            RateOut = 0;
        }

        // 重新启动：全部清零（与桌面端 emptyStats() 一致）。
        public void Reset()
        {
            ActiveConnections = 0;
            TotalConnections = 0;
            BytesIn = 0;
            BytesOut = 0;
            RateIn = 0;
            RateOut = 0;
            Since = null;
        }

        private static ulong Delta(ulong current, ulong previous)
        {
            return current > previous ? current - previous : 0;
        }
    }
}
