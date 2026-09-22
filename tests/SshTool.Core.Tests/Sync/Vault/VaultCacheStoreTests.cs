using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Storage;
using SshTool.Core.Sync.Protocol;
using SshTool.Core.Sync.Vault;
using Xunit;

namespace SshTool.Core.Tests.Sync.Vault
{
    // S11 验收：VaultCache 编解码（baseDocument 走 S02 读写器）与用户绑定。
    public class VaultCacheStoreTests
    {
        private sealed class CapturingLogger : ILogger
        {
            public readonly List<string> Lines = new List<string>();

            public void Log(LogLevel level, string tag, string message)
            {
                Lines.Add(level + " [" + tag + "] " + message);
            }
        }

        private static VaultKeyEnvelope SampleEnvelope()
        {
            return new VaultKeyEnvelope
            {
                KeyVersion = 1,
                PasswordWrappedKey = "cHcx",
                PasswordWrapNonce = "bm9uY2U",
                RecoveryWrappedKey = "cmMx",
                RecoveryWrapNonce = "bm9uY2Uy",
                KdfSalt = "c2FsdA",
                KdfAlgorithm = "argon2id",
                KdfMemory = 65536,
                KdfIterations = 3,
                KdfParallelism = 1
            };
        }

        private static SyncDocumentV1 SampleDocument()
        {
            var doc = new SyncDocumentV1
            {
                SchemaVersion = 1,
                UpdatedAt = "2026-09-19T00:00:00.000Z",
                Preferences = new SyncPreferencesV1()
            };
            doc.Servers.Add(new ServerRecord
            {
                Profile = new PortableServerProfile
                {
                    Id = "srv-1",
                    Name = "web",
                    Host = "example.com",
                    Port = 22,
                    Username = "root",
                    AuthType = AuthType.Password,
                    HostFingerprint = "",
                    Keepalive = 30
                },
                Secrets = new ServerSecrets { Password = "pw-secret-1" }
            });
            doc.Groups.Add(new GroupRecord { Id = "grp-1", Name = "g", Color = "#4F8CFF" });
            doc.Tunnels.Add(new TunnelRecord
            {
                Id = "tun-1",
                Name = "t",
                ServerId = "srv-1",
                GroupId = null,
                Type = TunnelType.Local,
                ListenHost = "127.0.0.1",
                ListenPort = 8080,
                DestHost = "127.0.0.1",
                DestPort = 80,
                DestServerId = "",
                AutoReconnect = true,
                Enabled = true
            });
            // 出站门禁：测试夹具自身必须通过 Writer（零偏差）。
            SyncDocumentWriter.Write(doc);
            return doc;
        }

        private static PendingVaultSetup SamplePendingSetup()
        {
            return new PendingVaultSetup
            {
                IdempotencyKey = "11111111-1111-1111-1111-111111111111",
                VaultKeyBase64 = "dmF1bHQta2V5LTAx",
                RecoveryKey = "SPM1-RECOVERY-KEY-1",
                KeyEnvelope = SampleEnvelope(),
                CreatedAt = "2026-09-19T00:00:00.000Z"
            };
        }

        private static SyncConflictSummary SampleConflict()
        {
            var summary = new SyncConflictSummary
            {
                Reason = SyncConflictReason.MergeConflict,
                LocalRevision = "7",
                RemoteRevision = "8",
                LocalUpdatedAt = "2026-09-19T00:00:00.000Z",
                RemoteUpdatedAt = "2026-09-19T00:00:01.000Z",
                RemoteSummary = new SyncRemoteSummary
                {
                    Servers = 1,
                    Tunnels = 1,
                    Groups = 1,
                    IncludesPasswords = true,
                    IncludesPrivateKeys = false
                }
            };
            summary.Fields.Add(new SyncConflictField
            {
                Entity = SyncConflictEntity.Server,
                Id = "srv-1",
                Field = "profile.name",
                Sensitive = false
            });
            return summary;
        }

