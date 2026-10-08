using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Forwarding;
using SshTool.Core.Hosts;
using SshTool.Core.Models;
using SshTool.Core.Sessions;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;
using SshTool.Core.Tests.Fakes;
using Xunit;

namespace SshTool.Core.Tests.Forwarding
{
    // fix/functional-pass：隧道非交互建链——跳板链、主机密钥校验、agent 遍历、失败类别。
    public class TunnelConnectorTests
    {
        private const string Pem =
            "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA-T\n-----END OPENSSH PRIVATE KEY-----\n";

        private static readonly HostKeyInfo JumpKey = new HostKeyInfo("ssh-ed25519", "SHA256:jump", "j");
        private static readonly HostKeyInfo TargetKey = new HostKeyInfo("ssh-ed25519", "SHA256:target", "t");

        private sealed class Fixture
        {
            public readonly InMemoryFileSystem Fs = new InMemoryFileSystem();
            public readonly HostRepository Hosts;
            public readonly KnownHostRepository Known;
            public readonly KeyRepository Keys;
            public readonly InMemorySecretStore Secrets = new InMemorySecretStore();
            public readonly FakeSshSessionFactory Factory = new FakeSshSessionFactory();
            public readonly FakeSshAgent Agent = new FakeSshAgent();
            public readonly TunnelConnector Connector;

            public Fixture()
            {
                Hosts = new HostRepository(Fs);
                Known = new KnownHostRepository(Fs);
                Keys = new KeyRepository(Fs);
                Connector = new TunnelConnector(Hosts, Known, Keys, Secrets, Agent, Factory, null);
            }

            public async Task<Host> AddHostAsync(string name, string addr, AuthType type, string jumpId = null, string keyId = null)
            {
                Host h = Defaults.NewHost();
                h.Name = name;
                h.HostName = addr;
                h.Port = 22;
                h.Username = "root";
                h.AuthType = type;
                h.JumpHostId = jumpId;
                h.KeyId = keyId;
                await Hosts.AddAsync(h);
                return h;
            }

            public async Task TrustAsync(string addr, HostKeyInfo key)
            {
                await Known.AddAsync(new KnownHost
                {
                    Id = IdGenerator.NewId(),
                    Host = addr,
                    Port = 22,
                    KeyType = key.KeyType,
                    FingerprintSha256 = key.FingerprintSha256,
                    AddedAt = "2026-10-08T00:00:00Z",
                    LastSeenAt = "2026-10-08T00:00:00Z"
                }, ChangeOrigin.User);
            }

            public async Task AddKeyAsync(string id, bool encrypted, string storedPassphrase = null)
            {
                await Keys.AddAsync(new KeyEntry
                {
                    Id = id,
                    Name = "k-" + id,
                    KeyType = "ssh-ed25519",
                    Bits = 256,
                    Format = "openssh",
                    Encrypted = encrypted,
                    PublicKeyOpenSsh = "ssh-ed25519 AAAA " + id,
                    FingerprintSha256 = "SHA256:" + id,
                    CreatedAt = "2026-10-08T00:00:00Z"
                });
                await Secrets.SetAsync(SecretKeys.KeyPrivate(id), Pem);
                if (storedPassphrase != null)
                {
                    await Secrets.SetAsync(SecretKeys.KeyPassphrase(id), storedPassphrase);
                }
            }

            public FakeSshSession Enqueue(HostKeyInfo key)
            {
                var s = new FakeSshSession { HostKeyOnConnect = key, HostKeyOnJump = true };
                Factory.Queue.Enqueue(s);
                return s;
            }
        }

