using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Forwarding;
using SshTool.Core.Models;

namespace SshTool.Core.Tests.Fakes
{
    // F05：可脚本化的隧道运行时假实现。预设每次 Start 的结果、手动触发 Dropped、
    // 记录启停调用序列，供 TunnelManager 状态机单测使用。
    public sealed class FakeTunnelRuntime : ITunnelRuntime
    {
        public readonly List<string> Calls = new List<string>();
        public readonly List<string> StoppedIds = new List<string>();
        public readonly Dictionary<string, TunnelStatsSample> Samples =
            new Dictionary<string, TunnelStatsSample>(StringComparer.Ordinal);

        // tunnelId → 下一次 StartAsync 的结果（null = 成功）。
        public readonly Dictionary<string, TunnelRuntimeStartResult> StartResults =
            new Dictionary<string, TunnelRuntimeStartResult>(StringComparer.Ordinal);

        public readonly List<Tunnel> Started = new List<Tunnel>();
        public Func<Tunnel, CancellationToken, TunnelRuntimeStartResult> OnStart;
        public int StartDelayMs;

        public event EventHandler<TunnelRuntimeDroppedEventArgs> Dropped;

        public Task<TunnelRuntimeStartResult> StartAsync(Tunnel tunnel, CancellationToken cancellation)
        {
            Calls.Add("start:" + tunnel.Id);
            Started.Add(tunnel);
            Func<Tunnel, CancellationToken, TunnelRuntimeStartResult> handler = OnStart;
            TunnelRuntimeStartResult result;
            if (handler != null)
            {
                result = handler(tunnel, cancellation);
            }
            else
            {
                TunnelRuntimeStartResult scripted;
                if (!StartResults.TryGetValue(tunnel.Id, out scripted) || scripted == null)
                {
                    scripted = new TunnelRuntimeStartResult
                    {
                        Code = SshErrorCode.None,
                        Message = string.Empty,
                        RouteDescription = "route " + tunnel.Id
                    };
                }
                result = scripted;
            }
            if (StartDelayMs > 0)
            {
                return Task.Run(new Func<Task>(async delegate
                {
                    await Task.Delay(StartDelayMs).ConfigureAwait(false);
                })).ContinueWith(_ => result);
            }
            return Task.FromResult(result);
        }

        public void Stop(string tunnelId)
        {
            Calls.Add("stop:" + tunnelId);
            StoppedIds.Add(tunnelId);
        }

        public TunnelStatsSample ReadSample(string tunnelId)
        {
            Calls.Add("read:" + tunnelId);
            TunnelStatsSample sample;
            return Samples.TryGetValue(tunnelId, out sample) ? sample : null;
        }

        public void Drop(string tunnelId, string reason, SshErrorCode code)
        {
            EventHandler<TunnelRuntimeDroppedEventArgs> handler = Dropped;
            if (handler != null)
            {
                handler(this, new TunnelRuntimeDroppedEventArgs
                {
                    TunnelId = tunnelId,
                    Reason = reason,
                    ErrorCode = code
                });
            }
        }
    }
}