        private static VaultCacheStore NewStore(InMemorySecureFile file, CapturingLogger logger = null)
        {
            return new VaultCacheStore(file, logger);
        }

        [Fact]
        public async Task RoundTrip_FullState()
        {
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            await store.LoadAsync();

            var doc = SampleDocument();
            var remote = SampleDocument();
            var next = VaultCacheState.Initial();
            next.UserId = "u1";
            next.VaultId = "vault-1";
            next.VaultKeyBase64 = "dmF1bHQta2V5LTAx";
            next.KeyVersion = 2;
            next.Revision = "9";
            next.Preferences = new SyncPreferences
            {
                Enabled = true,
                AutoSync = false,
                SyncPasswords = true,
                SyncPrivateKeys = false
            };
            next.BaseDocument = doc;
            next.Dirty = true;
            next.LastSyncedAt = "2026-09-19T00:00:02.000Z";
            next.PendingVaultSetup = SamplePendingSetup();
            next.PendingUpload = new PendingUpload
            {
                IdempotencyKey = "22222222-2222-2222-2222-222222222222",
                BaseRevision = "8",
                Body = "{\"schemaVersion\":1}",
                Document = doc,
                CreatedAt = "2026-09-19T00:00:03.000Z"
            };
            next.Conflict = SampleConflict();
            next.ConflictRemoteDocument = remote;
            next.ConflictRemoteRevision = "8";
            await store.SaveAsync(next);

            var reloaded = NewStore(file);
            var back = await reloaded.LoadAsync();

            Assert.Equal("u1", back.UserId);
            Assert.Equal("vault-1", back.VaultId);
            Assert.Equal("dmF1bHQta2V5LTAx", back.VaultKeyBase64);
            Assert.Equal(2, back.KeyVersion);
            Assert.Equal("9", back.Revision);
            Assert.True(back.Preferences.Enabled);
            Assert.False(back.Preferences.AutoSync);
            Assert.True(back.Preferences.SyncPasswords);
            Assert.False(back.Preferences.SyncPrivateKeys);
            Assert.True(back.Dirty);
            Assert.Equal("2026-09-19T00:00:02.000Z", back.LastSyncedAt);
            Assert.True(SyncDocumentWriter.SameContent(doc, back.BaseDocument));
            Assert.Equal("11111111-1111-1111-1111-111111111111", back.PendingVaultSetup.IdempotencyKey);
            Assert.Equal("dmF1bHQta2V5LTAx", back.PendingVaultSetup.VaultKeyBase64);
            Assert.Equal("SPM1-RECOVERY-KEY-1", back.PendingVaultSetup.RecoveryKey);
            Assert.Equal(65536, back.PendingVaultSetup.KeyEnvelope.KdfMemory);
            Assert.Equal("22222222-2222-2222-2222-222222222222", back.PendingUpload.IdempotencyKey);
            Assert.Equal("8", back.PendingUpload.BaseRevision);
            Assert.Equal("{\"schemaVersion\":1}", back.PendingUpload.Body);
            Assert.True(SyncDocumentWriter.SameContent(doc, back.PendingUpload.Document));
            Assert.Equal(SyncConflictReason.MergeConflict, back.Conflict.Reason);
            Assert.Equal("7", back.Conflict.LocalRevision);
            Assert.Equal("8", back.Conflict.RemoteRevision);
            Assert.True(back.Conflict.RemoteSummary.IncludesPasswords);
            Assert.Equal(SyncConflictEntity.Server, back.Conflict.Fields[0].Entity);
            Assert.Equal("profile.name", back.Conflict.Fields[0].Field);
            Assert.True(SyncDocumentWriter.SameContent(remote, back.ConflictRemoteDocument));
            Assert.Equal("8", back.ConflictRemoteRevision);
        }

