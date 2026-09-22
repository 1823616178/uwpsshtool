using System;
using System.Collections.Generic;
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
                Host host = FirstMatch(hosts.GetAllAsync(), id, h => h.Id);
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
                HostGroup group = FirstMatch(groups.GetAllAsync(), id, g => g.Id);
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
                Tunnel tunnel = FirstMatch(tunnels.GetAllAsync(), id, t => t.Id);
                return tunnel != null ? tunnel.Name : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // O14：不再 .Result 阻塞。IEntityNameLookup 是同步契约（冲突页在渲染行
        // 时逐条问名字），没法改异步；但仓库在首次 LoadAsync 之后就是内存表，
        // 所以只在**已经加载完**时读，没加载完就返回 null，由 Presenter 回退 id。
        // 这样 UI 线程上不会再有一次文件 I/O 的同步等待。
        private static T FirstMatch<T>(System.Threading.Tasks.Task<IReadOnlyList<T>> loaded,
                                       string id, Func<T, string> idOf)
            where T : class
        {
            if (loaded == null || !loaded.IsCompleted || loaded.IsFaulted || loaded.IsCanceled)
            {
                return null; // 仓库尚未就绪：宁可回退 id，也不在 UI 线程上等 I/O
            }
            IReadOnlyList<T> all = loaded.Result; // 已完成，Result 不阻塞
            if (all == null)
            {
                return null;
            }
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(idOf(all[i]), id, StringComparison.Ordinal))
                {
                    return all[i];
                }
            }
            return null;
        }

        private static string FirstNonEmpty(string value)
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
