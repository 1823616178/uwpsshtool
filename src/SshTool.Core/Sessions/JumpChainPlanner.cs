using System;
using System.Collections.Generic;
using SshTool.Core.Hosts;
using SshTool.Core.Models;

namespace SshTool.Core.Sessions
{
    // F07: ProxyJump 跳板跳跃信息
    public sealed class JumpHopInfo
    {
        public Host Host { get; }
        public int HopIndex { get; }      // 1-based (e.g. 1 of 3)
        public int TotalHops { get; }     // total hops in chain (e.g. 3)
        public bool IsTarget { get; }     // true for destination host
        public string Title { get; }      // formatted display title, e.g. "[1/3] JumpHost1"

        public JumpHopInfo(Host host, int hopIndex, int totalHops, bool isTarget)
        {
            Host = host ?? throw new ArgumentNullException(nameof(host));
            HopIndex = hopIndex;
            TotalHops = totalHops;
            IsTarget = isTarget;
            string hostDisplay = !string.IsNullOrEmpty(host.Name)
                ? host.Name
                : (host.Username + "@" + host.HostName);
            Title = totalHops > 1
                ? $"[{hopIndex}/{totalHops}] {hostDisplay}"
                : hostDisplay;
        }
    }

    // F07: ProxyJump 跳板连接计划（01-DESIGN §11.3；04-TASKS F07）
    public sealed class JumpChainPlan
    {
        public IReadOnlyList<JumpHopInfo> Hops { get; }
        public JumpHopInfo Target => Hops[Hops.Count - 1];
        public bool HasJump => Hops.Count > 1;
        public int TotalHops => Hops.Count;

        public JumpChainPlan(IReadOnlyList<JumpHopInfo> hops)
        {
            Hops = hops ?? throw new ArgumentNullException(nameof(hops));
            if (hops.Count == 0)
            {
                throw new ArgumentException("Plan must have at least one hop.", nameof(hops));
            }
        }
    }

    // F07: 跳板链规划器：计算连接顺序、检测环、深度限制 <= 5（01-DESIGN §11.3；04-TASKS F07）
    public static class JumpChainPlanner
    {
        public const int MaxDepth = JumpChainValidator.MaxDepth; // 5

        public static JumpChainPlan Plan(Host target, IReadOnlyList<Host> allHosts)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }
            if (allHosts == null)
            {
                throw new ArgumentNullException(nameof(allHosts));
            }

            var hostMap = new Dictionary<string, Host>(StringComparer.Ordinal);
            for (int i = 0; i < allHosts.Count; i++)
            {
                Host h = allHosts[i];
                if (h != null && !string.IsNullOrEmpty(h.Id))
                {
                    hostMap[h.Id] = h;
                }
            }
            if (!string.IsNullOrEmpty(target.Id))
            {
                hostMap[target.Id] = target;
            }

            var chainFromTarget = new List<Host> { target };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(target.Id))
            {
                seen.Add(target.Id);
            }

            string currentJumpId = target.JumpHostId;
            while (!string.IsNullOrWhiteSpace(currentJumpId))
            {
                if (string.Equals(currentJumpId, target.Id, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Self loop detected: host '{target.Id}' references itself as jump host.");
                }

                if (!seen.Add(currentJumpId))
                {
                    throw new InvalidOperationException($"Cycle detected in jump chain involving host '{currentJumpId}'.");
                }

                if (!hostMap.TryGetValue(currentJumpId, out Host jumpHost) || jumpHost == null)
                {
                    throw new InvalidOperationException($"Jump host not found: '{currentJumpId}'.");
                }

                chainFromTarget.Add(jumpHost);

                if (chainFromTarget.Count > MaxDepth)
                {
                    throw new InvalidOperationException($"Jump chain exceeds maximum depth of {MaxDepth}.");
                }

                currentJumpId = jumpHost.JumpHostId;
            }

            // 反转为连接顺序：最外层跳板 -> ... -> 最终目标
            chainFromTarget.Reverse();

            int total = chainFromTarget.Count;
            var hopInfos = new List<JumpHopInfo>(total);
            for (int i = 0; i < total; i++)
            {
                bool isTarget = (i == total - 1);
                hopInfos.Add(new JumpHopInfo(chainFromTarget[i], i + 1, total, isTarget));
            }

            return new JumpChainPlan(hopInfos);
        }
    }
}
