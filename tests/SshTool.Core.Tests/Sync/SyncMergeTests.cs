using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SshTool.Core.Models;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Protocol;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // S09 验收：§10.1 Merge 1–5，另加 preferences 合并、secrets 部分键新增、两边删除同一实体。
    // 对齐桌面端 test/sync-merge.test.ts。
    public class SyncMergeTests
    {
        private static SyncDocumentV1 Document()
        {
            return new SyncDocumentV1
            {
                UpdatedAt = "2026-01-01T00:00:00.000Z",
                Preferences = new SyncPreferencesV1 { SyncPasswords = true, SyncPrivateKeys = true },
                Servers =
                {
                    new ServerRecord
                    {
                        Profile = new PortableServerProfile
                        {
                            Id = "server-1", Name = "base", Host = "base.example", Port = 22,
                            Username = "root", AuthType = AuthType.Password,
                            HostFingerprint = "SHA256:base", Keepalive = 30
                        },
                        Secrets = new ServerSecrets { Password = "base-password" }
                    }
                },
                Tunnels =
                {
                    new TunnelRecord
                    {
                        Id = "tunnel-1", Name = "base", ServerId = "server-1", GroupId = null,
                        Type = TunnelType.Local, ListenHost = "127.0.0.1", ListenPort = 3306,
                        DestHost = "127.0.0.1", DestPort = 3306, DestServerId = "",
                        AutoReconnect = true, Enabled = true
                    }
                },
                Groups = { new GroupRecord { Id = "group-1", Name = "base", Color = "#ffffff" } }
            };
        }

        // Merge 1：不同字段的修改自动合并
        [Fact]
        public void AutoMergesDifferentFields()
        {
            var baseDoc = Document();
            var local = baseDoc.Clone();
            var remote = baseDoc.Clone();
            local.Servers[0].Profile.Name = "local name";
            remote.Servers[0].Profile.Host = "remote.example";

            var result = SyncMerge.Merge(baseDoc, local, remote);

            Assert.Empty(result.Conflicts);
            Assert.Equal("local name", result.Document.Servers[0].Profile.Name);
            Assert.Equal("remote.example", result.Document.Servers[0].Profile.Host);
        }

        // Merge 2：同字段冲突上报并暂取本地值
        [Fact]
        public void SameFieldConflict_ReportsAndKeepsLocal()
        {
            var baseDoc = Document();
            var local = baseDoc.Clone();
            var remote = baseDoc.Clone();
            local.Tunnels[0].Name = "local";
            remote.Tunnels[0].Name = "remote";

            var result = SyncMerge.Merge(baseDoc, local, remote);

            Assert.Equal("local", result.Document.Tunnels[0].Name);
            var conflict = result.Conflicts.Single();
            Assert.Equal(SyncMergeEntity.Tunnel, conflict.Entity);
            Assert.Equal("tunnel-1", conflict.Id);
            Assert.Equal("name", conflict.Field);
            Assert.False(conflict.Sensitive);
            Assert.Equal(SyncMergeKind.Field, conflict.Kind);
        }

        // Merge 3：指纹与凭据冲突标记敏感且冲突信息不含值
        [Fact]
        public void FingerprintAndSecretConflicts_AreSensitiveAndValueFree()
        {
            var baseDoc = Document();
            var local = baseDoc.Clone();
            var remote = baseDoc.Clone();
            local.Servers[0].Profile.HostFingerprint = "SHA256:local";
            remote.Servers[0].Profile.HostFingerprint = "SHA256:remote";
            local.Servers[0].Secrets.Password = "local-secret";
            remote.Servers[0].Secrets.Password = "remote-secret";

            var result = SyncMerge.Merge(baseDoc, local, remote);

            var fingerprint = result.Conflicts.Single(c => c.Field == "profile.hostFingerprint");
            Assert.True(fingerprint.Sensitive);
            Assert.Equal(SyncMergeEntity.Server, fingerprint.Entity);
            var password = result.Conflicts.Single(c => c.Field == "secrets.password");
            Assert.True(password.Sensitive);
            // 冲突信息序列化后不得含任何一方的值
            string json = new JArray(result.Conflicts.Select(c => c.ToJson()).ToArray()).ToString(Formatting.None);
            Assert.DoesNotContain("local-secret", json);
            Assert.DoesNotContain("remote-secret", json);
            Assert.DoesNotContain("SHA256:local", json);
            Assert.DoesNotContain("SHA256:remote", json);
            // 暂取本地值
            Assert.Equal("local-secret", result.Document.Servers[0].Secrets.Password);
        }

        // Merge 4a：本地删除 vs 远端修改 → 上报且不保留（删除暂胜）
        [Fact]
        public void DeleteVsModify_LocalDeleteWinsProvisionally()
        {
            var baseDoc = Document();
            var local = baseDoc.Clone();
            var remote = baseDoc.Clone();
            local.Groups.Clear();
            remote.Groups[0].Name = "remote changed";

            var result = SyncMerge.Merge(baseDoc, local, remote);

            Assert.Empty(result.Document.Groups);
            var conflict = result.Conflicts.Single();
            Assert.Equal(SyncMergeEntity.Group, conflict.Entity);
            Assert.Equal("group-1", conflict.Id);
            Assert.Equal("*", conflict.Field);
            Assert.False(conflict.Sensitive);
            Assert.Equal(SyncMergeKind.DeleteModify, conflict.Kind);
        }

        // Merge 4b：远端删除 vs 本地修改 → 上报且保留本地
        [Fact]
        public void DeleteVsModify_RemoteDelete_KeepsLocal()
        {
            var baseDoc = Document();
            var local = baseDoc.Clone();
            var remote = baseDoc.Clone();
            local.Groups[0].Name = "local changed";
            remote.Groups.Clear();

            var result = SyncMerge.Merge(baseDoc, local, remote);

            Assert.Single(result.Document.Groups);
            Assert.Equal("local changed", result.Document.Groups[0].Name);
            Assert.Single(result.Conflicts, c => c.Kind == SyncMergeKind.DeleteModify && c.Id == "group-1");
        }

        // 两边删除同一实体：直接删除，无冲突
        [Fact]
        public void BothSidesDelete_NoConflict()
        {
            var baseDoc = Document();
            var local = baseDoc.Clone();
            var remote = baseDoc.Clone();
            local.Tunnels.Clear();
            remote.Tunnels.Clear();

            var result = SyncMerge.Merge(baseDoc, local, remote);

            Assert.Empty(result.Document.Tunnels);
            Assert.Empty(result.Conflicts);
        }

        // Merge 5：add-add 相同内容不冲突、不同内容冲突并暂取本地
        [Fact]
        public void AddAdd_IdenticalContent_NoConflict()
        {
            var baseDoc = Document();
            var local = baseDoc.Clone();
            var remote = baseDoc.Clone();
            foreach (var doc in new[] { local, remote })
            {
                doc.Groups.Add(new GroupRecord { Id = "group-2", Name = "新分组", Color = "#123456" });
            }

            var result = SyncMerge.Merge(baseDoc, local, remote);

            Assert.Empty(result.Conflicts);
            Assert.Equal(2, result.Document.Groups.Count);
        }

        [Fact]
        public void AddAdd_DifferentContent_ConflictTakesLocal()
        {
            var baseDoc = Document();
            var local = baseDoc.Clone();
            var remote = baseDoc.Clone();
            local.Groups.Add(new GroupRecord { Id = "group-2", Name = "本地名", Color = "#123456" });
            remote.Groups.Add(new GroupRecord { Id = "group-2", Name = "远端名", Color = "#654321" });

            var result = SyncMerge.Merge(baseDoc, local, remote);

            var conflict = result.Conflicts.Single();
            Assert.Equal(SyncMergeKind.AddAdd, conflict.Kind);
            Assert.Equal("group-2", conflict.Id);
            Assert.Equal("*", conflict.Field);
            Assert.False(conflict.Sensitive);
            var merged = result.Document.Groups.Single(g => g.Id == "group-2");
            Assert.Equal("本地名", merged.Name);
        }

        [Fact]
        public void AddAdd_Server_IsSensitive()
        {
            var baseDoc = Document();
            var local = baseDoc.Clone();
            var remote = baseDoc.Clone();
            local.Servers.Add(new ServerRecord { Profile = NewProfile("server-2", "甲") });
            remote.Servers.Add(new ServerRecord { Profile = NewProfile("server-2", "乙") });

            var result = SyncMerge.Merge(baseDoc, local, remote);

            Assert.Single(result.Conflicts, c => c.Kind == SyncMergeKind.AddAdd && c.Sensitive && c.Entity == SyncMergeEntity.Server);
            Assert.Equal("甲", result.Document.Servers.Single(s => s.Profile.Id == "server-2").Profile.Name);
        }

        // 补充：preferences 不同键自动合并（布尔单键不可能产生三方冲突：两边翻转即同值）
        [Fact]
        public void Preferences_DifferentKeysAutoMerge()
        {
            var baseDoc = Document();
            var local = baseDoc.Clone();
            var remote = baseDoc.Clone();
            local.Preferences.SyncPasswords = false;
            remote.Preferences.SyncPrivateKeys = false;

            var result = SyncMerge.Merge(baseDoc, local, remote);

            Assert.Empty(result.Conflicts);
            Assert.False(result.Document.Preferences.SyncPasswords);
            Assert.False(result.Document.Preferences.SyncPrivateKeys);
        }

        [Fact]
        public void Preferences_BothFlipSameKey_NoConflict()
        {
            var baseDoc = Document();
            var local = baseDoc.Clone();
            var remote = baseDoc.Clone();
            local.Preferences.SyncPasswords = false;
            remote.Preferences.SyncPasswords = false;

            var result = SyncMerge.Merge(baseDoc, local, remote);

            Assert.Empty(result.Conflicts);
            Assert.False(result.Document.Preferences.SyncPasswords);
        }

        // 补充：secrets 部分键新增（本地加 passphrase，远端不动 → 两个键都在，无冲突）
        [Fact]
        public void Secrets_PartialKeyAdded_Merges()
        {
            var baseDoc = Document();
            var local = baseDoc.Clone();
            var remote = baseDoc.Clone();
            local.Servers[0].Secrets.Passphrase = "新增短语";

            var result = SyncMerge.Merge(baseDoc, local, remote);

            Assert.Empty(result.Conflicts);
            Assert.Equal("base-password", result.Document.Servers[0].Secrets.Password);
            Assert.Equal("新增短语", result.Document.Servers[0].Secrets.Passphrase);
        }

        // secrets 键删除：本地删 password（键消失），远端不动 → password 消失
        [Fact]
        public void Secrets_KeyRemovedLocally_Removed()
        {
            var baseDoc = Document();
            var local = baseDoc.Clone();
            var remote = baseDoc.Clone();
            local.Servers[0].Secrets.Password = null;

            var result = SyncMerge.Merge(baseDoc, local, remote);

            Assert.Empty(result.Conflicts);
            Assert.Null(result.Document.Servers[0].Secrets); // 整组为空 → 键省略
        }

        [Fact]
        public void UpdatedAt_TakesMax()
        {
            var baseDoc = Document();
            var local = baseDoc.Clone();
            var remote = baseDoc.Clone();
            local.UpdatedAt = "2026-06-01T00:00:00.000Z";
            remote.UpdatedAt = "2026-03-01T00:00:00.000Z";

            var result = SyncMerge.Merge(baseDoc, local, remote);

            Assert.Equal("2026-06-01T00:00:00.000Z", result.Document.UpdatedAt);
        }

        [Fact]
        public void MergedDocument_PassesStrictValidation()
        {
            // Merge 内部经 Reader 转回强类型：能返回即已过 §4.1 全量校验。
            // 这里显式再跑一遍 Writer（含 Validate）确认可上传。
            var baseDoc = Document();
            var local = baseDoc.Clone();
            var remote = baseDoc.Clone();
            local.Servers[0].Profile.Name = "改";

            var result = SyncMerge.Merge(baseDoc, local, remote);

            string json = SyncDocumentWriter.Write(result.Document);
            Assert.Contains("\"改\"", json);
        }

        [Fact]
        public void SchemaVersionMismatch_Throws()
        {
            var baseDoc = Document();
            baseDoc.SchemaVersion = 2;

            Assert.Throws<SyncDocumentInvalidException>(() => SyncMerge.Merge(baseDoc, Document(), Document()));
        }

        private static PortableServerProfile NewProfile(string id, string name)
        {
            return new PortableServerProfile
            {
                Id = id, Name = name, Host = "h.example", Port = 22, Username = "u",
                AuthType = AuthType.Password, HostFingerprint = "", Keepalive = 30
            };
        }
    }
}
