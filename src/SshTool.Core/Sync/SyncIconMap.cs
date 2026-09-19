using System;

namespace SshTool.Core.Sync
{
    // MainPage 同步图标规格（02-UI-DESIGN.md §5.1 CommandBar 第三个图标）。
    // GlyphKey/BrushKey 引用 Themes/Tokens.xaml 的资源键（App 层按当前主题解析）；
    // Core 只做相位→规格的纯映射（可单测），不碰任何 XAML 类型。
    public sealed class SyncIconSpec
    {
        public string GlyphKey { get; set; }

        // null = 默认前景（跟随 CommandBar）。
        public string BrushKey { get; set; }

        // 同步中：图标持续旋转（MainPage 用 Storyboard 实现）。
        public bool Spin { get; set; }
    }

    public static class SyncIconMap
    {
        public static SyncIconSpec ForPhase(SyncPhase phase)
        {
            switch (phase)
            {
                case SyncPhase.SignedOut:
                    // 云+斜杠（灰）：尚未登录。
                    return new SyncIconSpec { GlyphKey = "IconCloudOff", BrushKey = "AppTextDimBrush" };
                case SyncPhase.Disabled:
                    // 云（灰）：已登录但同步未启用。
                    return new SyncIconSpec { GlyphKey = "IconCloud", BrushKey = "AppTextDimBrush" };
                case SyncPhase.Locked:
                    // 锁：保险库锁定，待解锁。
                    return new SyncIconSpec { GlyphKey = "IconLock", BrushKey = null };
                case SyncPhase.Idle:
                    return new SyncIconSpec { GlyphKey = "IconSync", BrushKey = null };
                case SyncPhase.Syncing:
                    return new SyncIconSpec { GlyphKey = "IconSync", BrushKey = null, Spin = true };
                case SyncPhase.Synced:
                    // 云+勾：用成功色表达“已对齐”。
                    return new SyncIconSpec { GlyphKey = "IconSync", BrushKey = "AppSuccessBrush" };
                case SyncPhase.Offline:
                    return new SyncIconSpec { GlyphKey = "IconCloudOff", BrushKey = "AppWarningBrush" };
                case SyncPhase.Conflict:
                    // 感叹号（警告色）。
                    return new SyncIconSpec { GlyphKey = "IconWarning", BrushKey = "AppWarningBrush" };
                case SyncPhase.Error:
                    return new SyncIconSpec { GlyphKey = "IconSyncError", BrushKey = "AppDangerBrush" };
                case SyncPhase.AuthError:
                    return new SyncIconSpec { GlyphKey = "IconSyncError", BrushKey = "AppDangerBrush" };
                default:
                    throw new InvalidOperationException("未知同步相位 " + (int)phase);
            }
        }
    }
}
