namespace SshTool.Core.Sync.Protocol
{
    // §4.1 preferences：文档里只有这两个键（enabled/autoSync 是本机偏好，存 VaultCache，不进文档）。
    public sealed class SyncPreferencesV1
    {
        public bool SyncPasswords { get; set; }
        public bool SyncPrivateKeys { get; set; }

        public SyncPreferencesV1 Clone()
        {
            return new SyncPreferencesV1
            {
                SyncPasswords = SyncPasswords,
                SyncPrivateKeys = SyncPrivateKeys
            };
        }
    }
}
