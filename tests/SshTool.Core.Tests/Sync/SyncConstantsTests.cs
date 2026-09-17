using SshTool.Core.Sync.Protocol;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // S01 验收：常量写死期望值（与桌面端 crypto-vault.ts / sync-types.ts 逐项对齐，
    // 含 AAD 域字符串与 SPM1）。任何一处对不上都会破坏跨端互通。
    public class SyncConstantsTests
    {
        [Fact]
        public void SchemaAndLimits()
        {
            Assert.Equal(1, SyncConstants.SchemaVersion);
            Assert.Equal(2097152, SyncConstants.DocumentMaxBytes);
            Assert.Equal(262144, SyncConstants.PrivateKeyMaxBytes);
            Assert.Equal(5000, SyncConstants.ServersMax);
            Assert.Equal(10000, SyncConstants.TunnelsMax);
            Assert.Equal(1000, SyncConstants.GroupsMax);
        }

        [Fact]
        public void KdfDefaultsAndRanges()
        {
            Assert.Equal("argon2id", SyncConstants.KdfAlgorithm);
            Assert.Equal(65536, SyncConstants.DefaultKdfMemoryKib);
            Assert.Equal(3, SyncConstants.DefaultKdfIterations);
            Assert.Equal(1, SyncConstants.DefaultKdfParallelism);
            Assert.Equal(8192, SyncConstants.KdfMemoryKibMin);
            Assert.Equal(1048576, SyncConstants.KdfMemoryKibMax);
            Assert.Equal(1, SyncConstants.KdfIterationsMin);
            Assert.Equal(20, SyncConstants.KdfIterationsMax);
            Assert.Equal(1, SyncConstants.KdfParallelismMin);
            Assert.Equal(16, SyncConstants.KdfParallelismMax);
        }

        [Fact]
        public void KeyAndAesParameters()
        {
            Assert.Equal(32, SyncConstants.KeyBytes);
            Assert.Equal(16, SyncConstants.KdfSaltBytes);
            Assert.Equal("AES-256-GCM", SyncConstants.AesAlgorithm);
            Assert.Equal(12, SyncConstants.NonceBytes);
            Assert.Equal(16, SyncConstants.TagBytes);
        }

        [Fact]
        public void AadDomainsAndRecoveryPrefix()
        {
            Assert.Equal("ssh-port-mapper/sync-document/v1", SyncConstants.AadDomainDocument);
            Assert.Equal("ssh-port-mapper/vault-key/password/v1", SyncConstants.AadDomainPasswordWrap);
            Assert.Equal("ssh-port-mapper/vault-key/recovery/v1", SyncConstants.AadDomainRecoveryWrap);
            Assert.Equal("ssh-port-mapper/recovery-kek/v1", SyncConstants.HkdfInfoRecoveryKek);
            Assert.Equal("SPM1", SyncConstants.RecoveryKeyPrefix);
        }
    }
}