        [Fact]
        public async Task JumpChain_ConnectsThroughHop_AndReturnsHops()
        {
            var fx = new Fixture();
            Host jump = await fx.AddHostAsync("bastion", "10.0.0.1", AuthType.Password);
            Host target = await fx.AddHostAsync("db", "10.0.0.2", AuthType.Password, jump.Id);
            await fx.Secrets.SetAsync(SecretKeys.HostPassword(jump.Id), "jpw");
            await fx.Secrets.SetAsync(SecretKeys.HostPassword(target.Id), "tpw");
            await fx.TrustAsync("10.0.0.1", JumpKey);
            await fx.TrustAsync("10.0.0.2", TargetKey);
            FakeSshSession hop = fx.Enqueue(JumpKey);
            FakeSshSession tgt = fx.Enqueue(TargetKey);

            TunnelConnectResult r = await fx.Connector.ConnectAsync(target, 5000, CancellationToken.None);

            Assert.True(r.Success);
            Assert.Same(tgt, r.Session);
            Assert.Single(r.JumpSessions);
            Assert.Same(hop, r.JumpSessions[0]);
            Assert.Equal(new[] { "Connect", "AuthPassword" }, hop.Calls);
            Assert.Equal(new[] { "ConnectJump", "AuthPassword" }, tgt.Calls);
            Assert.Same(hop, tgt.LastJumpSession);
            Assert.Equal("jpw", hop.LastPassword);
            Assert.Equal("tpw", tgt.LastPassword);
            Assert.Equal("none", tgt.LastConnectRequest.TermType);

            TunnelConnector.Close(r);
            Assert.Equal(1, hop.CloseCount);
            Assert.Equal(1, tgt.CloseCount);
        }

        [Fact]
        public async Task UnknownJumpHostKey_FailsWithHopTitle_AndClosesSessions()
        {
            var fx = new Fixture();
            Host jump = await fx.AddHostAsync("bastion", "10.0.0.1", AuthType.Password);
            Host target = await fx.AddHostAsync("db", "10.0.0.2", AuthType.Password, jump.Id);
            FakeSshSession hop = fx.Enqueue(JumpKey);

            TunnelConnectResult r = await fx.Connector.ConnectAsync(target, 5000, CancellationToken.None);

            Assert.False(r.Success);
            Assert.Equal(TunnelConnectFailure.UnknownHostKey, r.Failure);
            Assert.Equal(SshErrorCode.UnknownHostKey, r.Code);
            Assert.Contains("bastion", r.FailedHop);
            Assert.Equal(1, hop.CloseCount);
            Assert.Single(fx.Factory.Created); // 目标会话根本未创建
            Assert.Empty(await fx.Known.GetAllAsync());
        }

        [Fact]
        public async Task TargetMismatch_ClosesHopsInReverse()
        {
            var fx = new Fixture();
            Host jump = await fx.AddHostAsync("bastion", "10.0.0.1", AuthType.Password);
            Host target = await fx.AddHostAsync("db", "10.0.0.2", AuthType.Password, jump.Id);
            await fx.Secrets.SetAsync(SecretKeys.HostPassword(jump.Id), "jpw");
            await fx.TrustAsync("10.0.0.1", JumpKey);
            await fx.TrustAsync("10.0.0.2", new HostKeyInfo("ssh-ed25519", "SHA256:other", "o"));
            FakeSshSession hop = fx.Enqueue(JumpKey);
            FakeSshSession tgt = fx.Enqueue(TargetKey);

            TunnelConnectResult r = await fx.Connector.ConnectAsync(target, 5000, CancellationToken.None);

            Assert.Equal(TunnelConnectFailure.HostKeyMismatch, r.Failure);
            Assert.Null(r.FailedHop);
            Assert.Equal(1, tgt.CloseCount);
            Assert.Equal(1, hop.CloseCount);
        }

        [Fact]
        public async Task PinnedFingerprint_AcceptedAndWrittenToKnownHosts()
        {
            var fx = new Fixture();
            Host target = await fx.AddHostAsync("db", "10.0.0.2", AuthType.Password);
            target.HostFingerprint = TargetKey.FingerprintSha256;
            await fx.Hosts.UpdateAsync(target);
            await fx.Secrets.SetAsync(SecretKeys.HostPassword(target.Id), "tpw");
            fx.Enqueue(TargetKey);

            TunnelConnectResult r = await fx.Connector.ConnectAsync(target, 5000, CancellationToken.None);

            Assert.True(r.Success);
            Assert.Empty(r.JumpSessions);
            KnownHost saved = await fx.Known.FindAsync("10.0.0.2", 22);
            Assert.NotNull(saved);
            Assert.Equal(TargetKey.FingerprintSha256, saved.FingerprintSha256);
        }

