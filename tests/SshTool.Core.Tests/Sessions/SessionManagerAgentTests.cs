using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Hosts;
using SshTool.Core.Models;
using SshTool.Core.Sessions;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;
using SshTool.Core.Tests.Fakes;
using Xunit;

namespace SshTool.Core.Tests.Sessions
{
    // K03：SessionManager authType=agent 流程（01-DESIGN.md §12.1）。
    //   - 超时按设置项对齐；依次尝试已解锁密钥；204 lock 后试下一个；
    //   - 无可用密钥时提示选择解锁；挂起清除经 LockAgentKeys；
    //   - 多会话复用同一份托管（解锁一次，第二个会话免弹框）。
    public class SessionManagerAgentTests
    {
        private const string PemA =
            "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA-A\n-----END OPENSSH PRIVATE KEY-----\n";
        private const string PemB =
            "-----BEGIN OPENSSH PRIVATE KEY-----\nBBBB-B\n-----END OPENSSH PRIVATE KEY-----\n";

        private static readonly HostKeyInfo SampleKey =
            new HostKeyInfo("ssh-ed25519", "SHA256:abc123def456", "art");

        private sealed class FakeHostKeyPrompter : IHostKeyPrompter
        {
            public Task<bool> PromptUnknownAsync(HostKeyInfo info, string hostDisplay)
            {
                return Task.FromResult(true);
            }

            public Task<bool> PromptMismatchAsync(HostKeyInfo info, string hostDisplay, string previousFingerprint)
            {
                return Task.FromResult(false);
            }
        }

        private sealed class FakeCredentialPrompter : ICredentialPrompter
        {
            // null = 取消；非 null = 选中的 KeyId。
            public string AgentPick;
            public bool AgentCancel = true;
            public int AgentPromptCount;

            public Task<PasswordPromptResult> PromptPasswordAsync(string hostDisplay, string errorMessage)
            {
                return Task.FromResult(new PasswordPromptResult { Cancelled = true });
            }

            public Task<string> PromptPassphraseAsync(string keyName)
            {
                return Task.FromResult("ph");
            }

            public Task<IReadOnlyList<string>> PromptKeyboardInteractiveAsync(AuthPromptEventArgs args)
            {
                return Task.FromResult((IReadOnlyList<string>)new[] { "token" });
            }

            public Task<AgentUnlockResult> PromptAgentUnlockAsync(
                IReadOnlyList<AgentKeyChoice> choices, string hostDisplay)
            {
                AgentPromptCount++;
                if (AgentCancel || string.IsNullOrEmpty(AgentPick))
                {
                    return Task.FromResult(new AgentUnlockResult { Cancelled = true });
                }
                return Task.FromResult(new AgentUnlockResult { KeyId = AgentPick });
            }
        }

        private sealed class Fixture
        {
            public readonly InMemoryFileSystem Fs = new InMemoryFileSystem();
            public readonly HostRepository Hosts;
            public readonly KnownHostRepository Known;
            public readonly KeyRepository Keys;
            public readonly InMemorySecretStore Secrets = new InMemorySecretStore();
            public readonly SettingsRepository Settings;
            public readonly FakeSshSessionFactory Factory = new FakeSshSessionFactory();
            public readonly FakeHostKeyPrompter HostKeys = new FakeHostKeyPrompter();
            public readonly FakeCredentialPrompter Creds = new FakeCredentialPrompter();
            public readonly ManualTimerFactory Timers = new ManualTimerFactory();
            public readonly FakeSshAgent Agent = new FakeSshAgent();
            public readonly SessionManager Manager;

            public Fixture()
            {
                Hosts = new HostRepository(Fs);
                Known = new KnownHostRepository(Fs);
                Keys = new KeyRepository(Fs);
                Settings = new SettingsRepository(new InMemorySettingsStore());
                Manager = new SessionManager(
                    Hosts, Known, Keys, Secrets, Settings, Factory,
                    HostKeys, Creds, Timers, new ImmediateDispatcher(), null, Agent);
            }

