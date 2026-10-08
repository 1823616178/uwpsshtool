using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.Core.Models;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;

namespace SshTool.Core.Sessions
{
    // fix/functional-pass：「主机密钥已改变」后的重置。
    // 信任记录有两处：known_hosts 条目（主机名+端口）和主机配置上的钉住指纹
    // （Host.HostFingerprint，首连成功后自动写入）。HostKeyVerifier 在 known_hosts 缺失时
    // 仍按钉住指纹判定，所以只删 known_hosts 会继续报不匹配——两处必须一起清。
    // 清完后下次连接走首次连接确认（PromptUnknown）。后台隧道不会自动信任（VerifyNonInteractive）。
    public static class HostKeyReset
    {
        public sealed class Result
        {
            public int KnownHostsRemoved { get; set; }
            public int PinsCleared { get; set; }
        }

        // 删除 hostName:port 的全部 known_hosts 条目，并清掉指向同一地址的主机（以及 hostId 指定的主机）
        // 的钉住指纹。主机名按 OrdinalIgnoreCase 比较，与 KnownHostRepository.FindAsync 一致。
        public static async Task<Result> ResetAsync(
            KnownHostRepository knownHosts, HostRepository hosts, string hostName, int port, string hostId)
        {
            var result = new Result();
            int normalizedPort = port > 0 ? port : 22;
            if (knownHosts != null && !string.IsNullOrEmpty(hostName))
            {
                IReadOnlyList<KnownHost> all = await knownHosts.GetAllAsync().ConfigureAwait(false);
                var doomed = new List<string>();
                for (int i = 0; i < all.Count; i++)
                {
                    KnownHost kh = all[i];
                    if (kh != null && !string.IsNullOrEmpty(kh.Id) && Matches(kh.Host, kh.Port, hostName, normalizedPort))
                    {
                        doomed.Add(kh.Id);
                    }
                }
                if (doomed.Count > 0)
                {
                    await knownHosts.RemoveManyAsync(doomed, ChangeOrigin.User).ConfigureAwait(false);
                    result.KnownHostsRemoved = doomed.Count;
                }
            }
            result.PinsCleared = await ClearPinsAsync(hosts, hostName, normalizedPort, hostId).ConfigureAwait(false);
            return result;
        }

        // 只清钉住指纹（「已知主机」页删除条目时调用，条目本身由调用方删除）。
        public static async Task<int> ClearPinsAsync(HostRepository hosts, string hostName, int port, string hostId)
        {
            if (hosts == null)
            {
                return 0;
            }
            int normalizedPort = port > 0 ? port : 22;
            IReadOnlyList<Host> all = await hosts.GetAllAsync().ConfigureAwait(false);
            var changed = new List<Host>();
            for (int i = 0; i < all.Count; i++)
            {
                Host h = all[i];
                if (h == null || string.IsNullOrEmpty(h.HostFingerprint))
                {
                    continue;
                }
                bool byId = !string.IsNullOrEmpty(hostId) && string.Equals(h.Id, hostId, StringComparison.Ordinal);
                bool byAddress = !string.IsNullOrEmpty(hostName) && Matches(h.HostName, h.Port, hostName, normalizedPort);
                if (byId || byAddress)
                {
                    Host copy = h.Clone();
                    copy.HostFingerprint = string.Empty;
                    changed.Add(copy);
                }
            }
            if (changed.Count > 0)
            {
                await hosts.UpdateManyAsync(changed, ChangeOrigin.User).ConfigureAwait(false);
            }
            return changed.Count;
        }

        private static bool Matches(string host, int port, string hostName, int normalizedPort)
        {
            int p = port > 0 ? port : 22;
            return p == normalizedPort && string.Equals(host, hostName, StringComparison.OrdinalIgnoreCase);
        }
    }
}