        [Fact]
        public async Task Load_EmptyFile_Initial()
        {
            var back = await NewStore(new InMemorySecureFile()).LoadAsync();
            Assert.Null(back.UserId);
            Assert.Null(back.VaultId);
            Assert.Null(back.VaultKeyBase64);
            Assert.Equal(0, back.KeyVersion);
            Assert.Equal("0", back.Revision);
            Assert.False(back.Preferences.Enabled);
            Assert.True(back.Preferences.AutoSync);
            Assert.Null(back.BaseDocument);
            Assert.False(back.Dirty);
            Assert.Null(back.PendingVaultSetup);
            Assert.Null(back.PendingUpload);
            Assert.Null(back.Conflict);
        }

        [Fact]
        public async Task Load_CorruptJson_ResetsToInitial()
        {
            var logger = new CapturingLogger();
            var file = new InMemorySecureFile();
            await file.WriteAsync(Encoding.UTF8.GetBytes("{不是json"));
            var back = await NewStore(file, logger).LoadAsync();
            Assert.Null(back.VaultId);
            Assert.Equal("0", back.Revision);
            Assert.Single(logger.Lines);
        }

        [Fact]
        public async Task Load_WrongVersion_ResetsToInitial()
        {
            var file = new InMemorySecureFile();
            await file.WriteAsync(Encoding.UTF8.GetBytes(
                @"{""version"":2,""userId"":""u1"",""vaultId"":""v1""}"));
            var back = await NewStore(file).LoadAsync();
            Assert.Null(back.UserId);
            Assert.Null(back.VaultId);
        }

        [Fact]
        public async Task Load_InvalidBaseDocument_ResetsWholeFile()
        {
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            await store.LoadAsync();
            var next = VaultCacheState.Initial();
            next.UserId = "u1";
            next.VaultId = "vault-1";
            next.VaultKeyBase64 = "a2V5";
            next.BaseDocument = SampleDocument();
            await store.SaveAsync(next);

            // 直接篡改落盘 JSON：baseDocument 注入未知键（S02 严格拒绝）。
            var raw = await file.ReadAsync();
            var root = JObject.Parse(Encoding.UTF8.GetString(raw, 0, raw.Length));
            ((JObject)root["baseDocument"])["unknownKey"] = 1;
            await file.WriteAsync(Encoding.UTF8.GetBytes(root.ToString()));

            var back = await NewStore(file).LoadAsync();
            Assert.Null(back.UserId);
            Assert.Null(back.VaultId);
            Assert.Null(back.VaultKeyBase64);
        }

        [Fact]
        public async Task BaseDocument_CanonicalizedThroughS02()
        {
            // 数组故意逆序落盘也必须读回同一语义（Writer 排序保证字节确定）。
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            await store.LoadAsync();
            var doc = SampleDocument();
            doc.Servers.Add(new ServerRecord
            {
                Profile = new PortableServerProfile
                {
                    Id = "aaa-first",
                    Name = "a",
                    Host = "h",
                    Port = 22,
                    Username = "u",
                    AuthType = AuthType.Agent,
                    HostFingerprint = "",
                    Keepalive = 0
                }
            });
            await store.UpdateAsync(next => { next.BaseDocument = doc; });
            var back = await NewStore(file).LoadAsync();
            Assert.True(SyncDocumentWriter.SameContent(doc, back.BaseDocument));
            string json = SyncDocumentWriter.Write(back.BaseDocument);
            Assert.Contains("\"servers\":[{\"profile\":{\"id\":\"aaa-first\"", json);
        }

        [Fact]
        public async Task Revision_PreservedAsString_U64Max()
        {
            // 踩坑 #8：revision 是 u64 字符串，不得转数字（double 会丢精度）。
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            await store.LoadAsync();
            await store.UpdateAsync(next => { next.Revision = "18446744073709551615"; });
            var back = await NewStore(file).LoadAsync();
            Assert.Equal("18446744073709551615", back.Revision);
        }

