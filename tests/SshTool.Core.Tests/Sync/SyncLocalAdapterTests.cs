using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SshTool.Core.Models;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Protocol;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // S10 验收：B 端独有凭据保留、🏠 字段全部保留、分组删除清引用、relay 与 autoStart 保留、
    // 指纹变化拒绝、运行中隧道变更拒绝、未开启开关时文档不含 secrets。
    public class SyncLocalAdapterTests
    {
        private sealed class StubProbe : ITunnelBusyProbe
        {
            public readonly HashSet<string> Busy = new HashSet<string>(StringComparer.Ordinal);

            public bool IsBusy(string tunnelId)
            {
                return tunnelId != null && Busy.Contains(tunnelId);
            }
        }

        private sealed class Fixture
        {
            public readonly HostRepository Hosts;
            public readonly GroupRepository Groups;
            public readonly TunnelRepository Tunnels;
            public readonly KeyRepository Keys;
            public readonly InMemorySecretStore Secrets = new InMemorySecretStore();
            public readonly StubProbe Probe = new StubProbe();
            public readonly SshTool.Core.Sync.SyncLocalAdapter Adapter;

            public Fixture()
            {
                var fs = new InMemoryFileSystem();
                Hosts = new HostRepository(fs);
                Groups = new GroupRepository(fs);
                Tunnels = new TunnelRepository(fs);
                Keys = new KeyRepository(fs);
                Adapter = new SshTool.Core.Sync.SyncLocalAdapter(Hosts, Groups, Tunnels, Keys, Secrets, Probe);
            }
        }

        private static SyncPreferencesV1 Prefs(bool passwords, bool privateKeys)
        {
            return new SyncPreferencesV1 { SyncPasswords = passwords, SyncPrivateKeys = privateKeys };
        }

        private static Host NewHost(string id, string name)
        {
            return new Host
            {
                Id = id,
                Name = name,
                HostName = "example.com",
                Port = 22,
                Username = "root",
                AuthType = AuthType.Password,
                HostFingerprint = "",
                Keepalive = 30,
                TermType = "xterm-256color"
            };
        }

        private static HostGroup NewGroup(string id, string name)
        {
            return new HostGroup { Id = id, Name = name, Color = "#4F8CFF", Order = 0, Collapsed = false };
        }

        private static Tunnel NewTunnel(string id, string serverId)
        {
            return new Tunnel
            {
                Id = id,
                Name = id,
                ServerId = serverId,
                GroupId = null,
                Type = TunnelType.Local,
                ListenHost = "127.0.0.1",
                ListenPort = 8080,
                DestHost = "127.0.0.1",
                DestPort = 80,
                DestServerId = "",
                AutoReconnect = true,
                Enabled = true,
                AutoStart = false
            };
        }

        // 验收：未开启开关时文档不含 secrets（且 updatedAt 格式固定、可经 Writer 零偏差写出）。
        [Fact]
        public async Task Build_OmitsSecrets_WhenSwitchesOff()
        {
            var f = new Fixture();
            await f.Hosts.AddAsync(NewHost("h1", "web"));
            await f.Secrets.SetAsync(SecretKeys.HostPassword("h1"), "s3cret");
            await f.Secrets.SetAsync(SecretKeys.HostPassphrase("h1"), "phrase");

            var doc = await f.Adapter.BuildLocalDocumentAsync(Prefs(false, false));

            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", doc.UpdatedAt);
            Assert.False(doc.Preferences.SyncPasswords);
            Assert.False(doc.Preferences.SyncPrivateKeys);
            Assert.All(doc.Servers, s => Assert.Null(s.Secrets));
            string json = SyncDocumentWriter.Write(doc);
            Assert.DoesNotContain("\"secrets\"", json);
            var back = SyncDocumentReader.Read(json);
            Assert.True(SyncDocumentWriter.SameContent(doc, back));
        }

        // §5.1：password 仅 password 鉴权输出；passphrase 优先 key: 其次 host:。
        [Fact]
        public async Task Build_IncludesSecrets_WhenSwitchOn()
        {
            var f = new Fixture();
            var web = NewHost("h1", "web");
            await f.Hosts.AddAsync(web);
            await f.Secrets.SetAsync(SecretKeys.HostPassword("h1"), "pw1");
            await f.Secrets.SetAsync(SecretKeys.HostPassphrase("h1"), "host-phrase");

            var keyHost = NewHost("h2", "keybox");
            keyHost.AuthType = AuthType.Key;
            keyHost.KeyId = "k1";
            await f.Hosts.AddAsync(keyHost);
            await f.Secrets.SetAsync(SecretKeys.HostPassword("h2"), "must-not-sync");
            await f.Secrets.SetAsync(SecretKeys.KeyPassphrase("k1"), "key-phrase");
            await f.Secrets.SetAsync(SecretKeys.HostPassphrase("h2"), "host-phrase-ignored");

            var doc = await f.Adapter.BuildLocalDocumentAsync(Prefs(true, false));
            var byId = doc.Servers.ToDictionary(s => s.Profile.Id, StringComparer.Ordinal);

            Assert.Equal("pw1", byId["h1"].Secrets.Password);
            Assert.Equal("host-phrase", byId["h1"].Secrets.Passphrase);
            Assert.Null(byId["h2"].Secrets.Password);
            Assert.Equal("key-phrase", byId["h2"].Secrets.Passphrase);
            SyncDocumentWriter.Write(doc);
        }

        // §5.1 兜底：serverId 悬空的隧道丢弃；groupId 悬空的隧道置 null。
        [Fact]
        public async Task Build_DropsDanglingTunnel_AndNullsDanglingGroup()
        {
            var f = new Fixture();
            await f.Groups.AddAsync(NewGroup("g1", "线上"));
            await f.Hosts.AddAsync(NewHost("h1", "web"));
            await f.Tunnels.AddAsync(NewTunnel("t-ok", "h1"));
            var danglingGroup = NewTunnel("t-dangle", "h1");
            danglingGroup.GroupId = "ghost-group";
            await f.Tunnels.AddAsync(danglingGroup);
            await f.Tunnels.AddAsync(NewTunnel("t-ghost", "ghost-host"));

            var doc = await f.Adapter.BuildLocalDocumentAsync(Prefs(false, false));

            Assert.Equal(2, doc.Tunnels.Count);
            Assert.Null(doc.Tunnels.Single(t => t.Id == "t-dangle").GroupId);
            SyncDocumentWriter.Write(doc);
        }

        // 验收：B 端独有凭据保留（缺失即保留）+ 出现即覆盖（含空串清凭据）。
        [Fact]
        public async Task Apply_PreservesLocalOnlySecrets_AndAppliesPresentKeys()
        {
            var f = new Fixture();
            await f.Hosts.AddAsync(NewHost("h1", "web"));
            await f.Secrets.SetAsync(SecretKeys.HostPassword("h1"), "local-pwd");
            await f.Secrets.SetAsync(SecretKeys.HostPassphrase("h1"), "local-phrase");
            await f.Hosts.AddAsync(NewHost("h2", "db"));
            await f.Secrets.SetAsync(SecretKeys.HostPassword("h2"), "old-pwd");
            await f.Secrets.SetAsync(SecretKeys.HostPassphrase("h2"), "old-phrase");
            await f.Hosts.AddAsync(NewHost("h3", "cache"));
            await f.Secrets.SetAsync(SecretKeys.HostPassword("h3"), "to-clear");

            var doc = await f.Adapter.BuildLocalDocumentAsync(Prefs(true, false));
            var byId = doc.Servers.ToDictionary(s => s.Profile.Id, StringComparer.Ordinal);
            byId["h1"].Secrets = null;
            byId["h2"].Secrets = new ServerSecrets { Password = "remote-pwd" };
            byId["h3"].Secrets = new ServerSecrets { Password = "" };

            await f.Adapter.ApplyDocumentAsync(doc);

            Assert.Equal("local-pwd", await f.Secrets.GetAsync(SecretKeys.HostPassword("h1")));
            Assert.Equal("local-phrase", await f.Secrets.GetAsync(SecretKeys.HostPassphrase("h1")));
            Assert.Equal("remote-pwd", await f.Secrets.GetAsync(SecretKeys.HostPassword("h2")));
            Assert.Equal("old-phrase", await f.Secrets.GetAsync(SecretKeys.HostPassphrase("h2")));
            Assert.Null(await f.Secrets.GetAsync(SecretKeys.HostPassword("h3")));
        }

        // 验收：🏠 字段全部保留 + ☁ 更新 + 写入来源均为 Sync。
        [Fact]
        public async Task Apply_PreservesAllLocalFields_AndUsesSyncOrigin()
        {
            var f = new Fixture();
            var g = NewGroup("g1", "线上");
            g.Order = 7;
            g.Collapsed = true;
            await f.Groups.AddAsync(g);
            await f.Hosts.AddAsync(NewHost("h2", "跳板"));
            var h = NewHost("h1", "web");
            h.HostFingerprint = "SHA256:aaa";
            h.GroupId = "g1";
            h.KeyId = "k1";
            h.AppearanceId = "a1";
            h.JumpHostId = "h2";
            h.InitCommands = new List<string> { "uptime" };
            h.EnvVars = new Dictionary<string, string> { { "LANG", "C.UTF-8" } };
            h.TermType = "screen-256color";
            h.TmuxAutoAttach = true;
            h.TmuxSessionName = "main";
            h.BackspaceSendsCtrlH = true;
            h.SortOrder = 5;
            h.LastConnectedAt = "2026-09-18T00:00:00.000Z";
            h.Favorite = true; // W03：本机专有
            h.Extra = new JObject { ["future"] = "keep" };
            await f.Hosts.AddAsync(h);
            var t = NewTunnel("t1", "h1");
            t.GroupId = "g1";
            t.AutoStart = true;
            await f.Tunnels.AddAsync(t);

            var origins = new List<ChangeOrigin>();
            f.Hosts.Changed += (s, e) => origins.Add(e.Origin);
            f.Groups.Changed += (s, e) => origins.Add(e.Origin);
            f.Tunnels.Changed += (s, e) => origins.Add(e.Origin);

            var doc = await f.Adapter.BuildLocalDocumentAsync(Prefs(false, false));
            // W03：收藏标记绝不上云（桌面端 strictObject 多一个键整份拒绝）。
            Assert.DoesNotContain("favorite", SyncDocumentWriter.Write(doc), System.StringComparison.OrdinalIgnoreCase);
            var server = doc.Servers.Single(s => s.Profile.Id == "h1");
            server.Profile.Name = "web-改名";
            server.Profile.Host = "new.example.com";
            server.Profile.Port = 2222;
            server.Profile.Username = "admin";
            server.Profile.Keepalive = 60;
            doc.Groups.Single(x => x.Id == "g1").Name = "线上-改名";
            doc.Groups.Single(x => x.Id == "g1").Color = "#FF0000";
            doc.Tunnels.Single(x => x.Id == "t1").Name = "隧道-改名";

            await f.Adapter.ApplyDocumentAsync(doc);

            var after = await f.Hosts.GetByIdAsync("h1");
            Assert.Equal("web-改名", after.Name);
            Assert.Equal("new.example.com", after.HostName);
            Assert.Equal(2222, after.Port);
            Assert.Equal("admin", after.Username);
            Assert.Equal(60, after.Keepalive);
            Assert.Equal("SHA256:aaa", after.HostFingerprint);
            Assert.Equal("g1", after.GroupId);
            Assert.Equal("k1", after.KeyId);
            Assert.Equal("a1", after.AppearanceId);
            Assert.Equal("h2", after.JumpHostId);
            Assert.Equal(new[] { "uptime" }, after.InitCommands.ToArray());
            Assert.Equal("C.UTF-8", after.EnvVars["LANG"]);
            Assert.Equal("screen-256color", after.TermType);
            Assert.True(after.TmuxAutoAttach);
            Assert.Equal("main", after.TmuxSessionName);
            Assert.True(after.BackspaceSendsCtrlH);
            Assert.Equal(5, after.SortOrder);
            Assert.Equal("2026-09-18T00:00:00.000Z", after.LastConnectedAt);
            Assert.True(after.Favorite);
            Assert.Equal("keep", (string)after.Extra["future"]);

            var afterGroup = await f.Groups.GetByIdAsync("g1");
            Assert.Equal("线上-改名", afterGroup.Name);
            Assert.Equal(7, afterGroup.Order);
            Assert.True(afterGroup.Collapsed);

            var afterTunnel = await f.Tunnels.GetByIdAsync("t1");
            Assert.Equal("隧道-改名", afterTunnel.Name);
            Assert.True(afterTunnel.AutoStart);

            Assert.Equal(3, origins.Count);
            Assert.All(origins, o => Assert.Equal(ChangeOrigin.Sync, o));
        }

        // 验收：分组删除清引用。
        [Fact]
        public async Task Apply_GroupDeletion_ClearsHostGroupId()
        {
            var f = new Fixture();
            await f.Groups.AddAsync(NewGroup("g1", "待删"));
            await f.Groups.AddAsync(NewGroup("g2", "保留"));
            var h = NewHost("h1", "web");
            h.GroupId = "g1";
            await f.Hosts.AddAsync(h);
            var t = NewTunnel("t1", "h1");
            t.GroupId = "g1";
            await f.Tunnels.AddAsync(t);

            var doc = await f.Adapter.BuildLocalDocumentAsync(Prefs(false, false));
            doc.Groups.RemoveAll(x => x.Id == "g1");
            doc.Tunnels.Single(x => x.Id == "t1").GroupId = null;

            await f.Adapter.ApplyDocumentAsync(doc);

            Assert.Null((await f.Hosts.GetByIdAsync("h1")).GroupId);
            Assert.Null(await f.Groups.GetByIdAsync("g1"));
            Assert.NotNull(await f.Groups.GetByIdAsync("g2"));
            Assert.Null((await f.Tunnels.GetByIdAsync("t1")).GroupId);
        }

        // 验收：relay 与 autoStart 保留。
        [Fact]
        public async Task Apply_PreservesRelayAndAutoStart()
        {
            var f = new Fixture();
            await f.Hosts.AddAsync(NewHost("h1", "web"));
            await f.Hosts.AddAsync(NewHost("h2", "db"));
            var relay = NewTunnel("t-relay", "h1");
            relay.Type = TunnelType.Relay;
            relay.DestServerId = "h2";
            relay.AutoStart = true;
            await f.Tunnels.AddAsync(relay);

            var doc = await f.Adapter.BuildLocalDocumentAsync(Prefs(false, false));
            doc.Tunnels.Single(x => x.Id == "t-relay").Name = "中转-改名";

            await f.Adapter.ApplyDocumentAsync(doc);

            var after = await f.Tunnels.GetByIdAsync("t-relay");
            Assert.Equal("中转-改名", after.Name);
            Assert.Equal(TunnelType.Relay, after.Type);
            Assert.Equal("h2", after.DestServerId);
            Assert.True(after.AutoStart);
        }

        // 验收：指纹变化拒绝（整份不应用）。
        [Fact]
        public async Task Apply_RejectsFingerprintChange_WithoutApplying()
        {
            var f = new Fixture();
            var h = NewHost("h1", "web");
            h.HostFingerprint = "SHA256:aaa";
            await f.Hosts.AddAsync(h);
            await f.Secrets.SetAsync(SecretKeys.HostPassword("h1"), "local-pwd");

            var doc = await f.Adapter.BuildLocalDocumentAsync(Prefs(true, false));
            var server = doc.Servers.Single(s => s.Profile.Id == "h1");
            server.Profile.HostFingerprint = "SHA256:bbb";
            server.Profile.Name = "web-改名";
            server.Secrets.Password = "remote-pwd";

            var ex = await Assert.ThrowsAsync<SyncApplyException>(() => f.Adapter.ApplyDocumentAsync(doc));
            Assert.Equal(SyncApplyFailure.HostFingerprintChanged, ex.Failure);
            Assert.Contains("指纹", ex.Message);

            Assert.Equal("web", (await f.Hosts.GetByIdAsync("h1")).Name);
            Assert.Equal("local-pwd", await f.Secrets.GetAsync(SecretKeys.HostPassword("h1")));
        }

        // 验收：运行中隧道连接变更拒绝（整份不应用）。
        [Fact]
        public async Task Apply_RejectsBusyTunnelConnectionChange()
        {
            var f = new Fixture();
            await f.Hosts.AddAsync(NewHost("h1", "web"));
            await f.Tunnels.AddAsync(NewTunnel("t1", "h1"));
            f.Probe.Busy.Add("t1");

            var doc = await f.Adapter.BuildLocalDocumentAsync(Prefs(false, false));
            doc.Tunnels.Single(x => x.Id == "t1").ListenPort = 9090;

            var ex = await Assert.ThrowsAsync<SyncApplyException>(() => f.Adapter.ApplyDocumentAsync(doc));
            Assert.Equal(SyncApplyFailure.TunnelBusy, ex.Failure);
            Assert.Contains("运行中的隧道", ex.Message);
            Assert.Equal(8080, (await f.Tunnels.GetByIdAsync("t1")).ListenPort);
        }

        // 运行中隧道的主机连接字段变化同样拒绝；仅改名则放行。
        [Fact]
        public async Task Apply_RejectsBusyTunnelServerChange_AllowsRename()
        {
            var f = new Fixture();
            await f.Hosts.AddAsync(NewHost("h1", "web"));
            await f.Tunnels.AddAsync(NewTunnel("t1", "h1"));
            f.Probe.Busy.Add("t1");

            var doc = await f.Adapter.BuildLocalDocumentAsync(Prefs(false, false));
            doc.Servers.Single(s => s.Profile.Id == "h1").Profile.Port = 2222;
            await Assert.ThrowsAsync<SyncApplyException>(() => f.Adapter.ApplyDocumentAsync(doc));
            Assert.Equal(22, (await f.Hosts.GetByIdAsync("h1")).Port);

            f.Probe.Busy.Clear();
            await f.Adapter.ApplyDocumentAsync(doc);
            Assert.Equal(2222, (await f.Hosts.GetByIdAsync("h1")).Port);
        }

        // 删除级联（复用 ConfigService 规则）：远端删除主机 → 凭据前缀清理、jumpHostId 置 null、相关隧道消失。
        [Fact]
        public async Task Apply_DeletesMissingHost_WithCascade()
        {
            var f = new Fixture();
            await f.Hosts.AddAsync(NewHost("h1", "gone"));
            var h2 = NewHost("h2", "stay");
            h2.JumpHostId = "h1";
            await f.Hosts.AddAsync(h2);
            await f.Secrets.SetAsync(SecretKeys.HostPassword("h1"), "gone-pwd");
            await f.Secrets.SetAsync(SecretKeys.HostPassphrase("h1"), "gone-phrase");
            await f.Tunnels.AddAsync(NewTunnel("t1", "h1"));

            var doc = await f.Adapter.BuildLocalDocumentAsync(Prefs(true, false));
            doc.Servers.RemoveAll(s => s.Profile.Id == "h1");
            doc.Tunnels.RemoveAll(t => t.ServerId == "h1");

            await f.Adapter.ApplyDocumentAsync(doc);

            Assert.Null(await f.Hosts.GetByIdAsync("h1"));
            Assert.Null((await f.Hosts.GetByIdAsync("h2")).JumpHostId);
            Assert.Null(await f.Secrets.GetAsync(SecretKeys.HostPassword("h1")));
            Assert.Null(await f.Secrets.GetAsync(SecretKeys.HostPassphrase("h1")));
            Assert.Null(await f.Tunnels.GetByIdAsync("t1"));
        }

        // §5.2：远端新主机 → 🏠 字段取默认值。
        [Fact]
        public async Task Apply_NewHost_GetsLocalDefaults()
        {
            var f = new Fixture();
            var doc = new SyncDocumentV1
            {
                UpdatedAt = "2026-09-18T00:00:00.000Z",
                Preferences = Prefs(false, false),
                Servers =
                {
                    new ServerRecord
                    {
                        Profile = new PortableServerProfile
                        {
                            Id = "h-new", Name = "远端新增", Host = "new.example.com", Port = 22,
                            Username = "root", AuthType = AuthType.Password,
                            HostFingerprint = "", Keepalive = 30
                        }
                    }
                }
            };

            await f.Adapter.ApplyDocumentAsync(doc);

            var created = await f.Hosts.GetByIdAsync("h-new");
            Assert.NotNull(created);
            Assert.Null(created.GroupId);
            Assert.Null(created.KeyId);
            Assert.Null(created.JumpHostId);
            Assert.Equal("xterm-256color", created.TermType);
            Assert.Empty(created.InitCommands);
            Assert.Empty(created.EnvVars);
            Assert.Equal(0, created.SortOrder);
        }
    }
}
