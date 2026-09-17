namespace SshTool.Core.Sync.Protocol
{
    // 03-SYNC-PROTOCOL.md §3.1：与桌面端 crypto-vault.ts / sync-types.ts 逐项对齐
    // （native sync_params.h 用同名常量，S03 落地）。改动任一值都会破坏跨端互通，禁止。
    public static class SyncConstants
    {
        public const int SchemaVersion = 1;

        public const string KdfAlgorithm = "argon2id";
        public const int DefaultKdfMemoryKib = 65536;
        public const int DefaultKdfIterations = 3;
        public const int DefaultKdfParallelism = 1;
        // 解锁时校验信封 KDF 参数的允许范围
        public const int KdfMemoryKibMin = 8192;
        public const int KdfMemoryKibMax = 1048576;
        public const int KdfIterationsMin = 1;
        public const int KdfIterationsMax = 20;
        public const int KdfParallelismMin = 1;
        public const int KdfParallelismMax = 16;

        public const int KeyBytes = 32;
        public const int KdfSaltBytes = 16;

        public const string AesAlgorithm = "AES-256-GCM";
        public const int NonceBytes = 12;
        public const int TagBytes = 16; // tag 拼在密文末尾

        public const string AadDomainDocument = "ssh-port-mapper/sync-document/v1";
        public const string AadDomainPasswordWrap = "ssh-port-mapper/vault-key/password/v1";
        public const string AadDomainRecoveryWrap = "ssh-port-mapper/vault-key/recovery/v1";
        public const string HkdfInfoRecoveryKek = "ssh-port-mapper/recovery-kek/v1";

        public const string RecoveryKeyPrefix = "SPM1";

        public const int DocumentMaxBytes = 2097152;   // 2 MiB
        public const int PrivateKeyMaxBytes = 262144;  // 256 KiB

        public const int ServersMax = 5000;
        public const int TunnelsMax = 10000;
        public const int GroupsMax = 1000;
    }
}
