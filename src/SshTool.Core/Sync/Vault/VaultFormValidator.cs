namespace SshTool.Core.Sync.Vault
{
    using System;

    // U16 建库/解锁表单纯校验（02-UI-DESIGN.md §5.13 VaultSetupPage / VaultUnlockPage）：
    // 同步密码 ≥8 位 + 确认一致（与登录密码 ≥10 无关——同步密码不经过服务端，只用于本地
    // Argon2 派生与信封加密）；解锁密码段只判空。恢复密钥段校验在 RecoveryKeyInput。
    // 返回 null 表示通过，否则为 resw 错误文案键（Vault_*，中英由单测钉住存在性）。
    // 注意：不提供「记住同步密码」——保险库密钥解锁后由 VaultCache 持久化，重启无需再输。
    public static class VaultFormValidator
    {
        public const int MinSyncPasswordLength = 8;

        public const string SyncPasswordRequiredKey = "Vault_SyncPasswordRequired";
        public const string SyncPasswordTooShortKey = "Vault_SyncPasswordTooShort";
        public const string SyncPasswordMismatchKey = "Vault_SyncPasswordMismatch";

        public static readonly string[] AllErrorKeys = new string[]
        {
            SyncPasswordRequiredKey,
            SyncPasswordTooShortKey,
            SyncPasswordMismatchKey,
        };

        // 建库：同步密码必填 + ≥8 位 + 确认一致。
        public static string ValidateSetup(string syncPassword, string confirmPassword)
        {
            if (string.IsNullOrEmpty(syncPassword))
            {
                return SyncPasswordRequiredKey;
            }
            if (syncPassword.Length < MinSyncPasswordLength)
            {
                return SyncPasswordTooShortKey;
            }
            if (!string.Equals(syncPassword, confirmPassword, StringComparison.Ordinal))
            {
                return SyncPasswordMismatchKey;
            }
            return null;
        }

        // 解锁（密码段）：只判空（错误与否由解包结果给出「同步密码不正确」）。
        public static string ValidateUnlockPassword(string syncPassword)
        {
            if (string.IsNullOrEmpty(syncPassword))
            {
                return SyncPasswordRequiredKey;
            }
            return null;
        }
    }
}