            public async Task<Host> AddAgentHostAsync(string keyId = null)
            {
                Host host = Defaults.NewHost();
                host.Name = "agent-host";
                host.HostName = "10.0.0.2";
                host.Port = 22;
                host.Username = "root";
                host.AuthType = AuthType.Agent;
                host.KeyId = keyId;
                await Hosts.AddAsync(host);
                return host;
            }

            public async Task<KeyEntry> AddKeyAsync(string id, string name, bool encrypted)
            {
                var key = new KeyEntry
                {
                    Id = id,
                    Name = name,
                    KeyType = "ssh-ed25519",
                    Bits = 256,
                    Format = "openssh",
                    Encrypted = encrypted,
                    PublicKeyOpenSsh = "ssh-ed25519 AAAA " + name,
                    FingerprintSha256 = "SHA256:" + id,
                    CreatedAt = "2026-09-19T00:00:00Z"
                };
                await Keys.AddAsync(key);
                return key;
            }

            public FakeSshSession EnqueueAgentSession()
            {
                var native = new FakeSshSession
                {
                    HostKeyOnConnect = SampleKey,
                    ConnectResult = SshErrorCode.None,
                    OpenShellResult = SshErrorCode.None
                };
                Factory.Queue.Enqueue(native);
                return native;
            }
        }

        [Fact]
        public async Task Agent_WithoutAgent_ReturnsNoLocalCredential()
        {
            var fs = new InMemoryFileSystem();
            var hosts = new HostRepository(fs);
            var known = new KnownHostRepository(fs);
            var keys = new KeyRepository(fs);
            var secrets = new InMemorySecretStore();
            var settings = new SettingsRepository(new InMemorySettingsStore());
            var factory = new FakeSshSessionFactory();
            var manager = new SessionManager(
                hosts, known, keys, secrets, settings, factory,
                new FakeHostKeyPrompter(), new FakeCredentialPrompter(),
                new ManualTimerFactory(), new ImmediateDispatcher());

            Host host = Defaults.NewHost();
            host.Name = "a";
            host.HostName = "10.0.0.2";
            host.Username = "root";
            host.AuthType = AuthType.Agent;
            await hosts.AddAsync(host);
            factory.Queue.Enqueue(new FakeSshSession
            {
                HostKeyOnConnect = SampleKey,
                OpenShellResult = SshErrorCode.None
            });

            SessionInfo info = await manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Error, info.State);
            Assert.Equal(SshErrorCode.NoLocalCredential, info.ErrorCode);
        }

        [Fact]
        public async Task Agent_TimeoutAlignedFromSettings()
        {
            var fx = new Fixture();
            Assert.Equal(15, fx.Agent.TimeoutMinutes);

            fx.Settings.AgentKeyTimeoutMinutes = 7;
            Host host = await fx.AddAgentHostAsync();
            fx.EnqueueAgentSession();

            await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(7, fx.Agent.TimeoutMinutes);
        }

        [Fact]
        public async Task Agent_TriesUnlockedKeysInOrder()
        {
            var fx = new Fixture();
            Host host = await fx.AddAgentHostAsync();
            await fx.AddKeyAsync("k1", "first", false);
            await fx.AddKeyAsync("k2", "second", false);
            Assert.True(await fx.Agent.UnlockAsync("k1", PemA, string.Empty));
            Assert.True(await fx.Agent.UnlockAsync("k2", PemB, string.Empty));

            FakeSshSession native = fx.EnqueueAgentSession();
            native.AgentResults["k1"] = SshErrorCode.AuthPublicKeyFailed;
            native.AgentResults["k2"] = SshErrorCode.None;

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.Equal(new[] { "k1", "k2" }, native.AgentAttempts.ToArray());
            Assert.Equal(0, fx.Creds.AgentPromptCount);
        }

