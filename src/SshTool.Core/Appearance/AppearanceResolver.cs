using System;
using System.Collections.Generic;
using SshTool.Core.Models;

namespace SshTool.Core.Appearance
{
    // 有效外观来源（解析链命中层级）
    public enum AppearanceSource
    {
        Host,           // 主机 appearanceId
        GlobalDefault,  // 全局 defaultAppearanceId
        BuiltInFallback // 内置默认
    }

    public sealed class ResolvedAppearance
    {
        public ResolvedAppearance(AppearanceProfile profile, AppearanceSource source)
        {
            Profile = profile;
            Source = source;
        }

        public AppearanceProfile Profile { get; private set; }
        public AppearanceSource Source { get; private set; }
    }

    // A01 要点：有效外观 = 主机 appearanceId → 全局 defaultAppearanceId → 内置默认。
    public static class AppearanceResolver
    {
        public const string DefaultBuiltInId = "builtin-harmony-dark";

        public static ResolvedAppearance Resolve(
            string hostAppearanceId,
            string defaultAppearanceId,
            IReadOnlyList<AppearanceProfile> profiles,
            IReadOnlyList<AppearanceProfile> builtIns)
        {
            if (builtIns == null)
            {
                throw new ArgumentNullException("builtIns");
            }
            AppearanceProfile p;
            if (!string.IsNullOrEmpty(hostAppearanceId) && TryFind(hostAppearanceId, profiles, builtIns, out p))
            {
                return new ResolvedAppearance(p, AppearanceSource.Host);
            }
            if (!string.IsNullOrEmpty(defaultAppearanceId) && TryFind(defaultAppearanceId, profiles, builtIns, out p))
            {
                return new ResolvedAppearance(p, AppearanceSource.GlobalDefault);
            }
            if (TryFind(DefaultBuiltInId, null, builtIns, out p))
            {
                return new ResolvedAppearance(p, AppearanceSource.BuiltInFallback);
            }
            if (builtIns.Count > 0)
            {
                return new ResolvedAppearance(builtIns[0], AppearanceSource.BuiltInFallback);
            }
            throw new InvalidOperationException("没有可用的内置外观");
        }

        // 用户外观优先于同 id 内置（正常不应撞 id，内置 id 固定 builtin-<slug>）。
        public static bool TryFind(
            string id,
            IReadOnlyList<AppearanceProfile> profiles,
            IReadOnlyList<AppearanceProfile> builtIns,
            out AppearanceProfile profile)
        {
            if (profiles != null)
            {
                for (int i = 0; i < profiles.Count; i++)
                {
                    if (string.Equals(profiles[i].Id, id, StringComparison.Ordinal))
                    {
                        profile = profiles[i];
                        return true;
                    }
                }
            }
            if (builtIns != null)
            {
                for (int i = 0; i < builtIns.Count; i++)
                {
                    if (string.Equals(builtIns[i].Id, id, StringComparison.Ordinal))
                    {
                        profile = builtIns[i];
                        return true;
                    }
                }
            }
            profile = null;
            return false;
        }
    }
}
