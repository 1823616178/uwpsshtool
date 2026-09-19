using System;

namespace SshTool.Core.Sync
{
    // 03-SYNC-PROTOCOL.md §6.3 vault（对应桌面端 VaultState，见 sync-types.ts）。
    public enum VaultStatus
    {
        Missing,
        Locked,
        Ready
    }

    public static class VaultStatusNames
    {
        public static string ToName(VaultStatus status)
        {
            switch (status)
            {
                case VaultStatus.Missing: return "missing";
                case VaultStatus.Locked: return "locked";
                case VaultStatus.Ready: return "ready";
                default: throw new InvalidOperationException("未知保险库状态 " + (int)status);
            }
        }
    }
}