        [Fact]
        public async Task Agent_PassphraseFailure_LocksKeyAndTriesNext()
        {
            var fx = new Fixture();
            Host host = await fx.AddAgentHostAsync();
            await fx.AddKeyAsync("k1", "first", false);
            await fx.AddKeyAsync("k2", "second", false);
            Assert.True(await fx.Agent.UnlockAsync("k1", PemA, string.Empty));
            Assert.True(await fx.Agent.UnlockAsync("k2", PemB, string.Empty));

            FakeSshSession native = fx.EnqueueAgentSession();
            native.AgentResults["k1"] = SshErrorCode.PrivateKeyLoadFailed;
            native.AgentResults["k2"] = SshErrorCode.None;

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Connected, info.State);
            // 204 的密钥被 lock 掉（短语错误只能在首次认证时发现）。
            Assert.True(fx.Agent.IsLocked("k1"));
            Assert.False(fx.Agent.IsLocked("k2"));
        }

        [Fact]
        public async Task Agent_HostKeyId_OnlyTriesThatKey()
        {
            var fx = new Fixture();
            await fx.AddKeyAsync("k1", "first", false);
            await fx.AddKeyAsync("k2", "second", false);
            Assert.True(await fx.Agent.UnlockAsync("k1", PemA, string.Empty));
            Assert.True(await fx.Agent.UnlockAsync("k2", PemB, string.Empty));
            Host host = await fx.AddAgentHostAsync("k2");

            FakeSshSession native = fx.EnqueueAgentSession();
            native.DefaultAgentResult = SshErrorCode.None;

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.Equal(new[] { "k2" }, native.AgentAttempts.ToArray());
        }

        [Fact]
        public async Task Agent_NoUnlocked_PromptsUnlockAndConnects()
        {
            var fx = new Fixture();
            Host host = await fx.AddAgentHostAsync();
            await fx.AddKeyAsync("k1", "first", false);
            await fx.AddKeyAsync("k2", "second", false);
            await fx.Secrets.SetAsync(SecretKeys.KeyPrivate("k2"), PemB);
            fx.Creds.AgentCancel = false;
            fx.Creds.AgentPick = "k2";

            FakeSshSession native = fx.EnqueueAgentSession();
            native.DefaultAgentResult = SshErrorCode.None;

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.Equal(1, fx.Creds.AgentPromptCount);
            Assert.Equal(1, fx.Agent.UnlockCount);
            Assert.Equal(new[] { "k2" }, native.AgentAttempts.ToArray());
            Assert.False(fx.Agent.IsLocked("k2"));
        }

        [Fact]
        public async Task Agent_PromptCancelled_ReturnsNoLocalCredential()
        {
            var fx = new Fixture();
            Host host = await fx.AddAgentHostAsync();
            await fx.AddKeyAsync("k1", "first", false);
            fx.EnqueueAgentSession();
            fx.Creds.AgentCancel = true;

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Error, info.State);
            Assert.Equal(SshErrorCode.NoLocalCredential, info.ErrorCode);
            Assert.Equal(1, fx.Creds.AgentPromptCount);
        }

        [Fact]
        public async Task Agent_NoKeys_ReturnsNoLocalCredentialWithoutPrompt()
        {
            var fx = new Fixture();
            Host host = await fx.AddAgentHostAsync();
            fx.EnqueueAgentSession();

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SshErrorCode.NoLocalCredential, info.ErrorCode);
            Assert.Equal(0, fx.Creds.AgentPromptCount);
        }

        [Fact]
        public async Task Agent_UnlockedThenRejected_PromptsUnlock_RetriesOnce()
        {
            var fx = new Fixture();
            Host host = await fx.AddAgentHostAsync();
            await fx.AddKeyAsync("k1", "first", false);
            await fx.Secrets.SetAsync(SecretKeys.KeyPrivate("k1"), PemA);
            Assert.True(await fx.Agent.UnlockAsync("k1", PemA, string.Empty));
            fx.Creds.AgentCancel = false;
            fx.Creds.AgentPick = "k1";

            FakeSshSession native = fx.EnqueueAgentSession();
            native.AgentResults["k1"] = SshErrorCode.AuthPublicKeyFailed;

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Error, info.State);
            Assert.Equal(SshErrorCode.AuthPublicKeyFailed, info.ErrorCode);
            Assert.Equal(1, fx.Creds.AgentPromptCount);
            Assert.Equal(new[] { "k1", "k1" }, native.AgentAttempts.ToArray());
        }

        [Fact]
        public async Task Agent_KeyboardInteractive_FallsThrough()
        {
            var fx = new Fixture();
            Host host = await fx.AddAgentHostAsync();
            await fx.AddKeyAsync("k1", "first", false);
            Assert.True(await fx.Agent.UnlockAsync("k1", PemA, string.Empty));

            FakeSshSession native = fx.EnqueueAgentSession();
            native.AgentResults["k1"] = SshErrorCode.AuthKeyboardInteractiveFailed;
            native.KeyboardInteractiveResult = SshErrorCode.None;
            native.KiPrompts = new[] { "Code:" };

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.Contains("AuthKeyboardInteractive", native.Calls);
        }

        [Fact]
        public async Task Agent_MultiSession_SharesUnlockedKey()
        {
            var fx = new Fixture();
            Host host = await fx.AddAgentHostAsync();
            await fx.AddKeyAsync("k1", "first", false);
            await fx.Secrets.SetAsync(SecretKeys.KeyPrivate("k1"), PemA);
            fx.Creds.AgentCancel = false;
            fx.Creds.AgentPick = "k1";

            FakeSshSession first = fx.EnqueueAgentSession();
            first.DefaultAgentResult = SshErrorCode.None;
            SessionInfo info1 = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            Assert.Equal(SessionUiState.Connected, info1.State);

            FakeSshSession second = fx.EnqueueAgentSession();
            second.DefaultAgentResult = SshErrorCode.None;
            SessionInfo info2 = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            Assert.Equal(SessionUiState.Connected, info2.State);

            // 解锁一次后第二个会话免弹框（多会话复用）。
            Assert.Equal(1, fx.Creds.AgentPromptCount);
            Assert.Equal(new[] { "k1" }, second.AgentAttempts.ToArray());
        }

        [Fact]
        public void Agent_LockAgentKeys_ClearsMemory()
        {
            var fx = new Fixture();
            Assert.Equal(0, fx.Manager.LockAgentKeys());
        }

        [Fact]
        public async Task Agent_LockAgentKeys_AfterUnlock()
        {
            var fx = new Fixture();
            Assert.True(await fx.Agent.UnlockAsync("k1", PemA, string.Empty));
            Assert.Equal(1, fx.Agent.KeyCount);

            Assert.Equal(1, fx.Manager.LockAgentKeys());

            Assert.Equal(0, fx.Agent.KeyCount);
            Assert.True(fx.Agent.IsLocked("k1"));
        }

        [Fact]
        public async Task Agent_ExpiredTimeout_TreatedAsLocked()
        {
            var fx = new Fixture();
            fx.Agent.SetTimeout(1);
            Assert.True(await fx.Agent.UnlockAsync("k1", PemA, string.Empty));
            fx.Agent.Advance(System.TimeSpan.FromMinutes(2));

            Assert.True(fx.Agent.IsLocked("k1"));
            Assert.Equal(0, fx.Agent.KeyCount);
        }

        [Fact]
        public async Task Agent_UnlockRejectsGarbage_ReturnsNoLocalCredential()
        {
            var fx = new Fixture();
            Host host = await fx.AddAgentHostAsync();
            await fx.AddKeyAsync("k1", "first", false);
            await fx.Secrets.SetAsync(SecretKeys.KeyPrivate("k1"), "not-a-key");
            fx.Creds.AgentCancel = false;
            fx.Creds.AgentPick = "k1";
            fx.EnqueueAgentSession();

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SshErrorCode.PrivateKeyLoadFailed, info.ErrorCode);
        }
    }
}
