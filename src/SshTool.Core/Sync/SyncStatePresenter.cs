using System;
using System.Globalization;
using SshTool.Core.Sync.Auth;
using SshTool.Core.Sync.Vault;

namespace SshTool.Core.Sync
{
    // U17 同步状态页的屏幕判定（02-UI-DESIGN.md §5.13 状态 Pivot 的入口路由）。
    // 纯逻辑：未登录 → 登录；已登录 + 无库 → 建库；已登录 + 锁定 → 解锁；其余 → 状态卡。
    public enum SyncScreenKind
    {
        Login,
        CreateVault,
        UnlockVault,
        Status
    }

    // U17 状态卡的相位文案 + 图标（对应 §5.13 相位文案与图标）。
    // 文案走 resw 键（Core 不引用 App 资源）；图标键对应 Tokens.xaml 字形键。
    public sealed class StatusCardSpec
    {
        public string TextKey { get; set; }

        public string IconKey { get; set; }

        // 同步中：图标持续旋转。
        public bool Spin { get; set; }
    }

    // U17 SyncStatePresenter（纯逻辑，可单测）。
    // 根据 AuthState / VaultStatus / SyncPhase 决定页面路由、状态卡文案与相对时间。
    // 时钟经注入便于单测；默认使用 DateTimeOffset.UtcNow。
    public sealed class SyncStatePresenter
    {
        // 相对时间阈值（秒）。
        public const int JustNowSeconds = 60;
        public const int MinutesPerHour = 60;
        public const int HoursPerDay = 24;
        public const int SecondsPerMinute = 60;
        public const int SecondsPerHour = 3600;
        public const int SecondsPerDay = 86400;

        private readonly Func<DateTimeOffset> _clock;

        public SyncStatePresenter(Func<DateTimeOffset> clock = null)
        {
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        // 屏幕判定：未登录 → Login；已登录按 Vault 状态路由。
        public SyncScreenKind DetermineScreen(AuthState auth, SyncState state)
        {
            if (auth == null || !auth.Authenticated)
            {
                return SyncScreenKind.Login;
            }
            VaultStatus vault = state == null ? VaultStatus.Missing : state.Vault;
            switch (vault)
            {
                case VaultStatus.Missing:
                    return SyncScreenKind.CreateVault;
                case VaultStatus.Locked:
                    return SyncScreenKind.UnlockVault;
                default:
                    return SyncScreenKind.Status;
            }
        }

        // 相对时间："刚刚"/"5 分钟前"/"3 小时前"/"昨天"/"前天"/日期。
        // 入参为 ISO 8601 UTC（yyyy-MM-ddTHH:mm:ss...Z）；解析失败返回空字符串。
        public string FormatRelativeTime(string isoUtc)
        {
            if (string.IsNullOrEmpty(isoUtc))
            {
                return string.Empty;
            }
            DateTimeOffset parsed;
            if (!DateTimeOffset.TryParseExact(
                    isoUtc,
                    new[]
                    {
                        "yyyy-MM-ddTHH:mm:ss.fffZ",
                        "yyyy-MM-ddTHH:mm:ssZ",
                        "yyyy-MM-ddTHH:mmZ"
                    },
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out parsed))
            {
                // 兜底：宽松解析。
                if (!DateTimeOffset.TryParse(isoUtc, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
                {
                    return string.Empty;
                }
            }
            TimeSpan diff = _clock() - parsed;
            if (diff < TimeSpan.Zero)
            {
                diff = TimeSpan.Zero;
            }
            double totalSeconds = diff.TotalSeconds;
            if (totalSeconds < JustNowSeconds)
            {
                return "刚刚";
            }
            if (totalSeconds < SecondsPerHour)
            {
                int minutes = Math.Max(1, (int)(totalSeconds / SecondsPerMinute));
                return minutes + " 分钟前";
            }
            if (totalSeconds < SecondsPerDay)
            {
                int hours = Math.Max(1, (int)(totalSeconds / SecondsPerHour));
                return hours + " 小时前";
            }
            // 昨天 / 前天判定按日历日（UTC）。
            DateTimeOffset now = _clock();
            DateTimeOffset today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
            int dayDiff = (int)(today - parsed.Date).TotalDays;
            if (dayDiff == 1)
            {
                return "昨天";
            }
            if (dayDiff == 2)
            {
                return "前天";
            }
            if (dayDiff > 2 && dayDiff < 7)
            {
                return dayDiff + " 天前";
            }
            return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        // 状态卡文案 + 图标：每个相位对应一个 resw 键与字形键（Spin 仅 Syncing 为 true）。
        public StatusCardSpec StatusCardText(SyncState state)
        {
            SyncPhase phase = state == null ? SyncPhase.SignedOut : state.Phase;
            switch (phase)
            {
                case SyncPhase.SignedOut:
                    return new StatusCardSpec { TextKey = "Sync_SignedOut", IconKey = "IconCloudOff" };
                case SyncPhase.Disabled:
                    return new StatusCardSpec { TextKey = "Sync_Disabled", IconKey = "IconCloud" };
                case SyncPhase.Locked:
                    return new StatusCardSpec { TextKey = "Sync_Locked", IconKey = "IconLock" };
                case SyncPhase.Idle:
                    return new StatusCardSpec { TextKey = "Sync_Idle", IconKey = "IconSync" };
                case SyncPhase.Syncing:
                    return new StatusCardSpec { TextKey = "Sync_Syncing", IconKey = "IconSync", Spin = true };
                case SyncPhase.Synced:
                    return new StatusCardSpec { TextKey = "Sync_Synced", IconKey = "IconCircleCheck" };
                case SyncPhase.Offline:
                    return new StatusCardSpec { TextKey = "Sync_Offline", IconKey = "IconCloudOff" };
                case SyncPhase.Conflict:
                    return new StatusCardSpec { TextKey = "Sync_Conflict", IconKey = "IconWarning" };
                case SyncPhase.Error:
                    return new StatusCardSpec { TextKey = "Sync_Error", IconKey = "IconSyncError" };
                case SyncPhase.AuthError:
                    return new StatusCardSpec { TextKey = "Sync_AuthError", IconKey = "IconWarning" };
                default:
                    return new StatusCardSpec { TextKey = "Sync_Idle", IconKey = "IconSync" };
            }
        }
    }
}
