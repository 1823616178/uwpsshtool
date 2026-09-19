using System;

namespace SshTool.Core.Sync
{
    // 03-SYNC-PROTOCOL.md §6.3 phase（对应桌面端 SyncPhase，见 sync-types.ts）。
    // S13 前本文件只承载枚举与名称映射；重试/轮询语义在 S13/S14 落地。
    public enum SyncPhase
    {
        SignedOut,
        Disabled,
        Locked,
        Idle,
        Syncing,
        Synced,
        Offline,
        Error,
        Conflict,
        AuthError
    }

    public static class SyncPhaseNames
    {
        public static string ToName(SyncPhase phase)
        {
            switch (phase)
            {
                case SyncPhase.SignedOut: return "signed_out";
                case SyncPhase.Disabled: return "disabled";
                case SyncPhase.Locked: return "locked";
                case SyncPhase.Idle: return "idle";
                case SyncPhase.Syncing: return "syncing";
                case SyncPhase.Synced: return "synced";
                case SyncPhase.Offline: return "offline";
                case SyncPhase.Error: return "error";
                case SyncPhase.Conflict: return "conflict";
                case SyncPhase.AuthError: return "auth_error";
                default: throw new InvalidOperationException("未知同步相位 " + (int)phase);
            }
        }
    }
}
