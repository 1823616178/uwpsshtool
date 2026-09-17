namespace SshTool.Core.Storage
{
    // 01-DESIGN.md §8.2 SecretStore 键名规范。
    public static class SecretKeys
    {
        public static string HostPassword(string hostId)
        {
            return HostPrefix(hostId) + "password";
        }

        public static string HostPassphrase(string hostId)
        {
            return HostPrefix(hostId) + "passphrase";
        }

        public static string KeyPrivate(string keyId)
        {
            return KeyPrefix(keyId) + "private";
        }

        public static string KeyPassphrase(string keyId)
        {
            return KeyPrefix(keyId) + "passphrase";
        }

        public static string HostPrefix(string hostId)
        {
            return "host:" + hostId + ":";
        }

        public static string KeyPrefix(string keyId)
        {
            return "key:" + keyId + ":";
        }
    }
}