        [Fact]
        public async Task BindToUser_SetsFirstUser_KeepsDataOnSameUser_ResetsOnChange()
        {
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            await store.LoadAsync();

            var first = await store.BindToUserAsync("u1");
            Assert.Equal("u1", first.UserId);

            await store.UpdateAsync(next =>
            {
                next.VaultId = "vault-1";
                next.VaultKeyBase64 = "a2V5";
                next.KeyVersion = 1;
                next.Revision = "5";
            });
            var same = await store.BindToUserAsync("u1");
            Assert.Equal("vault-1", same.VaultId);
            Assert.Equal("a2V5", same.VaultKeyBase64);

            var reset = await store.BindToUserAsync("u2");
            Assert.Equal("u2", reset.UserId);
            Assert.Null(reset.VaultId);
            Assert.Null(reset.VaultKeyBase64);
            Assert.Equal(0, reset.KeyVersion);
            Assert.Equal("0", reset.Revision);
            Assert.False(reset.Preferences.Enabled);
            Assert.Null(reset.PendingVaultSetup);

            var persisted = await NewStore(file).LoadAsync();
            Assert.Equal("u2", persisted.UserId);
            Assert.Null(persisted.VaultId);
        }

        [Fact]
        public async Task BindToUser_NullOrEmpty_NoChange()
        {
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            await store.LoadAsync();
            await store.BindToUserAsync("u1");
            Assert.Equal("u1", (await store.BindToUserAsync(null)).UserId);
            Assert.Equal("u1", (await store.BindToUserAsync("")).UserId);
        }

        [Fact]
        public async Task Lock_ClearsKeyKeepsRest()
        {
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            await store.LoadAsync();
            await store.UpdateAsync(next =>
            {
                next.UserId = "u1";
                next.VaultId = "vault-1";
                next.VaultKeyBase64 = "a2V5";
                next.KeyVersion = 3;
                next.Revision = "4";
            });
            await store.LockAsync();
            var back = await NewStore(file).LoadAsync();
            Assert.Null(back.VaultKeyBase64);
            Assert.Equal("vault-1", back.VaultId);
            Assert.Equal(3, back.KeyVersion);
            Assert.Equal("4", back.Revision);

            // 无 key 时再次 Lock 是空操作（不抛、不改其他字段）。
            await store.LockAsync();
            Assert.Equal("vault-1", store.State.VaultId);
        }

        [Fact]
        public async Task Clear_ResetsAll()
        {
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            await store.LoadAsync();
            await store.BindToUserAsync("u1");
            await store.UpdateAsync(next =>
            {
                next.VaultId = "vault-1";
                next.VaultKeyBase64 = "a2V5";
            });
            await store.ClearAsync();
            var back = store.State;
            Assert.Null(back.UserId);
            Assert.Null(back.VaultId);
            Assert.Equal("0", back.Revision);
        }

        [Fact]
        public void State_ReturnsIsolatedClone()
        {
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            store.State.UserId = "mutated";
            Assert.Null(store.State.UserId);
        }

        [Fact]
        public async Task Logs_DoNotContainSecrets()
        {
            var logger = new CapturingLogger();
            var file = new InMemorySecureFile();
            var store = NewStore(file, logger);
            await store.LoadAsync();
            await store.UpdateAsync(next =>
            {
                next.VaultKeyBase64 = "dmF1bHQta2V5LVNFQ1JFVA";
                next.PendingVaultSetup = SamplePendingSetup();
                next.BaseDocument = SampleDocument();
            });
            await store.BindToUserAsync("u2");
            await file.WriteAsync(Encoding.UTF8.GetBytes("{broken"));
            await store.LoadAsync();
            string all = string.Join("\n", logger.Lines);
            Assert.DoesNotContain("dmF1bHQta2V5LVNFQ1JFVA", all);
            Assert.DoesNotContain("SPM1-RECOVERY-KEY-1", all);
            Assert.DoesNotContain("pw-secret-1", all);
        }

        [Theory]
        [InlineData(SyncConflictReason.InitialImport, "initial-import")]
        [InlineData(SyncConflictReason.RemoteDeletion, "remote-deletion")]
        [InlineData(SyncConflictReason.MergeConflict, "merge-conflict")]
        public void ConflictReason_JsonNames(SyncConflictReason reason, string name)
        {
            Assert.Equal(name, SyncConflictSummary.ReasonToJson(reason));
            Assert.Equal(reason, SyncConflictSummary.ReasonFromJson(name));
        }

