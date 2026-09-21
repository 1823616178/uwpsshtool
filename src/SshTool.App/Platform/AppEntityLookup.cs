using System;
using SshTool.App.Infrastructure;
using SshTool.Core.Models;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Vault;
using SshTool.Core.Storage.Repositories;

namespace SshTool.App.Platform
{
    // U19：冲突字段的实体名称查找（本机名 → 远端文档 title → id）。
    // 本机名按实体类型查 HostRepository / GroupRepository / TunnelRepository；找不到则回退 id。
    // 远端文档 title 在本机场景无独立来源，回退 null（由 Presenter 再回退 id）。
    public sealed class AppEntityLookup : IEntityNameLookup
    {
        private readonly AppServices _services;

        public AppEntityLookup(AppServices services)
        {
            _services = services;
        }

        public string LocalName(SyncConflictEntity entity, string id)
        {
            if (_services == null || string.IsNullOrEmpty(id))
            {
                return null;
            }
            try
            {
                switch (entity)
                {
                    case SyncConflictEntity.Server:
                        return FirstNonEmpty(FindHostName(id));
                    case SyncConflictEntity.Group:
                        return FirstNonEmpty(FindGroupName(id));
                    case SyncConflictEntity.Tunnel:
                        return FirstNonEmpty(FindTunnelName(id));
                    default:
                        return null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public string RemoteTitle(SyncConflictEntity entity, string id)
        {
            // 远端文档未持久化；按规格回退 id（由 Presenter 处理）。
            return null;
        }

        private string FindHostName(string id)
        {
            try
            {
                HostRepository hosts;
                if (!ServiceRegistry.TryGet(out hosts))
                {
                    hosts = _services.Hosts;
                }
                if (hosts == null)
                {
                    return null;
                }
                Host host = hosts.GetByIdAsync(id).Result;
                return host != null ? host.Name : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private string FindGroupName(string id)
        {
            try
            {
                GroupRepository groups;
                if (!ServiceRegistry.TryGet(out groups))
                {
                    groups = _services.Groups;
                }
                if (groups == null)
                {
                    return null;
                }
                HostGroup group = groups.GetByIdAsync(id).Result;
                return group != null ? group.Name : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private string FindTunnelName(string id)
        {
            try
            {
                TunnelRepository tunnels;
                if (!ServiceRegistry.TryGet(out tunnels))
                {
                    tunnels = _services.Tunnels;
                }
                if (tunnels == null)
                {
                    return null;
                }
                Tunnel tunnel = tunnels.GetByIdAsync(id).Result;
                return tunnel != null ? tunnel.Name : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string FirstNonEmpty(string value)
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
