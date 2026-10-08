using System;
using System.Collections.Generic;
using SshTool.Core.Sync.Auth;

namespace SshTool.Core.Sync
{
    // feat/account-sync-ui：「账号与同步」页各卡片的纯逻辑映射（可单测）。App 只负责把这里给出的
    // resw 键 / 字形键 / 语气（tone）翻成文案、字形与画刷，判断规则不散落在 code-behind 里。
    //   状态卡（hero）：相位 → 语气 + 字形；「立即同步」何时可用、不可用时提示什么；是否内联显示消息。
    //   保险库卡：Missing → 创建；Locked → 输入同步密码解锁；Ready → 已解锁 + 锁定 / 修改同步密码 / 记住开关。
    //   设备：平台串 → 手机 / 桌面图标与可读名；历史：按版本序列推断「当前 / 首个 / 密钥轮换 / 普通更新」。
    public enum SyncTone
    {
        Neutral,
        Accent,
        Success,
        Warning,
        Danger
    }

    public enum VaultCardKind
    {
        Hidden,
        Missing,
        Locked,
        Ready
    }

    public enum DevicePlatformKind
    {
        Unknown,
        Phone,
        Desktop
    }

    public enum HistoryEventKind
    {
        Update,
        Current,
        KeyRotated,
        Initial
    }

    public sealed class StatusHeroSpec
    {
        public SyncTone Tone { get; set; }

        // Themes/Tokens.xaml 里的字形键（IconSync / IconCheck / IconLock …）。
        public string GlyphKey { get; set; }

        public bool Spin { get; set; }

        public bool CanSyncNow { get; set; }

        // 「立即同步」不可用时的说明（resw 键）；可用或无需说明时为 null。
        public string SyncNowHintKey { get; set; }

        // 是否在状态卡内联显示消息（错误 / 离线 / 冲突 / 其他设备改了同步密码等）。
        public bool ShowDetail { get; set; }

        public SyncTone DetailTone { get; set; }
    }

    public sealed class VaultCardSpec
    {
        public VaultCardKind Kind { get; set; }
        public SyncTone Tone { get; set; }
        public string GlyphKey { get; set; }
        public string TitleKey { get; set; }
        public string DescriptionKey { get; set; }
        public bool ShowUnlock { get; set; }
        public bool ShowCreate { get; set; }
        public bool ShowLock { get; set; }
        public bool ShowChangePassword { get; set; }
        public bool ShowRemember { get; set; }
        public bool ShowDeleteVault { get; set; }
    }

    // 历史版本推断的输入（列表按服务端顺序：新 → 旧）。
    public sealed class HistoryEntryInput
    {
        public string Revision { get; set; }
        public int KeyVersion { get; set; }
    }

    public static class AccountSyncPresenter
    {
        public const string FirstRevision = "1";

        // 本页只在未登录时转走（去登录页）。保险库 Missing / Locked 不再转去建库 / 解锁页，
        // 而是在本页的保险库卡里给出「创建保险库」/「输入同步密码解锁」——此前一进本页就被弹去
        // 解锁页，用户找不到「在哪里输入同步密码」，锁定后也回不到状态页。
        public static bool NeedsRouting(AuthState auth)
        {
            return auth == null || !auth.Authenticated;
        }

        public static SyncTone ToneOf(SyncPhase phase)
        {
            switch (phase)
            {
                case SyncPhase.Synced:
                    return SyncTone.Success;
                case SyncPhase.Idle:
                case SyncPhase.Syncing:
                    return SyncTone.Accent;
                case SyncPhase.Offline:
                case SyncPhase.Conflict:
                case SyncPhase.Locked:
                    return SyncTone.Warning;
                case SyncPhase.Error:
                case SyncPhase.AuthError:
                    return SyncTone.Danger;
                default:
                    return SyncTone.Neutral;
            }
        }