        [Fact]
        public void PendingVaultSetup_Parse_RejectsEmptyFields()
        {
            var json = SamplePendingSetup().ToJson();
            json["vaultKey"] = "";
            Assert.Throws<SshTool.Core.Sync.Api.Dtos.ProtocolParseException>(
                () => PendingVaultSetup.Parse(json));
        }

        // -------- O15：标量读法（ReadVaultId / ReadVaultKeyBase64 / ReadAutoSync /
        // ReadRevision / ReadPreferences）—— 不克隆文档，取值应与 State 字段一致。 --------

        [Fact]
        public void ReadVaultId_InitialState_ReturnsNull()
        {
            var store = NewStore(new InMemorySecureFile());
            Assert.Null(store.ReadVaultId());
        }

        [Fact]
        public async Task ReadVaultId_AfterLoad_MatchesState()
        {
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            await store.LoadAsync();
            await store.UpdateAsync(s => { s.VaultId = "vault-42"; });
            Assert.Equal("vault-42", store.ReadVaultId());
            Assert.Equal(store.State.VaultId, store.ReadVaultId());
        }

        [Fact]
        public void ReadVaultKeyBase64_InitialState_ReturnsNull()
        {
            var store = NewStore(new InMemorySecureFile());
            Assert.Null(store.ReadVaultKeyBase64());
        }

        [Fact]
        public async Task ReadVaultKeyBase64_AfterSet_MatchesState()
        {
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            await store.LoadAsync();
            await store.UpdateAsync(s => { s.VaultKeyBase64 = "c2VjcmV0"; });
            Assert.Equal("c2VjcmV0", store.ReadVaultKeyBase64());
            Assert.Equal(store.State.VaultKeyBase64, store.ReadVaultKeyBase64());
        }

        [Fact]
        public void ReadAutoSync_InitialState_ReturnsTrue()
        {
            // SyncPreferences.Defaults() has AutoSync = true
            var store = NewStore(new InMemorySecureFile());
            Assert.True(store.ReadAutoSync());
        }

        [Fact]
        public async Task ReadAutoSync_AfterDisable_ReturnsFalse()
        {
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            await store.LoadAsync();
            await store.UpdateAsync(s => { s.Preferences.AutoSync = false; });
            Assert.False(store.ReadAutoSync());
        }

        [Fact]
        public void ReadRevision_InitialState_ReturnsZero()
        {
            var store = NewStore(new InMemorySecureFile());
            Assert.Equal("0", store.ReadRevision());
        }

        [Fact]
        public async Task ReadRevision_AfterUpdate_MatchesState()
        {
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            await store.LoadAsync();
            await store.UpdateAsync(s => { s.Revision = "999"; });
            Assert.Equal("999", store.ReadRevision());
            Assert.Equal(store.State.Revision, store.ReadRevision());
        }

        [Fact]
        public void ReadPreferences_InitialState_MatchesDefaults()
        {
            var store = NewStore(new InMemorySecureFile());
            var prefs = store.ReadPreferences();
            Assert.NotNull(prefs);
            Assert.False(prefs.Enabled);
            Assert.True(prefs.AutoSync);
        }

        [Fact]
        public async Task ReadPreferences_AfterUpdate_ReturnsCloneNotReference()
        {
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            await store.LoadAsync();
            await store.UpdateAsync(s => { s.Preferences.Enabled = true; });
            var prefs = store.ReadPreferences();
            Assert.True(prefs.Enabled);
            // 验证是拷贝：修改拿到的副本不影响 store 内部
            prefs.Enabled = false;
            Assert.True(store.ReadPreferences().Enabled);
        }

        [Fact]
        public void ReadPendingVaultSetup_InitialState_ReturnsNull()
        {
            var store = NewStore(new InMemorySecureFile());
            Assert.Null(store.ReadPendingVaultSetup());
        }

