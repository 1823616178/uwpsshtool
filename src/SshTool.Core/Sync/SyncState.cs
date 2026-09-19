using SshTool.Core.Sync.Vault;

namespace SshTool.Core.Sync
{
    // 03-SYNC-PROTOCOL.md §6.3 SyncState（UI 可观察，不持久化；对应桌面端 SyncState）。
    // revision/keyVersion/dirty/lastSyncedAt/conflict 与 VaultCache 同步；
    // nextRetryAt 由 S13 的退避重试填写（S11 恒为 null）。
    public sealed class SyncState
    {
        public SyncPhase Phase { get; set; }

        public VaultStatus Vault { get; set; }

        public SyncPreferences Preferences { get; set; } = SyncPreferences.Defaults();

        public string Revision { get; set; } = "0";

        public int KeyVersion { get; set; }

        public bool Dirty { get; set; }

        public string LastSyncedAt { get; set; }

        public string NextRetryAt { get; set; }

        public string Message { get; set; } = "";

        public SyncConflictSummary Conflict { get; set; }

        public SyncState Clone()
        {
            return new SyncState
            {
                Phase = Phase,
                Vault = Vault,
                Preferences = Preferences == null ? SyncPreferences.Defaults() : Preferences.Clone(),
                Revision = Revision,
                KeyVersion = KeyVersion,
                Dirty = Dirty,
                LastSyncedAt = LastSyncedAt,
                NextRetryAt = NextRetryAt,
                Message = Message,
                Conflict = Conflict == null ? null : Conflict.Clone()
            };
        }
    }
}