        public static string GlyphOf(SyncPhase phase)
        {
            switch (phase)
            {
                case SyncPhase.Synced:
                    return "IconCheck";
                case SyncPhase.Idle:
                case SyncPhase.Syncing:
                    return "IconSync";
                case SyncPhase.Offline:
                case SyncPhase.SignedOut:
                    return "IconCloudOff";
                case SyncPhase.Conflict:
                    return "IconWarning";
                case SyncPhase.Locked:
                    return "IconLock";
                case SyncPhase.Error:
                case SyncPhase.AuthError:
                    return "IconSyncError";
                default:
                    return "IconCloud";
            }
        }

        public static StatusHeroSpec Hero(AuthState auth, SyncState state)
        {
            SyncPhase phase = state == null ? SyncPhase.SignedOut : state.Phase;
            bool signedIn = auth != null && auth.Authenticated;
            VaultStatus vault = state == null ? VaultStatus.Missing : state.Vault;
            bool enabled = state != null && state.Preferences != null && state.Preferences.Enabled;
            var spec = new StatusHeroSpec
            {
                Tone = ToneOf(phase),
                GlyphKey = GlyphOf(phase),
                Spin = phase == SyncPhase.Syncing
            };
            if (signedIn)
            {
                if (vault == VaultStatus.Missing)
                {
                    spec.SyncNowHintKey = "AccountSync_HintCreateVault";
                }
                else if (vault == VaultStatus.Locked)
                {
                    spec.SyncNowHintKey = "AccountSync_HintUnlockVault";
                }
                else if (!enabled)
                {
                    spec.SyncNowHintKey = "AccountSync_HintSyncDisabled";
                }
            }
            spec.CanSyncNow = signedIn && vault == VaultStatus.Ready && enabled && phase != SyncPhase.Syncing;
            spec.ShowDetail = HasDetail(state);
            spec.DetailTone = spec.Tone == SyncTone.Danger || spec.Tone == SyncTone.Warning
                ? spec.Tone
                : SyncTone.Accent;
            return spec;
        }

        private static bool HasDetail(SyncState state)
        {
            if (state == null)
            {
                return false;
            }
            switch (state.Phase)
            {
                case SyncPhase.Conflict:
                case SyncPhase.Error:
                case SyncPhase.Offline:
                case SyncPhase.AuthError:
                    return true;
            }
            // 非错误相位上的提示码（其他设备改了同步密码、云端保险库已删除、同步密码已更新…）也要让人看到。
            return state.MessageCode != SyncMessageCode.None && state.MessageCode != SyncMessageCode.Error;
        }

        public static VaultCardSpec VaultCard(AuthState auth, SyncState state, bool rememberKey)
        {
            if (auth == null || !auth.Authenticated || state == null)
            {
                return new VaultCardSpec { Kind = VaultCardKind.Hidden, Tone = SyncTone.Neutral, GlyphKey = "IconLock" };
            }
            switch (state.Vault)
            {
                case VaultStatus.Missing:
                    return new VaultCardSpec
                    {
                        Kind = VaultCardKind.Missing,
                        Tone = SyncTone.Accent,
                        GlyphKey = "IconAdd",
                        TitleKey = "AccountSync_VaultMissingTitle",
                        DescriptionKey = "AccountSync_VaultMissingDesc",
                        ShowCreate = true
                    };
                case VaultStatus.Locked:
                    return new VaultCardSpec
                    {
                        Kind = VaultCardKind.Locked,
                        Tone = SyncTone.Warning,
                        GlyphKey = "IconLock",
                        TitleKey = "AccountSync_VaultLockedTitle",
                        DescriptionKey = "AccountSync_VaultLockedDesc",
                        ShowUnlock = true,
                        ShowDeleteVault = true
                    };
                default:
                    return new VaultCardSpec
                    {
                        Kind = VaultCardKind.Ready,
                        Tone = SyncTone.Success,
                        GlyphKey = "IconUnlock",
                        TitleKey = "AccountSync_VaultReadyTitle",
                        DescriptionKey = rememberKey
                            ? "AccountSync_VaultReadyDescRemember"
                            : "AccountSync_VaultReadyDescForget",
                        ShowLock = true,
                        ShowChangePassword = true,
                        ShowRemember = true,
                        ShowDeleteVault = true
                    };
            }
        }

