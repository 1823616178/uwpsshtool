using System;
using System.Collections.Generic;
using SshTool.Core.Models;

namespace SshTool.Core.Hosts
{
    public enum JumpChainStatus
    {
        None = 0,
        SelfLoop = 1,
        Cycle = 2,
        TooDeep = 3
    }

    // 01-DESIGN.md §9：jumpHostId 链式跳板，保存时拒绝成环；任务 U03a 最大深度 5（含本机）。
    public static class JumpChainValidator
    {
        public const int MaxDepth = 5;

        public static JumpChainStatus Validate(string hostId, string jumpHostId, IReadOnlyList<Host> hosts)
        {
            if (string.IsNullOrWhiteSpace(jumpHostId))
            {
                return JumpChainStatus.None;
            }
            if (string.Equals(jumpHostId, hostId, StringComparison.Ordinal))
            {
                return JumpChainStatus.SelfLoop;
            }

            Dictionary<string, string> jumpOf = BuildMap(hosts);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int count = 0;
            if (!string.IsNullOrEmpty(hostId))
            {
                seen.Add(hostId);
                count = 1;
            }

            string current = jumpHostId;
            while (!string.IsNullOrEmpty(current))
            {
                count++;
                if (count > MaxDepth)
                {
                    return JumpChainStatus.TooDeep;
                }
                if (!seen.Add(current))
                {
                    return JumpChainStatus.Cycle;
                }
                string next;
                if (!jumpOf.TryGetValue(current, out next))
                {
                    break;
                }
                current = next;
            }
            return JumpChainStatus.None;
        }

        public static List<Host> EligibleJumps(string hostId, IReadOnlyList<Host> hosts)
        {
            var result = new List<Host>();
            if (hosts == null)
            {
                return result;
            }
            for (int i = 0; i < hosts.Count; i++)
            {
                Host candidate = hosts[i];
                if (candidate == null || string.IsNullOrEmpty(candidate.Id))
                {
                    continue;
                }
                if (string.Equals(candidate.Id, hostId, StringComparison.Ordinal))
                {
                    continue;
                }
                if (Validate(hostId, candidate.Id, hosts) == JumpChainStatus.None)
                {
                    result.Add(candidate);
                }
            }
            return result;
        }

        private static Dictionary<string, string> BuildMap(IReadOnlyList<Host> hosts)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (hosts == null)
            {
                return map;
            }
            for (int i = 0; i < hosts.Count; i++)
            {
                Host host = hosts[i];
                if (host != null && !string.IsNullOrEmpty(host.Id))
                {
                    map[host.Id] = host.JumpHostId;
                }
            }
            return map;
        }
    }
}
