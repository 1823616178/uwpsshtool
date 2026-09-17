using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SshTool.Core.Storage.Repositories;

namespace SshTool.Core.Storage
{
    // D02 要点 5：跨仓库引用规则协调。
    //   删主机 → 删其隧道（serverId 指向，及 relay 的 destServerId 指向，否则违反校验不变式）、
    //             清其他主机 jumpHostId、ISecretStore 按 "host:<id>:" 前缀级联；
    //   删分组 → 清主机 groupId 与隧道 groupId；
    //   删密钥 → 被主机引用时拒绝（InvalidOperationException）。
    public sealed class ConfigService
    {
        private readonly HostRepository _hosts;
        private readonly GroupRepository _groups;
        private readonly TunnelRepository _tunnels;
        private readonly KeyRepository _keys;
        private readonly ISecretStore _secrets;

        public ConfigService(
            HostRepository hosts,
            GroupRepository groups,
            TunnelRepository tunnels,
            KeyRepository keys,
            ISecretStore secrets = null)
        {
            if (hosts == null) throw new ArgumentNullException("hosts");
            if (groups == null) throw new ArgumentNullException("groups");
            if (tunnels == null) throw new ArgumentNullException("tunnels");
            if (keys == null) throw new ArgumentNullException("keys");
            _hosts = hosts;
            _groups = groups;
            _tunnels = tunnels;
            _keys = keys;
            _secrets = secrets ?? NullSecretStore.Instance;
        }

        public HostRepository Hosts
        {
            get { return _hosts; }
        }

        public GroupRepository Groups
        {
            get { return _groups; }
        }

        public TunnelRepository Tunnels
        {
            get { return _tunnels; }
        }

        public KeyRepository Keys
        {
            get { return _keys; }
        }

        public async Task DeleteHostAsync(string hostId)
        {
            var tunnels = await _tunnels.GetAllAsync().ConfigureAwait(false);
            var doomedTunnelIds = tunnels
                .Where(t => t.ServerId == hostId || t.DestServerId == hostId)
                .Select(t => t.Id)
                .ToList();
            await _tunnels.RemoveManyAsync(doomedTunnelIds).ConfigureAwait(false);

            var hosts = await _hosts.GetAllAsync().ConfigureAwait(false);
            var jumpRefs = hosts.Where(h => h.Id != hostId && h.JumpHostId == hostId).ToList();
            foreach (var h in jumpRefs)
            {
                h.JumpHostId = null;
            }
            await _hosts.UpdateManyAsync(jumpRefs).ConfigureAwait(false);

            await _hosts.RemoveAsync(hostId).ConfigureAwait(false);
            await _secrets.RemoveByPrefixAsync(SecretKeys.HostPrefix(hostId)).ConfigureAwait(false);
        }

        public async Task DeleteGroupAsync(string groupId)
        {
            var hosts = (await _hosts.GetAllAsync().ConfigureAwait(false))
                .Where(h => h.GroupId == groupId).ToList();
            foreach (var h in hosts)
            {
                h.GroupId = null;
            }
            await _hosts.UpdateManyAsync(hosts).ConfigureAwait(false);

            var tunnels = (await _tunnels.GetAllAsync().ConfigureAwait(false))
                .Where(t => t.GroupId == groupId).ToList();
            foreach (var t in tunnels)
            {
                t.GroupId = null;
            }
            await _tunnels.UpdateManyAsync(tunnels).ConfigureAwait(false);

            await _groups.RemoveAsync(groupId).ConfigureAwait(false);
        }

        public async Task DeleteKeyAsync(string keyId)
        {
            var hosts = await _hosts.GetAllAsync().ConfigureAwait(false);
            if (hosts.Any(h => h.KeyId == keyId))
            {
                throw new InvalidOperationException("密钥仍被主机引用，拒绝删除: " + keyId);
            }
            await _keys.RemoveAsync(keyId).ConfigureAwait(false);
            await _secrets.RemoveByPrefixAsync(SecretKeys.KeyPrefix(keyId)).ConfigureAwait(false);
        }
    }
}
