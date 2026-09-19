using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Keys;
using SshTool.Core.Models;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Protocol;
using SshTool.Core.Tests.Fakes;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // S15 验收：桌面端 test/sync-serializer.test.ts 私钥相关场景的等价用例
    //（出站矩阵/元数据/加密短语/非法内容拒绝/入站校验/指纹复算/跨设备恢复），
    // 另加 §9 出站跳过警告、入站 KeyEntry 落库/去重/绑定、同步往返稳定性。
    //
    // 对齐说明：桌面端出站失败（超限/无法解析）抛异常，中断整份序列化；
    // Lumia 端按 §9 跳过该主机私钥段并记警告（不阻塞整份文档），用例按此断言。
    public class PrivateKeySyncTests
    {
        private const string FingerprintA = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        private const string FingerprintB = "SHA256:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

        private const string OpensshKeyText =
            "-----BEGIN OPENSSH PRIVATE KEY-----\nZmFrZS1lZDI1NTE5LWtleQ==\n-----END OPENSSH PRIVATE KEY-----\n";

        private const string PemRsaKeyText =
            "-----BEGIN RSA PRIVATE KEY-----\nZmFrZS1yc2Eta2V5\n-----END RSA PRIVATE KEY-----\n";

        private const string Pkcs8KeyText =
            "-----BEGIN PRIVATE KEY-----\nZmFrZS1wa2NzOA==\n-----END PRIVATE KEY-----\n";

        private const string Pkcs8EncKeyText =
            "-----BEGIN ENCRYPTED PRIVATE KEY-----\nZmFrZS1lbmM=\n-----END ENCRYPTED PRIVATE KEY-----\n";

        private const string PpkText =
            "PuTTY-User-Key-File-3: ssh-ed25519\nEncryption: none\n";

        private const string FakePemText =
            "-----BEGIN PRIVATE KEY-----\nnot-a-real-key\n-----END PRIVATE KEY-----\n";

        private const string EncMarkerGarbageText =
            "-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\nDEK-Info: AES-128-CBC,0011223344556677\ngarbage-body\n-----END RSA PRIVATE KEY-----\n";

        private sealed class CapturingLogger : ILogger
        {
            public readonly List<string> Lines = new List<string>();

            public void Log(LogLevel level, string tag, string message)
            {
                Lines.Add(level + " [" + tag + "] " + message);
            }
        }

        private sealed class Fixture
        {
            public readonly HostRepository Hosts;
            public readonly GroupRepository Groups;
            public readonly TunnelRepository Tunnels;
            public readonly KeyRepository Keys;
            public readonly InMemorySecretStore Secrets = new InMemorySecretStore();
            public readonly FakeKeyTool Tool = new FakeKeyTool();
            public readonly CapturingLogger Log = new CapturingLogger();
            public readonly SyncLocalAdapter Adapter;

            public Fixture()
            {
                var fs = new InMemoryFileSystem();
                Hosts = new HostRepository(fs);
                Groups = new GroupRepository(fs);
                Tunnels = new TunnelRepository(fs);
                Keys = new KeyRepository(fs);
                var inspector = new KeyToolPrivateKeyInspector(Tool, Log);
                Adapter = new SyncLocalAdapter(Hosts, Groups, Tunnels, Keys, Secrets, null, inspector, Log);
            }
        }

        private static SyncPreferencesV1 Prefs(bool passwords, bool privateKeys)
        {
            return new SyncPreferencesV1 { SyncPasswords = passwords, SyncPrivateKeys = privateKeys };
        }

        private static Host PasswordHost(string id)
        {
            return new Host
            {
                Id = id,
                Name = id,
                HostName = "example.com",
                Port = 22,
                Username = "root",
                AuthType = AuthType.Password,
                HostFingerprint = "",
                Keepalive = 30
            };
        }

        private static Host KeyHost(string id, string keyId)
        {
            var h = PasswordHost(id);
            h.AuthType = AuthType.Key;
            h.KeyId = keyId;
            return h;
        }

        // secrets 非 null 键集合（排序后），对齐桌面端 Object.keys(...).sort() 比较。
        private static string[] SecretKeysOf(ServerSecrets secrets)
        {
            if (secrets == null)
            {
                return new string[0];
            }
            var keys = new List<string>();
            if (secrets.Password != null) keys.Add("password");
            if (secrets.Passphrase != null) keys.Add("passphrase");
            if (secrets.PrivateKey != null) keys.Add("privateKey");
            if (secrets.PrivateKeyEncoding != null) keys.Add("privateKeyEncoding");
            if (secrets.PrivateKeyFingerprint != null) keys.Add("privateKeyFingerprint");
            if (secrets.PrivateKeyFormat != null) keys.Add("privateKeyFormat");
            keys.Sort(StringComparer.Ordinal);
            return keys.ToArray();
        }

        private static ServerSecrets Triple(string text, string format, string fingerprint)
        {
            return new ServerSecrets
            {
                PrivateKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
                PrivateKeyEncoding = "base64",
                PrivateKeyFormat = format,
                PrivateKeyFingerprint = fingerprint
            };
        }

        // 桌面端 sensitive-field matrix 的私钥部分：false/true × false/true。
        [Theory]
        [InlineData(false, false, false, false)]
        [InlineData(true, false, true, false)]
        [InlineData(false, true, false, true)]
        [InlineData(true, true, true, true)]
        public async Task Matrix_SecretsKeysMatchDesktop(bool syncPasswords, bool syncPrivateKeys,
            bool expectPasswordKeys, bool expectPrivateKeyKeys)
        {
            var f = new Fixture();
            await f.Hosts.AddAsync(PasswordHost("h-pwd"));
            await f.Secrets.SetAsync(SecretKeys.HostPassword("h-pwd"), "pw");
            await f.Secrets.SetAsync(SecretKeys.HostPassphrase("h-pwd"), "phrase");
            await f.Hosts.AddAsync(KeyHost("h-key", "k1"));
            await f.Secrets.SetAsync(SecretKeys.KeyPrivate("k1"), OpensshKeyText);
            await f.Secrets.SetAsync(SecretKeys.KeyPassphrase("k1"), "key-phrase");
            f.Tool.DefaultInfo.Format = "openssh";
            f.Tool.DefaultInfo.FingerprintSha256 = FingerprintA;

            var doc = await f.Adapter.BuildLocalDocumentAsync(Prefs(syncPasswords, syncPrivateKeys));
            var byId = doc.Servers.ToDictionary(s => s.Profile.Id, StringComparer.Ordinal);

            // 密码主机：仅 syncPasswords 时输出 password/passphrase（key 主机的密码永不同步）。
            string[] wantPwd = expectPasswordKeys
                ? new[] { "passphrase", "password" }
                : new string[0];
            Assert.Equal(wantPwd, SecretKeysOf(byId["h-pwd"].Secrets));

            // 密钥主机：syncPasswords 时输出 passphrase；syncPrivateKeys 时输出私钥三元组。
            var wantKey = new List<string>();
            if (syncPasswords) wantKey.Add("passphrase");
            if (expectPrivateKeyKeys)
            {
                wantKey.Add("privateKey");
                wantKey.Add("privateKeyEncoding");
                wantKey.Add("privateKeyFingerprint");
                wantKey.Add("privateKeyFormat");
            }
            wantKey.Sort(StringComparer.Ordinal);
            Assert.Equal(wantKey.ToArray(), SecretKeysOf(byId["h-key"].Secrets));

            // 零偏差门禁：出站恒可经 Writer 写出。
            SyncDocumentWriter.Write(doc);
        }

        // 桌面端 emits verified OpenSSH metadata：base64/encoding/format/fingerprint 精确。
        [Fact]
        public async Task Outbound_EmitsVerifiedMetadata()
        {
            var f = new Fixture();
            await f.Hosts.AddAsync(KeyHost("h1", "k1"));
            await f.Secrets.SetAsync(SecretKeys.KeyPrivate("k1"), OpensshKeyText);
            f.Tool.DefaultInfo.Format = "openssh";
            f.Tool.DefaultInfo.FingerprintSha256 = FingerprintA;

            var doc = await f.Adapter.BuildLocalDocumentAsync(Prefs(false, true));
            var secrets = doc.Servers.Single(s => s.Profile.Id == "h1").Secrets;

            Assert.NotNull(secrets);
            Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(OpensshKeyText)), secrets.PrivateKey);
            Assert.Equal("base64", secrets.PrivateKeyEncoding);
            Assert.Equal("openssh", secrets.PrivateKeyFormat);
            Assert.Equal(FingerprintA, secrets.PrivateKeyFingerprint);
            // 短语开关未开时不输出 passphrase（桌面端同；解析用短语不进文档）。
            Assert.Null(secrets.Passphrase);
            SyncDocumentWriter.Write(doc);
        }

        // 桌面端 rejects larger than 256 KiB：桌面抛，Lumia 按 §9 跳过该主机并记警告。
        [Fact]
        public async Task Outbound_SkipsOversizeKey_WithWarning()
        {
            var f = new Fixture();
            await f.Hosts.AddAsync(KeyHost("h1", "k1"));
            var big = "-----BEGIN OPENSSH PRIVATE KEY-----\n"
                + new string('A', SyncConstants.PrivateKeyMaxBytes) + "\n-----END OPENSSH PRIVATE KEY-----\n";
            await f.Secrets.SetAsync(SecretKeys.KeyPrivate("k1"), big);

            var doc = await f.Adapter.BuildLocalDocumentAsync(Prefs(false, true));

            Assert.Null(doc.Servers.Single(s => s.Profile.Id == "h1").Secrets);
            Assert.Contains(f.Log.Lines, l => l.Contains("跳过私钥段"));
            SyncDocumentWriter.Write(doc);
        }

        // 桌面端 accepts encrypted PEM only with correct passphrase。
        [Fact]
        public async Task Outbound_EncryptedPem_RequiresCorrectPassphrase()
        {
            var f = new Fixture();
            await f.Hosts.AddAsync(KeyHost("h1", "k1"));
            await f.Secrets.SetAsync(SecretKeys.KeyPrivate("k1"), PemRsaKeyText);
            await f.Secrets.SetAsync(SecretKeys.KeyPassphrase("k1"), "correct-passphrase");
            f.Tool.InspectFunc = (text, phrase) =>
            {
                if (phrase != "correct-passphrase") return null;
                return new InspectedKeyInfo
                {
                    KeyType = "ssh-rsa",
                    Bits = 3072,
                    Format = "pem",
                    Encrypted = true,
                    FingerprintSha256 = FingerprintA
                };
            };

            var good = await f.Adapter.BuildLocalDocumentAsync(Prefs(true, true));
            var goodSecrets = good.Servers.Single(s => s.Profile.Id == "h1").Secrets;
            Assert.Equal("correct-passphrase", goodSecrets.Passphrase);
            Assert.Equal("pem", goodSecrets.PrivateKeyFormat);
            Assert.Equal(FingerprintA, goodSecrets.PrivateKeyFingerprint);

            await f.Secrets.SetAsync(SecretKeys.KeyPassphrase("k1"), "wrong-passphrase");
            var bad = await f.Adapter.BuildLocalDocumentAsync(Prefs(true, true));
            var badSecrets = bad.Servers.Single(s => s.Profile.Id == "h1").Secrets;
            // 短语仍同步（开关开），但私钥段跳过（§9；桌面端此处抛“无法解析”）。
            Assert.Equal("wrong-passphrase", badSecrets.Passphrase);
            Assert.Null(badSecrets.PrivateKey);
        }

        // 桌面端 rejects outbound fake header / PPK。
        [Theory]
        [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nnot-a-real-key\n-----END OPENSSH PRIVATE KEY-----\n")]
        [InlineData(FakePemText)]
        [InlineData(PpkText)]
        public async Task Outbound_SkipsUnsupportedContent(string keyText)
        {
            var f = new Fixture();
            await f.Hosts.AddAsync(KeyHost("h1", "k1"));
            await f.Secrets.SetAsync(SecretKeys.KeyPrivate("k1"), keyText);
            f.Tool.InspectFunc = (text, phrase) => null;

            var doc = await f.Adapter.BuildLocalDocumentAsync(Prefs(false, true));

            Assert.Null(doc.Servers.Single(s => s.Profile.Id == "h1").Secrets);
            SyncDocumentWriter.Write(doc);
        }

        // PKCS#8：即使可解析（K01 支持）也不上行——桌面端 ssh2 无法消费（互通限制）。
        [Theory]
        [InlineData(Pkcs8KeyText)]
        [InlineData(Pkcs8EncKeyText)]
        public async Task Outbound_SkipsPkcs8_ForDesktopCompat(string keyText)
        {
            var f = new Fixture();
            await f.Hosts.AddAsync(KeyHost("h1", "k1"));
            await f.Secrets.SetAsync(SecretKeys.KeyPrivate("k1"), keyText);
            f.Tool.DefaultInfo.Format = "pem";
            f.Tool.DefaultInfo.FingerprintSha256 = FingerprintA;

            var doc = await f.Adapter.BuildLocalDocumentAsync(Prefs(false, true));

            Assert.Null(doc.Servers.Single(s => s.Profile.Id == "h1").Secrets);
            Assert.Contains(f.Log.Lines, l => l.Contains("PKCS#8"));
        }

        // 桌面端 rejects inbound 非规范/非法字母表/超限。
        [Theory]
        [InlineData("Zh==")]
        [InlineData("%%%%")]
        public async Task Inbound_RejectsBadBase64(string privateKey)
        {
            var f = new Fixture();
            var inspector = new KeyToolPrivateKeyInspector(f.Tool);
            var secrets = Triple(OpensshKeyText, "openssh", FingerprintA);
            secrets.PrivateKey = privateKey;

            var ex = await Assert.ThrowsAsync<SyncApplyException>(
                () => inspector.DecodeAndVerifyAsync(secrets, "srv-1"));
            Assert.Equal(SyncApplyFailure.PrivateKeyInvalid, ex.Failure);
        }

        [Fact]
        public async Task Inbound_RejectsOversizePayload()
        {
            var f = new Fixture();
            var inspector = new KeyToolPrivateKeyInspector(f.Tool);
            var secrets = Triple(OpensshKeyText, "openssh", FingerprintA);
            secrets.PrivateKey = Convert.ToBase64String(new byte[SyncConstants.PrivateKeyMaxBytes + 1]);

            var ex = await Assert.ThrowsAsync<SyncApplyException>(
                () => inspector.DecodeAndVerifyAsync(secrets, "srv-1"));
            Assert.Equal(SyncApplyFailure.PrivateKeyInvalid, ex.Failure);
            Assert.Contains("256 KiB", ex.Message);
        }

        // 桌面端 requires encoding/format/fingerprint whenever privateKey exists。
        [Fact]
        public async Task Inbound_RequiresMetadata()
        {
            var f = new Fixture();
            var inspector = new KeyToolPrivateKeyInspector(f.Tool);
            var secrets = Triple(OpensshKeyText, "openssh", FingerprintA);
            secrets.PrivateKeyFingerprint = null;

            var ex = await Assert.ThrowsAsync<SyncApplyException>(
                () => inspector.DecodeAndVerifyAsync(secrets, "srv-1"));
            Assert.Contains("encoding、format 和 fingerprint", ex.Message);
        }

        // 桌面端 rejects forged header（header 与声明格式不一致）。
        [Fact]
        public async Task Inbound_RejectsHeaderFormatMismatch()
        {
            var f = new Fixture();
            var inspector = new KeyToolPrivateKeyInspector(f.Tool);
            var secrets = Triple(OpensshKeyText, "pem", FingerprintA);

            var ex = await Assert.ThrowsAsync<SyncApplyException>(
                () => inspector.DecodeAndVerifyAsync(secrets, "srv-1"));
            Assert.Contains("header 与声明格式不一致", ex.Message);
        }

        // 桌面端 rejects forged header（同格式但内容伪造，无法解析）。
        [Fact]
        public async Task Inbound_RejectsUnparseableContent()
        {
            var f = new Fixture();
            f.Tool.InspectFunc = (text, phrase) => null;
            var inspector = new KeyToolPrivateKeyInspector(f.Tool);
            var secrets = Triple(FakePemText, "pem", FingerprintA);

            var ex = await Assert.ThrowsAsync<SyncApplyException>(
                () => inspector.DecodeAndVerifyAsync(secrets, "srv-1"));
            Assert.Contains("无法使用同步的密码短语解析", ex.Message);
        }

        // 桌面端 recomputes and rejects mismatched fingerprint when passphrase present。
        [Fact]
        public async Task Inbound_RejectsFingerprintMismatch()
        {
            var f = new Fixture();
            f.Tool.DefaultInfo.Format = "pem";
            f.Tool.DefaultInfo.FingerprintSha256 = FingerprintA;
            var inspector = new KeyToolPrivateKeyInspector(f.Tool);
            var secrets = Triple(PemRsaKeyText, "pem", FingerprintB);
            secrets.Passphrase = "fingerprint-passphrase";

            var ex = await Assert.ThrowsAsync<SyncApplyException>(
                () => inspector.DecodeAndVerifyAsync(secrets, "srv-1"));
            Assert.Contains("公钥指纹不匹配", ex.Message);
        }

        // 加密私钥无短语：PEM 部分成功（Encrypted，无指纹）→ 只做 header/元数据校验即接受。
        [Fact]
        public async Task Inbound_AcceptsEncryptedWithoutPassphrase_PartialInfo()
        {
            var f = new Fixture();
            f.Tool.InspectFunc = (keyText, phrase) => new InspectedKeyInfo
            {
                KeyType = "ssh-rsa",
                Bits = 0,
                Format = "pem",
                Encrypted = true,
                FingerprintSha256 = ""
            };
            var inspector = new KeyToolPrivateKeyInspector(f.Tool);
            var secrets = Triple(PemRsaKeyText, "pem", FingerprintA);

            string text = await inspector.DecodeAndVerifyAsync(secrets, "srv-1");
            Assert.Equal(PemRsaKeyText, text);
        }

        // 短语缺失的加密钥（检查器直接失败 + ENCRYPTED 标记）→ 容忍分支接受。
        [Fact]
        public async Task Inbound_AcceptsMissingPassphraseHeuristic()
        {
            var f = new Fixture();
            f.Tool.InspectFunc = (keyText, phrase) => null;
            var inspector = new KeyToolPrivateKeyInspector(f.Tool);
            var secrets = Triple(EncMarkerGarbageText, "pem", FingerprintA);

            string text = await inspector.DecodeAndVerifyAsync(secrets, "srv-1");
            Assert.Equal(EncMarkerGarbageText, text);
        }

        // 桌面端 decodes and restores into another device：Apply 落库 KeyEntry 并绑定主机。
        [Fact]
        public async Task Apply_CreatesKeyEntry_BindsHost_StoresSecrets()
        {
            var f = new Fixture();
            f.Tool.DefaultInfo.Format = "openssh";
            f.Tool.DefaultInfo.FingerprintSha256 = FingerprintA;
            await f.Hosts.AddAsync(PasswordHost("h1"));
            var doc = await f.Adapter.BuildLocalDocumentAsync(Prefs(false, false));
            var server = doc.Servers.Single(s => s.Profile.Id == "h1");
            server.Profile.AuthType = AuthType.Key;
            server.Secrets = Triple(OpensshKeyText, "openssh", FingerprintA);
            server.Secrets.Passphrase = "synced-phrase";
            SyncDocumentWriter.Write(doc);

            await f.Adapter.ApplyDocumentAsync(SyncDocumentReader.Read(SyncDocumentWriter.Write(doc)));

            var keys = await f.Keys.GetAllAsync();
            var entry = Assert.Single(keys);
            Assert.Equal("h1 (synced)", entry.Name);
            Assert.Equal("openssh", entry.Format);
            Assert.Equal(FingerprintA, entry.FingerprintSha256);
            var host = await f.Hosts.GetByIdAsync("h1");
            Assert.Equal(AuthType.Key, host.AuthType);
            Assert.Equal(entry.Id, host.KeyId);
            Assert.Equal(OpensshKeyText, await f.Secrets.GetAsync(SecretKeys.KeyPrivate(entry.Id)));
            Assert.Equal("synced-phrase", await f.Secrets.GetAsync(SecretKeys.HostPassphrase("h1")));
        }

        // §5.2 第 4 条：按指纹去重——第二台同指纹主机复用已有 KeyEntry。
        [Fact]
        public async Task Apply_DedupsByFingerprint()
        {
            var f = new Fixture();
            f.Tool.DefaultInfo.Format = "openssh";
            f.Tool.DefaultInfo.FingerprintSha256 = FingerprintA;
            await f.Hosts.AddAsync(PasswordHost("h1"));
            await f.Hosts.AddAsync(PasswordHost("h2"));
            var doc = await f.Adapter.BuildLocalDocumentAsync(Prefs(false, false));
            foreach (var s in doc.Servers)
            {
                s.Profile.AuthType = AuthType.Key;
                s.Secrets = Triple(OpensshKeyText, "openssh", FingerprintA);
            }

            await f.Adapter.ApplyDocumentAsync(doc);

            var keys = await f.Keys.GetAllAsync();
            var entry = Assert.Single(keys);
            Assert.Equal(entry.Id, (await f.Hosts.GetByIdAsync("h1")).KeyId);
            Assert.Equal(entry.Id, (await f.Hosts.GetByIdAsync("h2")).KeyId);
        }

        // 远端无 secrets 时保留本机私钥与绑定（preserves B-only local secrets）。
        [Fact]
        public async Task Apply_PreservesLocalKey_WhenRemoteAbsent()
        {
            var f = new Fixture();
            var h = KeyHost("h1", "k-local");
            await f.Hosts.AddAsync(h);
            await f.Secrets.SetAsync(SecretKeys.KeyPrivate("k-local"), OpensshKeyText);
            var doc = await f.Adapter.BuildLocalDocumentAsync(Prefs(false, false));
            doc.Servers.Single(s => s.Profile.Id == "h1").Secrets = null;

            await f.Adapter.ApplyDocumentAsync(doc);

            Assert.Equal("k-local", (await f.Hosts.GetByIdAsync("h1")).KeyId);
            Assert.Equal(OpensshKeyText, await f.Secrets.GetAsync(SecretKeys.KeyPrivate("k-local")));
            Assert.Empty(await f.Keys.GetAllAsync());
        }

        // 同步往返稳定：出站 → 入站 → 出站语义一致（忽略 updatedAt），不触发上传抖动。
        [Fact]
        public async Task RoundTrip_StableAcrossApply()
        {
            var f = new Fixture();
            f.Tool.DefaultInfo.Format = "openssh";
            f.Tool.DefaultInfo.FingerprintSha256 = FingerprintA;
            await f.Hosts.AddAsync(KeyHost("h1", "k1"));
            await f.Secrets.SetAsync(SecretKeys.KeyPrivate("k1"), OpensshKeyText);

            var first = await f.Adapter.BuildLocalDocumentAsync(Prefs(false, true));
            await f.Adapter.ApplyDocumentAsync(SyncDocumentReader.Read(SyncDocumentWriter.Write(first)));
            var second = await f.Adapter.BuildLocalDocumentAsync(Prefs(false, true));

            Assert.True(SyncDocumentWriter.SameContent(first, second));
        }
    }
}