        // 设备平台串（本端上报 windows-mobile-arm / windows-uwp-x64；桌面端 win32 / darwin / linux）。
        public static DevicePlatformKind ClassifyPlatform(string platform)
        {
            if (string.IsNullOrWhiteSpace(platform))
            {
                return DevicePlatformKind.Unknown;
            }
            string p = platform.Trim().ToLowerInvariant();
            if (p.Contains("mobile") || p.Contains("phone") || p.Contains("android") || p.StartsWith("ios", StringComparison.Ordinal))
            {
                return DevicePlatformKind.Phone;
            }
            if (p.StartsWith("windows", StringComparison.Ordinal) || p.StartsWith("win32", StringComparison.Ordinal)
                || p.Contains("desktop") || p.StartsWith("darwin", StringComparison.Ordinal)
                || p.StartsWith("mac", StringComparison.Ordinal) || p.StartsWith("linux", StringComparison.Ordinal))
            {
                return DevicePlatformKind.Desktop;
            }
            return DevicePlatformKind.Unknown;
        }

        public static string GlyphOf(DevicePlatformKind kind)
        {
            switch (kind)
            {
                case DevicePlatformKind.Phone:
                    return "IconPhone";
                case DevicePlatformKind.Desktop:
                    return "IconDesktop";
                default:
                    return "IconDevices";
            }
        }

        // 平台可读名（产品名，中英文界面写法相同，不进 resw）；未知平台原样返回。
        public static string DescribePlatform(string platform)
        {
            if (string.IsNullOrWhiteSpace(platform))
            {
                return string.Empty;
            }
            string p = platform.Trim().ToLowerInvariant();
            if (p.StartsWith("windows-mobile", StringComparison.Ordinal))
            {
                return "Windows 10 Mobile";
            }
            if (p.StartsWith("windows-uwp", StringComparison.Ordinal))
            {
                return "Windows 10";
            }
            if (p.StartsWith("win32", StringComparison.Ordinal) || p == "windows")
            {
                return "Windows";
            }
            if (p.StartsWith("darwin", StringComparison.Ordinal) || p.StartsWith("mac", StringComparison.Ordinal))
            {
                return "macOS";
            }
            if (p.StartsWith("linux", StringComparison.Ordinal))
            {
                return "Linux";
            }
            if (p.StartsWith("android", StringComparison.Ordinal))
            {
                return "Android";
            }
            if (p.StartsWith("ios", StringComparison.Ordinal))
            {
                return "iOS";
            }
            return platform.Trim();
        }

        // 历史事件推断（items 新 → 旧）：当前云端版本 > 首个版本（revision 1 且是最旧一条）>
        // 与下一条（更旧）keyVersion 不同 = 密钥轮换 > 普通更新。
        public static HistoryEventKind[] ClassifyHistory(IList<HistoryEntryInput> items, string currentRevision)
        {
            if (items == null || items.Count == 0)
            {
                return new HistoryEventKind[0];
            }
            var result = new HistoryEventKind[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                HistoryEntryInput item = items[i];
                string revision = item == null ? null : item.Revision;
                if (!string.IsNullOrEmpty(revision) && !string.IsNullOrEmpty(currentRevision)
                    && string.Equals(revision, currentRevision, StringComparison.Ordinal))
                {
                    result[i] = HistoryEventKind.Current;
                }
                else if (i == items.Count - 1 && string.Equals(revision, FirstRevision, StringComparison.Ordinal))
                {
                    result[i] = HistoryEventKind.Initial;
                }
                else if (item != null && i + 1 < items.Count && items[i + 1] != null
                    && item.KeyVersion != items[i + 1].KeyVersion)
                {
                    result[i] = HistoryEventKind.KeyRotated;
                }
                else
                {
                    result[i] = HistoryEventKind.Update;
                }
            }
            return result;
        }

        public static string GlyphOf(HistoryEventKind kind)
        {
            switch (kind)
            {
                case HistoryEventKind.Current:
                    return "IconCheck";
                case HistoryEventKind.KeyRotated:
                    return "IconKey";
                case HistoryEventKind.Initial:
                    return "IconAdd";
                default:
                    return "IconUpload";
            }
        }
    }
}
