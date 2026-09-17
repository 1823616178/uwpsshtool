using SshTool.Core.Models;
using SshTool.Core.Sync.Protocol;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // S01：文档模型 Clone 深拷贝；ServerSecrets null = 键不存在的语义在克隆后保持。
    public class SyncDocumentModelTests
    {
        private static SyncDocumentV1 FullDocument()
        {
            return new SyncDocumentV1
            {
                UpdatedAt = "2026-09-17T08:00:00.000Z",
                Preferences = new SyncPreferencesV1 { SyncPasswords = true, SyncPrivateKeys = false },
                Servers =
                {
                    new ServerRecord
                    {
                        Profile = new PortableServerProfile
                        {
                            Id = "s1",
                            Name = "主机",
                            Host = "example.com",
                            Port = 22,
                            Username = "root",
                            AuthType = AuthType.Key,
                            HostFingerprint = "SHA256:abc",
                            Keepalive = 30
                        },
                        Secrets = new ServerSecrets
                        {
                            Password = null,          // 键不存在
                            Passphrase = "短语",
                            PrivateKey = "AAAA",
                            PrivateKeyEncoding = "base64",
                            PrivateKeyFormat = "openssh",
                            PrivateKeyFingerprint = "SHA256:xyz"
                        }
                    },
                    new ServerRecord
                    {
                        Profile = new PortableServerProfile { Id = "s2", Name = "无 secrets" },
                        Secrets = null
                    }
                },
                Tunnels =
                {
                    new TunnelRecord
                    {
                        Id = "t1",
                        Name = "隧道",
                        ServerId = "s1",
                        GroupId = "g1",
                        Type = TunnelType.Relay,
                        ListenHost = "127.0.0.1",
                        ListenPort = 8080,
                        DestHost = "",
                        DestPort = 0,
                        DestServerId = "s2",
                        AutoReconnect = true,
                        Enabled = false
                    }
                },
                Groups = { new GroupRecord { Id = "g1", Name = "分组", Color = "#4F8CFF" } }
            };
        }

        [Fact]
        public void Clone_DeepCopiesEverything()
        {
            var original = FullDocument();
            var copy = original.Clone();

            Assert.Equal("2026-09-17T08:00:00.000Z", copy.UpdatedAt);
            Assert.Equal(1, copy.SchemaVersion);
            Assert.True(copy.Preferences.SyncPasswords);
            Assert.Equal(2, copy.Servers.Count);
            Assert.Single(copy.Tunnels);
            Assert.Single(copy.Groups);

            // 列表与嵌套对象不共享
            Assert.NotSame(original.Servers, copy.Servers);
            Assert.NotSame(original.Servers[0], copy.Servers[0]);
            Assert.NotSame(original.Servers[0].Profile, copy.Servers[0].Profile);
            Assert.NotSame(original.Servers[0].Secrets, copy.Servers[0].Secrets);
            Assert.NotSame(original.Tunnels, copy.Tunnels);
            Assert.NotSame(original.Groups, copy.Groups);

            // 修改副本不影响原对象
            copy.Servers[0].Profile.Name = "改";
            copy.Servers[0].Secrets.Passphrase = "改";
            copy.Tunnels[0].Name = "改";
            copy.Groups[0].Color = "#000000";
            copy.Preferences.SyncPasswords = false;
            Assert.Equal("主机", original.Servers[0].Profile.Name);
            Assert.Equal("短语", original.Servers[0].Secrets.Passphrase);
            Assert.Equal("隧道", original.Tunnels[0].Name);
            Assert.Equal("#4F8CFF", original.Groups[0].Color);
            Assert.True(original.Preferences.SyncPasswords);
        }

        [Fact]
        public void Clone_PreservesNullSemantics()
        {
            var copy = FullDocument().Clone();

            Assert.Null(copy.Servers[0].Secrets.Password);   // null 表示键不存在，克隆后仍是 null
            Assert.Null(copy.Servers[1].Secrets);            // 无 secrets 键
            Assert.Null(copy.Servers[1].Profile.Host);
        }

        [Fact]
        public void Defaults_SchemaVersionOne_AndEmptyLists()
        {
            var doc = new SyncDocumentV1();
            Assert.Equal(1, doc.SchemaVersion);
            Assert.NotNull(doc.Preferences);
            Assert.Empty(doc.Servers);
            Assert.Empty(doc.Tunnels);
            Assert.Empty(doc.Groups);
        }
    }
}