        [Fact]
        public async Task ReadPendingVaultSetup_AfterSet_ReturnsCloneNotReference()
        {
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            await store.LoadAsync();
            var setup = SamplePendingSetup();
            await store.UpdateAsync(s => { s.PendingVaultSetup = setup; });

            var read = store.ReadPendingVaultSetup();
            Assert.NotNull(read);
            Assert.Equal(setup.IdempotencyKey, read.IdempotencyKey);
            Assert.Equal(setup.RecoveryKey, read.RecoveryKey);

            // 验证是独立深拷贝：修改读到的对象不影响 store 内部
            read.RecoveryKey = "MODIFIED";
            Assert.Equal(setup.RecoveryKey, store.ReadPendingVaultSetup().RecoveryKey);
        }

        [Fact]
        public async Task CloneWithoutDocuments_SkipsDocuments_RetainsMetadataAndScalars()
        {
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            await store.LoadAsync();

            var fullDoc = SampleDocument();
            var fullConflict = SampleConflict();
            var fullSetup = SamplePendingSetup();

            await store.UpdateAsync(s =>
            {
                s.UserId = "u1";
                s.VaultId = "v1";
                s.VaultKeyBase64 = "key1";
                s.KeyVersion = 2;
                s.Revision = "42";
                s.Dirty = true;
                s.LastSyncedAt = "2026-09-22T00:00:00Z";
                s.Preferences = new SyncPreferences { Enabled = true, AutoSync = false, SyncPasswords = true, SyncPrivateKeys = true };
                s.BaseDocument = fullDoc;
                s.PendingVaultSetup = fullSetup;
                s.PendingUpload = new PendingUpload
                {
                    IdempotencyKey = "idemp",
                    BaseRevision = "41",
                    Body = "{}",
                    Document = fullDoc,
                    CreatedAt = "2026-09-22T00:00:00Z"
                };
                s.Conflict = fullConflict;
                s.ConflictRemoteDocument = fullDoc;
                s.ConflictRemoteRevision = "43";
            });

            // 1. 常规 State：包含所有 3 份文档
            var fullState = store.State;
            Assert.NotNull(fullState.BaseDocument);
            Assert.NotNull(fullState.PendingUpload);
            Assert.NotNull(fullState.PendingUpload.Document);
            Assert.NotNull(fullState.ConflictRemoteDocument);

            // 2. CloneWithoutDocuments：所有 3 份文档跳过（为 null），但元数据与标量完整且深拷贝
            var light = store.CloneWithoutDocuments();
            Assert.Null(light.BaseDocument);
            Assert.Null(light.PendingUpload);
            Assert.Null(light.ConflictRemoteDocument);

            Assert.Equal("u1", light.UserId);
            Assert.Equal("v1", light.VaultId);
            Assert.Equal("key1", light.VaultKeyBase64);
            Assert.Equal(2, light.KeyVersion);
            Assert.Equal("42", light.Revision);
            Assert.True(light.Dirty);
            Assert.Equal("2026-09-22T00:00:00Z", light.LastSyncedAt);
            Assert.Equal("43", light.ConflictRemoteRevision);

            Assert.NotNull(light.Preferences);
            Assert.True(light.Preferences.Enabled);
            Assert.False(light.Preferences.AutoSync);
            Assert.True(light.Preferences.SyncPasswords);

            Assert.NotNull(light.PendingVaultSetup);
            Assert.Equal(fullSetup.RecoveryKey, light.PendingVaultSetup.RecoveryKey);

            Assert.NotNull(light.Conflict);
            Assert.Equal(fullConflict.LocalRevision, light.Conflict.LocalRevision);

            // 验证克隆安全性：修改 light 中的可变引用不影响 store
            light.Preferences.Enabled = false;
            light.PendingVaultSetup.RecoveryKey = "CHANGED";
            var fresh = store.CloneWithoutDocuments();
            Assert.True(fresh.Preferences.Enabled);
            Assert.Equal(fullSetup.RecoveryKey, fresh.PendingVaultSetup.RecoveryKey);
        }
    }
}

