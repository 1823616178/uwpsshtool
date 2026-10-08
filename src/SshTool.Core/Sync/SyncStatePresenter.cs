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

    // fix/functional-pass：相对时间类别（文案在 App resw：Sync_Relative_*）。
    public enum RelativeTimeKind
    {
        None = 0,
        JustNow,
        Minutes,
        Hours,
        Yesterday,
        DayBeforeYesterday,
        Days,
        Date
    }

    public struct RelativeTime
    {
        public static readonly RelativeTime None = new RelativeTime(RelativeTimeKind.None, 0, default(DateTimeOffset));

        public RelativeTime(RelativeTimeKind kind, int value, DateTimeOffset local)
        {
            Kind = kind;
            Value = value;
            Local = local;
        }

        public RelativeTimeKind Kind { get; private set; }
        // Minutes / Hours / Days 的数量。
        public int Value { get; private set; }
        // 本地时间（Kind=Date 时显示其日期）。
        public DateTimeOffset Local { get; private set; }
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
        private readonly Func<DateTimeOffset, DateTimeOffset> _toLocal;

        // toLocal：UTC → 本地时间（默认 ToLocalTime，即设备时区；单测注入固定偏移）。
        public SyncStatePresenter(Func<DateTimeOffset> clock = null, Func<DateTimeOffset, DateTimeOffset> toLocal = null)
        {
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _toLocal = toLocal ?? (d => d.ToLocalTime());
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

        // fix/functional-pass（P2-2）：相对时间只给类别 + 数值，文案由 App 走 resw（此前 Core 直接
        // 返回中文「刚刚 / 5 分钟前 / 昨天」，英文界面也显示中文）；「昨天 / 前天 / N 天前」按
        // **本地**日历日判定（此前按 UTC 日，东八区凌晨 0–8 点会把今天的同步算成昨天）。
        // 入参为 ISO 8601 UTC（yyyy-MM-ddTHH:mm:ss...Z）；解析失败返回 Kind=None。
        public RelativeTime ComputeRelativeTime(string isoUtc)
        {
            if (string.IsNullOrEmpty(isoUtc))
            {
                return RelativeTime.None;
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
                    return RelativeTime.None;
                }
            }
            DateTimeOffset now = _clock();
            DateTimeOffset localParsed = _toLocal(parsed);
            TimeSpan diff = now - parsed;
            if (diff < TimeSpan.Zero)
            {
                diff = TimeSpan.Zero;
            }
            double totalSeconds = diff.TotalSeconds;
            if (totalSeconds < JustNowSeconds)
            {
                return new RelativeTime(RelativeTimeKind.JustNow, 0, localParsed);
            }
            if (totalSeconds < SecondsPerHour)
            {
                int minutes = Math.Max(1, (int)(totalSeconds / SecondsPerMinute));
                return new RelativeTime(RelativeTimeKind.Minutes, minutes, localParsed);
            }
            if (totalSeconds < SecondsPerDay)
            {
                int hours = Math.Max(1, (int)(totalSeconds / SecondsPerHour));
                return new RelativeTime(RelativeTimeKind.Hours, hours, localParsed);
            }
            DateTime todayLocal = _toLocal(now).Date;
            int dayDiff = (int)(todayLocal - localParsed.Date).TotalDays;
            if (dayDiff == 1)
            {
                return new RelativeTime(RelativeTimeKind.Yesterday, 1, localParsed);
            }
            if (dayDiff == 2)
            {
                return new RelativeTime(RelativeTimeKind.DayBeforeYesterday, 2, localParsed);
            }
            if (dayDiff > 2 && dayDiff < 7)
            {
                return new RelativeTime(RelativeTimeKind.Days, dayDiff, localParsed);
            }
            return new RelativeTime(RelativeTimeKind.Date, 0, localParsed);
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
