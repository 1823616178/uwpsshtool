using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.Core.Models;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;

namespace SshTool.Core.Appearance
{
    public enum AppearanceChangeKind
    {
        Added,
        Updated,
        Deleted,
        DefaultChanged
    }

    public sealed class AppearanceChangedEventArgs : EventArgs
    {
        public AppearanceChangedEventArgs(AppearanceChangeKind kind, string appearanceId, IReadOnlyList<string> affectedHostIds)
        {
            Kind = kind;
            AppearanceId = appearanceId;
            AffectedHostIds = affectedHostIds;
        }

        public AppearanceChangeKind Kind { get; private set; }
        public string AppearanceId { get; private set; }

        // 有效外观受本次变化影响的主机 id 集合（A01 要点）
        public IReadOnlyList<string> AffectedHostIds { get; private set; }
    }

    // A01 要点：内置外观只读；删除被引用外观时把引用主机 appearanceId 改回 null（调用方先
    // GetReferencingHostsAsync 弹确认）；变化事件带受影响主机集合。
    // 内置外观由代码注入（A02 BuiltInThemes），不在 appearances.json 里。
    public sealed class AppearanceService
    {
        private readonly AppearanceRepository _appearances;
        private readonly HostRepository _hosts;
        private readonly SettingsRepository _settings;
        private readonly IReadOnlyList<AppearanceProfile> _builtIns;

        public AppearanceService(
            AppearanceRepository appearances,
            HostRepository hosts,
            SettingsRepository settings,
            IReadOnlyList<AppearanceProfile> builtIns)
        {
            if (appearances == null) throw new ArgumentNullException("appearances");
            if (hosts == null) throw new ArgumentNullException("hosts");
            if (settings == null) throw new ArgumentNullException("settings");
            if (builtIns == null) throw new ArgumentNullException("builtIns");
            if (builtIns.Count == 0) throw new ArgumentException("至少需要一个内置外观", "builtIns");
            _appearances = appearances;
            _hosts = hosts;
            _settings = settings;
            _builtIns = builtIns;
        }

        public event EventHandler<AppearanceChangedEventArgs> Changed;

        public IReadOnlyList<AppearanceProfile> BuiltIns
        {
            get { return _builtIns; }
        }

        public bool IsBuiltIn(string appearanceId)
        {
            AppearanceProfile p;
            return AppearanceResolver.TryFind(appearanceId, null, _builtIns, out p);
        }

        // 内置在前，用户外观在后。
        public async Task<IReadOnlyList<AppearanceProfile>> ListAsync()
        {
            var all = new List<AppearanceProfile>(_builtIns);
            all.AddRange(await _appearances.GetAllAsync().ConfigureAwait(false));
            return all;
        }

        public async Task<ResolvedAppearance> ResolveAsync(Host host)
        {
            return AppearanceResolver.Resolve(
                host == null ? null : host.AppearanceId,
                _settings.DefaultAppearanceId,
                await _appearances.GetAllAsync().ConfigureAwait(false),
                _builtIns);
        }

        public async Task<ResolvedAppearance> ResolveForHostAsync(string hostId)
        {
            return await ResolveAsync(await _hosts.GetByIdAsync(hostId).ConfigureAwait(false)).ConfigureAwait(false);
        }

        // 删除确认对话框用：列出引用该外观的主机。
        public async Task<IReadOnlyList<Host>> GetReferencingHostsAsync(string appearanceId)
        {
            var hosts = await _hosts.GetAllAsync().ConfigureAwait(false);
            var refs = new List<Host>();
            foreach (var h in hosts)
            {
                if (string.Equals(h.AppearanceId, appearanceId, StringComparison.Ordinal))
                {
                    refs.Add(h);
                }
            }
            return refs;
        }

        public async Task AddAsync(AppearanceProfile profile)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            ThrowIfBuiltIn(profile);
            await _appearances.AddAsync(profile).ConfigureAwait(false);
            RaiseChanged(AppearanceChangeKind.Added, profile.Id, new List<string>());
        }

