using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SshTool.Core.Models;
using SshTool.Core.Storage;
using SshTool.Core.Sync.Protocol;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // S02 验收：§10.1 Serializer 1–4（严格往返 / 逐条拒绝 / 引用拒绝 / 私钥规则）+ 写出字节确定。
    public class SyncDocumentTests
    {
        private static string Fixture(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "tests", "fixtures", "sync", name);
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate);
                }
                dir = dir.Parent;
            }
            throw new FileNotFoundException("找不到夹具 " + name);
        }

        private static JObject TypicalJson()
        {
            return JsonText.ParseObject(Fixture("typical.json"));
        }

        // ---------- §10.1 Serializer 1：严格往返 + 字节确定 ----------

        [Fact]
        public void RoundTrip_TypicalFixture_ByteStable()
        {
            var doc = SyncDocumentReader.Read(Fixture("typical.json"));
            string json1 = SyncDocumentWriter.Write(doc);
            string json2 = SyncDocumentWriter.Write(SyncDocumentReader.Read(json1));
            Assert.Equal(json1, json2);

            // 同一文档两次写出字节相同
            Assert.Equal(json1, SyncDocumentWriter.Write(doc));
        }

        [Fact]
        public void RoundTrip_EmptyFixture_ExactBytes()
        {
            string expected = Fixture("empty.json").Trim();
            var doc = SyncDocumentReader.Read(Fixture("empty.json"));
            Assert.Equal(expected, SyncDocumentWriter.Write(doc));
        }

        [Fact]
        public void Write_CanonicalizesKeyOrderAndSortsArrays()
        {
            string json = SyncDocumentWriter.Write(SyncDocumentReader.Read(Fixture("typical.json")));

            // 顶层键序：schemaVersion, updatedAt, preferences, servers, tunnels, groups
            AssertKeyOrder(json, "\"schemaVersion\"", "\"updatedAt\"", "\"preferences\"", "\"servers\"", "\"tunnels\"", "\"groups\"");
            // 数组序数排序（锚定数组起点断言，避免隧道的 groupId/serverId 交叉引用干扰）
            Assert.Contains("\"servers\":[{\"profile\":{\"id\":\"srv-1\"", json);
            Assert.Contains("\"tunnels\":[{\"id\":\"tun-1\"", json);
            Assert.Contains("\"groups\":[{\"id\":\"grp-1\"", json);
            // secrets 键序：password 在 passphrase 前（fixture 中顺序相反，证明被规范化）
            AssertKeyOrder(json, "\"password\"", "\"passphrase\"");
            // 无缩进、无 BOM（字符串层面无空白结构性字符）
            Assert.DoesNotContain("\n", json);
            Assert.DoesNotContain("\r", json);
        }

        [Fact]
        public void WriteUtf8_NoBom()
        {
            var bytes = SyncDocumentWriter.WriteUtf8(SyncDocumentReader.Read(Fixture("empty.json")));
            Assert.NotEqual(0xEF, bytes[0]);
        }

        [Fact]
        public void SameContent_IgnoresUpdatedAt()
        {
            var a = SyncDocumentReader.Read(Fixture("typical.json"));
            var b = a.Clone();
            b.UpdatedAt = "2027-01-01T00:00:00.000Z";
            Assert.True(SyncDocumentWriter.SameContent(a, b));

            b.Tunnels[0].Name = "改名";
            Assert.False(SyncDocumentWriter.SameContent(a, b));
        }

        private static void AssertKeyOrder(string json, params string[] keys)
        {
            int prev = -1;
            foreach (var key in keys)
            {
                int idx = json.IndexOf(key, StringComparison.Ordinal);
                Assert.True(idx > prev, key + " 顺序不对");
                prev = idx;
            }
        }

        // ---------- §10.1 Serializer 2：逐条拒绝 §4.1 约束 ----------

        public static IEnumerable<object[]> RejectionCases()
        {
            // 结构层（Reader）：未知键 / 缺键 / 类型不符
            yield return Case("根-未知键", j => j.Add("bogus", 1), "bogus");
            yield return Case("根-缺 tunnels", j => j.Remove("tunnels"), "tunnels");
            yield return Case("preferences-多键", j => Prefs(j).Add("enabled", true), "preferences.enabled");
            yield return Case("preferences-缺键", j => Prefs(j).Remove("syncPasswords"), "preferences.syncPasswords");
            yield return Case("profile-未知键", j => Profile(j).Add("privateKeyPath", "x"), "profile.privateKeyPath");
            yield return Case("tunnel-未知键", j => Tunnel(j, 0).Add("autoStart", true), "autoStart");
            yield return Case("group-未知键", j => Group(j, 0).Add("order", 1), "order");
            yield return Case("secrets-显式 null", j => Secrets(j)["password"] = JValue.CreateNull(), "password");
            yield return Case("secrets-整个 null", j => Server(j, 0)["secrets"] = JValue.CreateNull(), "secrets");
            yield return Case("port-浮点", j => Profile(j)["port"] = 22.5, "port");
            yield return Case("port-字符串", j => Profile(j)["port"] = "22", "port");
            yield return Case("enabled-数字", j => Tunnel(j, 0)["enabled"] = 1, "enabled");
            yield return Case("groupId-数字", j => Tunnel(j, 1)["groupId"] = 5, "groupId");

            // 值约束（Validator）
            yield return Case("schemaVersion≠1", j => j["schemaVersion"] = 2, "schemaVersion");
            yield return Case("updatedAt-格式", j => j["updatedAt"] = "2026-09-17", "updatedAt");
            yield return Case("id-空", j => Profile(j)["id"] = "", ".id");
            yield return Case("name-空", j => Profile(j)["name"] = "", "name");
            yield return Case("name-256", j => Profile(j)["name"] = new string('n', 256), "name");
            yield return Case("host-空", j => Profile(j)["host"] = "", "host");
            yield return Case("host-1025", j => Profile(j)["host"] = new string('h', 1025), "host");
            yield return Case("port-0", j => Profile(j)["port"] = 0, "port");
            yield return Case("port-65536", j => Profile(j)["port"] = 65536, "port");
            yield return Case("username-空", j => Profile(j)["username"] = "", "username");
            yield return Case("authType-非法", j => Profile(j)["authType"] = "bogus", "authType");
            yield return Case("hostFingerprint-256", j => Profile(j)["hostFingerprint"] = new string('f', 256), "hostFingerprint");
            yield return Case("keepalive-负", j => Profile(j)["keepalive"] = -1, "keepalive");
            yield return Case("keepalive-3601", j => Profile(j)["keepalive"] = 3601, "keepalive");

            yield return Case("secrets-password-4097", j => Secrets(j)["password"] = new string('p', 4097), "password");
            yield return Case("secrets-passphrase-4097", j => Secrets(j)["passphrase"] = new string('p', 4097), "passphrase");

            yield return Case("tunnel-name-256", j => Tunnel(j, 0)["name"] = new string('n', 256), "name");
            yield return Case("tunnel-serverId-空", j => Tunnel(j, 0)["serverId"] = "", "serverId");
            yield return Case("tunnel-groupId-空串", j => Tunnel(j, 1)["groupId"] = "", "groupId");
            yield return Case("tunnel-type-非法", j => Tunnel(j, 0)["type"] = "bogus", "type");
            yield return Case("tunnel-listenHost-1025", j => Tunnel(j, 0)["listenHost"] = new string('h', 1025), "listenHost");
            yield return Case("tunnel-listenPort-0", j => Tunnel(j, 0)["listenPort"] = 0, "listenPort");
            yield return Case("tunnel-destHost-1025", j => Tunnel(j, 0)["destHost"] = new string('d', 1025), "destHost");
            yield return Case("tunnel-destPort-负", j => Tunnel(j, 0)["destPort"] = -1, "destPort");
            yield return Case("tunnel-destPort-65536", j => Tunnel(j, 0)["destPort"] = 65536, "destPort");
            yield return Case("tunnel-destServerId-256", j => Tunnel(j, 0)["destServerId"] = new string('d', 256), "destServerId");

            yield return Case("group-id-空", j => Group(j, 0)["id"] = "", ".id");
            yield return Case("group-name-空", j => Group(j, 0)["name"] = "", "name");
            yield return Case("group-color-非hex", j => Group(j, 0)["color"] = "red", "color");
            yield return Case("group-color-7位", j => Group(j, 0)["color"] = "#1234567", "color");

            // 跨条目引用（§10.1 Serializer 3）
            yield return Case("隧道引用不存在主机", j => Tunnel(j, 0)["serverId"] = "ghost", "serverId");
            yield return Case("隧道引用不存在分组", j => Tunnel(j, 1)["groupId"] = "ghost", "groupId");
            yield return Case("relay 引用不存在目标", j => Tunnel(j, 0)["destServerId"] = "ghost", "destServerId");

            // 私钥规则（§10.1 Serializer 4，线格式层；指纹复算属 S10 native KeyTool）
            yield return Case("私钥-非规范base64", j => SetPrivateKey(j, "Zh=="), "privateKey");
            yield return Case("私钥-非法字母表", j => SetPrivateKey(j, "%%%%"), "privateKey");
            yield return Case("私钥-元数据缺失", j => Secrets(j)["privateKeyFingerprint"] = null, "privateKey");
            yield return Case("私钥-元数据脱离", j => Secrets(j).Add("privateKeyFormat", "pem"), "privateKey");
            yield return Case("私钥-encoding非base64", j =>
            {
                SetPrivateKey(j, ValidKeyB64());
                Secrets(j)["privateKeyEncoding"] = "raw";
            }, "privateKeyEncoding");
            yield return Case("私钥-format非法", j =>
            {
                SetPrivateKey(j, ValidKeyB64());
                Secrets(j)["privateKeyFormat"] = "ppk";
            }, "privateKeyFormat");
            yield return Case("私钥-指纹短", j =>
            {
                SetPrivateKey(j, ValidKeyB64());
                Secrets(j)["privateKeyFingerprint"] = "SHA256:short";
            }, "privateKeyFingerprint");
            yield return Case("私钥-指纹带填充", j =>
            {
                SetPrivateKey(j, ValidKeyB64());
                Secrets(j)["privateKeyFingerprint"] = "SHA256:" + new string('A', 42) + "=";
            }, "privateKeyFingerprint");
            yield return Case("私钥-header与声明不符", j =>
            {
                SetPrivateKey(j, B64("-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----\n"));
                Secrets(j)["privateKeyFormat"] = "pem";
            }, "privateKeyFormat");
            yield return Case("私钥-伪造header", j => SetPrivateKey(j, B64("不是私钥不是私钥")), "privateKey");
        }

        private static object[] Case(string name, Action<JObject> mutate, string pathFragment)
        {
            return new object[] { name, mutate, pathFragment };
        }

        [Theory]
        [MemberData(nameof(RejectionCases))]
        public void Rejects_InvalidDocument(string name, Action<JObject> mutate, string pathFragment)
        {
            var json = TypicalJson();
            mutate(json);
            var ex = Assert.Throws<SyncDocumentInvalidException>(() => SyncDocumentReader.Read(json.ToString(Formatting.None)));
            Assert.True(ex.Path.Contains(pathFragment), name + "：路径 " + ex.Path + " 不含 " + pathFragment);
        }

        [Fact]
        public void Rejects_NotJson()
        {
            var ex = Assert.Throws<SyncDocumentInvalidException>(() => SyncDocumentReader.Read("{ 坏掉的"));
            Assert.Equal("$", ex.Path);
        }

        [Fact]
        public void Rejects_ServersOverMax()
        {
            var doc = ValidModel();
            for (int i = 0; i < SyncConstants.ServersMax + 1; i++)
            {
                doc.Servers.Add(new ServerRecord { Profile = ValidProfile("s" + i) });
            }
            var ex = Assert.Throws<SyncDocumentInvalidException>(() => SyncDocumentValidator.Validate(doc));
            Assert.Equal("servers", ex.Path);
        }

        [Fact]
        public void Rejects_TunnelsOverMax()
        {
            var doc = ValidModel();
            for (int i = 0; i < SyncConstants.TunnelsMax + 1; i++)
            {
                doc.Tunnels.Add(new TunnelRecord
                {
                    Id = "t" + i, Name = "t", ServerId = "s1", Type = TunnelType.Local,
                    ListenHost = "127.0.0.1", ListenPort = 8080, DestHost = "h", DestPort = 80,
                    DestServerId = "", AutoReconnect = true, Enabled = true
                });
            }
            var ex = Assert.Throws<SyncDocumentInvalidException>(() => SyncDocumentValidator.Validate(doc));
            Assert.Equal("tunnels", ex.Path);
        }

        [Fact]
        public void Rejects_GroupsOverMax()
        {
            var doc = ValidModel();
            for (int i = 0; i < SyncConstants.GroupsMax + 1; i++)
            {
                doc.Groups.Add(new GroupRecord { Id = "g" + i, Name = "g", Color = "#FFFFFF" });
            }
            var ex = Assert.Throws<SyncDocumentInvalidException>(() => SyncDocumentValidator.Validate(doc));
            Assert.Equal("groups", ex.Path);
        }

        [Fact]
        public void Rejects_DocumentOver2MiB_OnWrite()
        {
            var doc = ValidModel();
            for (int i = 0; i < SyncConstants.TunnelsMax; i++)
            {
                doc.Tunnels.Add(new TunnelRecord
                {
                    Id = "tunnel-" + i.ToString("D5"),
                    Name = new string('隧', 60), // 180 字节 UTF-8，总量 > 2 MiB
                    ServerId = "s1",
                    Type = TunnelType.Local,
                    ListenHost = "127.0.0.1",
                    ListenPort = 8080,
                    DestHost = "internal.example.com",
                    DestPort = 8080,
                    DestServerId = "",
                    AutoReconnect = true,
                    Enabled = true
                });
            }
            var ex = Assert.Throws<SyncDocumentInvalidException>(() => SyncDocumentWriter.Write(doc));
            Assert.Contains("2 MiB", ex.Reason);
        }

        // ---------- 私钥正例 ----------

        [Fact]
        public void Accepts_ValidPrivateKeyMetadata()
        {
            var json = TypicalJson();
            SetPrivateKey(json, B64("-----BEGIN OPENSSH PRIVATE KEY-----\r\nAAAA\n-----END OPENSSH PRIVATE KEY-----\n"));
            var doc = SyncDocumentReader.Read(json.ToString(Formatting.None));
            Assert.Equal("openssh", doc.Servers[0].Secrets.PrivateKeyFormat);
            // 往返稳定
            Assert.Equal(SyncDocumentWriter.Write(doc), SyncDocumentWriter.Write(SyncDocumentReader.Read(SyncDocumentWriter.Write(doc))));
        }

        // ---------- 辅助 ----------

        private static string B64(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        }

        private static string ValidKeyB64()
        {
            return B64("-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----\n");
        }

        // 给 srv-2（index 0 的 server，带 secrets）设置合法私钥元数据
        private static void SetPrivateKey(JObject root, string base64)
        {
            var secrets = Secrets(root);
            secrets["privateKey"] = base64;
            secrets["privateKeyEncoding"] = "base64";
            secrets["privateKeyFormat"] = "openssh";
            secrets["privateKeyFingerprint"] = "SHA256:" + new string('A', 43);
        }

        private static JObject Prefs(JObject root) { return (JObject)root["preferences"]; }
        private static JObject Server(JObject root, int i) { return (JObject)((JArray)root["servers"])[i]; }
        private static JObject Profile(JObject root) { return (JObject)Server(root, 0)["profile"]; }
        private static JObject Secrets(JObject root) { return (JObject)Server(root, 0)["secrets"]; }
        private static JObject Tunnel(JObject root, int i) { return (JObject)((JArray)root["tunnels"])[i]; }
        private static JObject Group(JObject root, int i) { return (JObject)((JArray)root["groups"])[i]; }

        private static PortableServerProfile ValidProfile(string id)
        {
            return new PortableServerProfile
            {
                Id = id,
                Name = "n",
                Host = "h",
                Port = 22,
                Username = "u",
                AuthType = AuthType.Password,
                HostFingerprint = "",
                Keepalive = 30
            };
        }

        private static SyncDocumentV1 ValidModel()
        {
            return new SyncDocumentV1
            {
                UpdatedAt = "2026-09-17T08:00:00.000Z",
                Preferences = new SyncPreferencesV1(),
                Servers = { new ServerRecord { Profile = ValidProfile("s1") } }
            };
        }
    }
}