        [Fact]
        public async Task NoSavedPassword_IsNoSavedCredential()
        {
            var fx = new Fixture();
            Host target = await fx.AddHostAsync("db", "10.0.0.2", AuthType.Password);
            await fx.TrustAsync("10.0.0.2", TargetKey);
            FakeSshSession tgt = fx.Enqueue(TargetKey);

            TunnelConnectResult r = await fx.Connector.ConnectAsync(target, 5000, CancellationToken.None);

            Assert.Equal(TunnelConnectFailure.NoSavedCredential, r.Failure);
            Assert.Equal(SshErrorCode.NoLocalCredential, r.Code);
            Assert.Equal(1, tgt.CloseCount);
        }

        [Fact]
        public async Task Agent_NoKeyId_IteratesKeys_LikeTerminal()
        {
            var fx = new Fixture();
            await fx.AddKeyAsync("a", false);          // 服务器拒绝
            await fx.AddKeyAsync("b", true);           // 加密且无已存短语：无法非交互解锁 → 跳过
            await fx.AddKeyAsync("c", true, "ph");     // 加密 + 已存短语 → 解锁后成功
            Host target = await fx.AddHostAsync("db", "10.0.0.2", AuthType.Agent);
            await fx.TrustAsync("10.0.0.2", TargetKey);
            FakeSshSession tgt = fx.Enqueue(TargetKey);
            tgt.AgentResults["a"] = SshErrorCode.AuthPublicKeyFailed;
            tgt.AgentResults["c"] = SshErrorCode.None;

            TunnelConnectResult r = await fx.Connector.ConnectAsync(target, 5000, CancellationToken.None);

            Assert.True(r.Success);
            Assert.Equal(new[] { "a", "c" }, tgt.AgentAttempts);
            Assert.False(fx.Agent.IsLocked("c"));
        }

        [Fact]
        public async Task Agent_WrongStoredPassphrase_LocksAndReportsAuthFailed()
        {
            var fx = new Fixture();
            await fx.AddKeyAsync("k", true, "stale");
            Host target = await fx.AddHostAsync("db", "10.0.0.2", AuthType.Agent, null, "k");
            await fx.TrustAsync("10.0.0.2", TargetKey);
            FakeSshSession tgt = fx.Enqueue(TargetKey);
            tgt.AgentResults["k"] = SshErrorCode.PrivateKeyLoadFailed;

            TunnelConnectResult r = await fx.Connector.ConnectAsync(target, 5000, CancellationToken.None);

            Assert.Equal(TunnelConnectFailure.AuthFailed, r.Failure);
            Assert.Equal(SshErrorCode.PrivateKeyLoadFailed, r.Code);
            Assert.True(fx.Agent.IsLocked("k"));
        }

        [Fact]
        public async Task NoneAuthAccepted_SkipsCredentials()
        {
            var fx = new Fixture();
            Host target = await fx.AddHostAsync("db", "10.0.0.2", AuthType.Password);
            await fx.TrustAsync("10.0.0.2", TargetKey);
            FakeSshSession tgt = fx.Enqueue(TargetKey);
            tgt.AuthMethodsRaw = AuthMethodsInfo.AuthenticatedMarker;

            TunnelConnectResult r = await fx.Connector.ConnectAsync(target, 5000, CancellationToken.None);

            Assert.True(r.Success);
            Assert.DoesNotContain("AuthPassword", tgt.Calls);
        }

        [Fact]
        public async Task JumpCycle_IsPlanInvalid()
        {
            var fx = new Fixture();
            Host a = await fx.AddHostAsync("a", "10.0.0.1", AuthType.Password);
            Host b = await fx.AddHostAsync("b", "10.0.0.2", AuthType.Password, a.Id);
            a.JumpHostId = b.Id;
            await fx.Hosts.UpdateAsync(a);

            TunnelConnectResult r = await fx.Connector.ConnectAsync(b, 5000, CancellationToken.None);

            Assert.Equal(TunnelConnectFailure.JumpPlanInvalid, r.Failure);
            Assert.Empty(fx.Factory.Created);
        }
    }
}