        public async Task<bool> UpdateAsync(AppearanceProfile profile)
        {
            if (profile == null) throw new ArgumentNullException("profile");
            ThrowIfBuiltIn(profile);
            bool updated = await _appearances.UpdateAsync(profile).ConfigureAwait(false);
            if (updated)
            {
                RaiseChanged(AppearanceChangeKind.Updated, profile.Id, await AffectedHostsForUpdateAsync(profile.Id).ConfigureAwait(false));
            }
            return updated;
        }

        // 删除外观并把引用主机的 appearanceId 置 null。返回被回退的主机 id 集合。
        public async Task<IReadOnlyList<string>> DeleteAsync(string appearanceId)
        {
            if (IsBuiltIn(appearanceId))
            {
                throw new InvalidOperationException("内置外观只读: " + appearanceId);
            }
            if (await _appearances.GetByIdAsync(appearanceId).ConfigureAwait(false) == null)
            {
                return new List<string>();
            }
            var refs = await GetReferencingHostsAsync(appearanceId).ConfigureAwait(false);
            foreach (var h in refs)
            {
                h.AppearanceId = null;
            }
            await _hosts.UpdateManyAsync(refs).ConfigureAwait(false);
            await _appearances.RemoveAsync(appearanceId).ConfigureAwait(false);
            var affected = new List<string>(refs.Count);
            foreach (var h in refs)
            {
                affected.Add(h.Id);
            }
            RaiseChanged(AppearanceChangeKind.Deleted, appearanceId, affected);
            return affected;
        }

        // 改全局默认外观；跟随默认的主机（appearanceId == null）全部受影响。
        public async Task SetDefaultAsync(string appearanceId)
        {
            AppearanceProfile p;
            if (!AppearanceResolver.TryFind(appearanceId, await _appearances.GetAllAsync().ConfigureAwait(false), _builtIns, out p))
            {
                throw new InvalidOperationException("外观不存在: " + appearanceId);
            }
            _settings.DefaultAppearanceId = appearanceId;
            RaiseChanged(AppearanceChangeKind.DefaultChanged, appearanceId, await HostsFollowingDefaultAsync().ConfigureAwait(false));
        }

        // 受影响 = 直接引用的主机；若改的是当前默认外观，再加上跟随默认的主机。
        private async Task<IReadOnlyList<string>> AffectedHostsForUpdateAsync(string appearanceId)
        {
            bool isDefault = string.Equals(_settings.DefaultAppearanceId, appearanceId, StringComparison.Ordinal);
            var hosts = await _hosts.GetAllAsync().ConfigureAwait(false);
            var affected = new List<string>();
            foreach (var h in hosts)
            {
                if (string.Equals(h.AppearanceId, appearanceId, StringComparison.Ordinal)
                    || (isDefault && h.AppearanceId == null))
                {
                    affected.Add(h.Id);
                }
            }
            return affected;
        }

        private async Task<IReadOnlyList<string>> HostsFollowingDefaultAsync()
        {
            var hosts = await _hosts.GetAllAsync().ConfigureAwait(false);
            var affected = new List<string>();
            foreach (var h in hosts)
            {
                if (h.AppearanceId == null)
                {
                    affected.Add(h.Id);
                }
            }
            return affected;
        }

        private void ThrowIfBuiltIn(AppearanceProfile profile)
        {
            if (profile.BuiltIn || IsBuiltIn(profile.Id))
            {
                throw new InvalidOperationException("内置外观只读: " + profile.Id);
            }
        }

        private void RaiseChanged(AppearanceChangeKind kind, string appearanceId, IReadOnlyList<string> affectedHostIds)
        {
            var handler = Changed;
            if (handler != null)
            {
                handler(this, new AppearanceChangedEventArgs(kind, appearanceId, affectedHostIds));
            }
        }
    }
}
