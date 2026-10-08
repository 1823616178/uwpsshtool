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
    // fix/functional-pass：keyboard-interactive 选择（QueryAuthMethods）与私钥短语重输/保存。
    public class SessionManagerAuthMethodsTests
    {
        private const string Pem =
            "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA-K\n-----END OPENSSH PRIVATE KEY-----\n";

        private static readonly HostKeyInfo SampleKey =
            new HostKeyInfo("ssh-ed25519", "SHA256:abc123def456", "art");

        private sealed class TrustAll : IHostKeyPrompter
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

        private sealed class Prompter : ICredentialPrompter
        {
            public readonly List<int> PasswordRetriesLeft = new List<int>();
            public readonly List<bool> PassphrasePreviousWrong = new List<bool>();
            public readonly Queue<PassphrasePromptResult> Passphrases = new Queue<PassphrasePromptResult>();
            public string AgentPick;
            public int KiCount;

            public Task<PasswordPromptResult> PromptPasswordAsync(string hostDisplay, int retriesLeft)
            {
                PasswordRetriesLeft.Add(retriesLeft);
                return Task.FromResult(new PasswordPromptResult { Password = "pw" });
            }

            public Task<PassphrasePromptResult> PromptPassphraseAsync(string keyName, bool previousWrong)
            {
                PassphrasePreviousWrong.Add(previousWrong);
                if (Passphrases.Count == 0)
                {
                    return Task.FromResult(new PassphrasePromptResult { Cancelled = true });
                }
                return Task.FromResult(Passphrases.Dequeue());
            }

            public Task<IReadOnlyList<string>> PromptKeyboardInteractiveAsync(AuthPromptEventArgs args)
            {
                KiCount++;
                return Task.FromResult((IReadOnlyList<string>)new[] { "123456" });
            }

            public Task<AgentUnlockResult> PromptAgentUnlockAsync(
                IReadOnlyList<AgentKeyChoice> choices, string hostDisplay)
            {
                if (string.IsNullOrEmpty(AgentPick))
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
            public readonly KeyRepository Keys;
            public readonly InMemorySecretStore Secrets = new InMemorySecretStore();
            public readonly FakeSshSessionFactory Factory = new FakeSshSessionFactory();
            public readonly Prompter Creds = new Prompter();
            public readonly FakeSshAgent Agent = new FakeSshAgent();
            public readonly SessionManager Manager;

            public Fixture()
            {
                Hosts = new HostRepository(Fs);
                Keys = new KeyRepository(Fs);
                Manager = new SessionManager(
                    Hosts, new KnownHostRepository(Fs), Keys, Secrets,
                    new SettingsRepository(new InMemorySettingsStore()), Factory,
                    new TrustAll(), Creds, new ManualTimerFactory(), new ImmediateDispatcher(), null, Agent);
            }

            public async Task<Host> AddHostAsync(AuthType type, string keyId = null)
            {
                Host host = Defaults.NewHost();
                host.Name = "otp";
                host.HostName = "10.0.0.9";
                host.Port = 22;
                host.Username = "root";
                host.AuthType = type;
                host.KeyId = keyId;
                await Hosts.AddAsync(host);
                return host;
            }

            public async Task AddKeyAsync(string id, bool encrypted)
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
            }

            public FakeSshSession Enqueue()
            {
                var native = new FakeSshSession
                {
                    HostKeyOnConnect = SampleKey,
                    ConnectResult = SshErrorCode.None,
                    OpenShellResult = SshErrorCode.None,
                    KiPrompts = new[] { "Verification code: " }
                };
                Factory.Queue.Enqueue(native);
                return native;
            }
        }

        // ---- AuthMethodsInfo ----

        [Fact]
        public void Parse_ListsAndMarkers()
        {
            AuthMethodsInfo a = AuthMethodsInfo.Parse("publickey, Keyboard-Interactive,hostbased");
            Assert.True(a.Known);
            Assert.True(a.PublicKey);
            Assert.True(a.KeyboardInteractive);
            Assert.False(a.Password);
            Assert.False(a.Authenticated);

            Assert.False(AuthMethodsInfo.Parse(null).Known);
            Assert.False(AuthMethodsInfo.Parse(string.Empty).Known);
            Assert.True(AuthMethodsInfo.Parse(AuthMethodsInfo.AuthenticatedMarker).Authenticated);
        }

        [Theory]
        [InlineData("keyboard-interactive", AuthType.Password, true)]
        [InlineData("password,keyboard-interactive", AuthType.Password, false)]
        [InlineData("keyboard-interactive", AuthType.Key, true)]
        [InlineData("publickey,keyboard-interactive", AuthType.Agent, false)]
        [InlineData("publickey", AuthType.Password, false)]
        [InlineData("", AuthType.Password, false)]
        public void ShouldStartWithKi(string raw, AuthType type, bool expected)
        {
            Assert.Equal(expected, AuthMethodsInfo.ShouldStartWithKeyboardInteractive(AuthMethodsInfo.Parse(raw), type));
        }

        // ---- KI 选择 ----

        [Fact]
        public async Task KiOnlyServer_PasswordHost_GoesStraightToKi()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync(AuthType.Password);
            FakeSshSession native = fx.Enqueue();
            native.AuthMethodsRaw = "keyboard-interactive";

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.DoesNotContain("AuthPassword", native.Calls);
            Assert.Contains("AuthKeyboardInteractive", native.Calls);
            Assert.Empty(fx.Creds.PasswordRetriesLeft);
            Assert.Equal(1, fx.Creds.KiCount);
        }

        [Fact]
        public async Task NoneAccepted_SkipsAuthentication()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync(AuthType.Password);
            FakeSshSession native = fx.Enqueue();
            native.AuthMethodsRaw = AuthMethodsInfo.AuthenticatedMarker;

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.DoesNotContain("AuthPassword", native.Calls);
            Assert.DoesNotContain("AuthKeyboardInteractive", native.Calls);
        }

        [Fact]
        public async Task KeyPartialSuccess_ContinuesWithKi()
        {
            var fx = new Fixture();
            await fx.AddKeyAsync("k1", false);
            Host host = await fx.AddHostAsync(AuthType.Key, "k1");
            FakeSshSession native = fx.Enqueue();
            // AuthenticationMethods publickey,keyboard-interactive：公钥被接受（部分成功）后列表只剩 KI。
            native.AuthMethodsSequence.Enqueue("publickey");
            native.AuthMethodsSequence.Enqueue("keyboard-interactive");
            native.PublicKeyResult = SshErrorCode.AuthPublicKeyFailed;

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.Contains("AuthPublicKey", native.Calls);
            Assert.Contains("AuthKeyboardInteractive", native.Calls);
            Assert.Equal(2, native.AuthMethodsQueries);
        }

        [Fact]
        public async Task PasswordRejected_StillOffered_NoKi_RetriesWithCountdown()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync(AuthType.Password);
            FakeSshSession native = fx.Enqueue();
            native.AuthMethodsRaw = "password,keyboard-interactive";
            native.PasswordResult = SshErrorCode.AuthPasswordFailed;

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Error, info.State);
            Assert.DoesNotContain("AuthKeyboardInteractive", native.Calls);
            Assert.Equal(new[] { -1, 2, 1 }, fx.Creds.PasswordRetriesLeft);
        }

        [Fact]
        public async Task AgentRejectedAll_ThenKiOnly_ContinuesWithKi()
        {
            var fx = new Fixture();
            await fx.AddKeyAsync("a1", false);
            await fx.Agent.UnlockAsync("a1", Pem, string.Empty);
            Host host = await fx.AddHostAsync(AuthType.Agent);
            FakeSshSession native = fx.Enqueue();
            native.AuthMethodsSequence.Enqueue("publickey");
            native.AuthMethodsSequence.Enqueue("keyboard-interactive");
            native.DefaultAgentResult = SshErrorCode.AuthPublicKeyFailed;

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.Contains("AuthKeyboardInteractive", native.Calls);
        }

        // ---- 私钥短语 ----

        [Fact]
        public async Task WrongPassphrase_Reprompts_ThenRemembersOnSuccess()
        {
            var fx = new Fixture();
            await fx.AddKeyAsync("k2", true);
            Host host = await fx.AddHostAsync(AuthType.Key, "k2");
            FakeSshSession native = fx.Enqueue();
            native.PublicKeyResultSequence.Enqueue(SshErrorCode.PrivateKeyLoadFailed);
            native.PublicKeyResultSequence.Enqueue(SshErrorCode.None);
            fx.Creds.Passphrases.Enqueue(new PassphrasePromptResult { Passphrase = "bad" });
            fx.Creds.Passphrases.Enqueue(new PassphrasePromptResult { Passphrase = "good", Remember = true });

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.Equal(new[] { "bad", "good" }, native.Passphrases);
            Assert.Equal(new[] { false, true }, fx.Creds.PassphrasePreviousWrong);
            Assert.Equal("good", await fx.Secrets.GetAsync(SecretKeys.KeyPassphrase("k2")));
        }

        [Fact]
        public async Task Passphrase_NotRemembered_NotStored()
        {
            var fx = new Fixture();
            await fx.AddKeyAsync("k3", true);
            Host host = await fx.AddHostAsync(AuthType.Key, "k3");
            fx.Enqueue();
            fx.Creds.Passphrases.Enqueue(new PassphrasePromptResult { Passphrase = "good", Remember = false });

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.Null(await fx.Secrets.GetAsync(SecretKeys.KeyPassphrase("k3")));
        }

        [Fact]
        public async Task StoredPassphraseWrong_RepromptsAndOverwritesWhenRemembered()
        {
            var fx = new Fixture();
            await fx.AddKeyAsync("k4", true);
            await fx.Secrets.SetAsync(SecretKeys.KeyPassphrase("k4"), "stale");
            Host host = await fx.AddHostAsync(AuthType.Key, "k4");
            FakeSshSession native = fx.Enqueue();
            native.PublicKeyResultSequence.Enqueue(SshErrorCode.PrivateKeyLoadFailed);
            fx.Creds.Passphrases.Enqueue(new PassphrasePromptResult { Passphrase = "fresh", Remember = true });

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.Equal(new[] { true }, fx.Creds.PassphrasePreviousWrong);
            Assert.Equal("fresh", await fx.Secrets.GetAsync(SecretKeys.KeyPassphrase("k4")));
        }

        [Fact]
        public async Task WrongPassphrase_CancelRetry_Fails204()
        {
            var fx = new Fixture();
            await fx.AddKeyAsync("k5", true);
            Host host = await fx.AddHostAsync(AuthType.Key, "k5");
            FakeSshSession native = fx.Enqueue();
            native.PublicKeyResult = SshErrorCode.PrivateKeyLoadFailed;
            fx.Creds.Passphrases.Enqueue(new PassphrasePromptResult { Passphrase = "bad" });

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Error, info.State);
            Assert.Equal(SshErrorCode.PrivateKeyLoadFailed, info.ErrorCode);
            Assert.Equal(2, fx.Creds.PassphrasePreviousWrong.Count);
        }

        [Fact]
        public async Task AgentUnlock_WrongPassphrase_Reprompts_AndRemembers()
        {
            var fx = new Fixture();
            await fx.AddKeyAsync("a2", true);
            Host host = await fx.AddHostAsync(AuthType.Agent, "a2");
            FakeSshSession native = fx.Enqueue();
            fx.Creds.AgentPick = "a2";
            fx.Creds.Passphrases.Enqueue(new PassphrasePromptResult { Passphrase = "bad", Remember = true });
            fx.Creds.Passphrases.Enqueue(new PassphrasePromptResult { Passphrase = "good", Remember = true });
            native.AgentResultSequence.Enqueue(SshErrorCode.PrivateKeyLoadFailed);
            native.AgentResultSequence.Enqueue(SshErrorCode.None);

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.Equal(new[] { false, true }, fx.Creds.PassphrasePreviousWrong);
            Assert.Equal("good", await fx.Secrets.GetAsync(SecretKeys.KeyPassphrase("a2")));
        }
    }
}
