using System.Collections.Generic;
using System.Linq;
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
    // 评审（PR #1）：公钥认证传给 ISshSession 的私钥字节数组，认证结束后必须被清零
    // （成功与失败两条路径）。
    public class SessionManagerKeyAuthZeroingTests
    {
        private const string Pem =
            "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA-ZERO-ME\n-----END OPENSSH PRIVATE KEY-----\n";

        private static readonly HostKeyInfo SampleKey =
            new HostKeyInfo("ssh-ed25519", "SHA256:zero123", "art");

        private sealed class TrustAllPrompter : IHostKeyPrompter
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

        private sealed class NoCredentialPrompter : ICredentialPrompter
        {
            public Task<PasswordPromptResult> PromptPasswordAsync(string hostDisplay, string errorMessage)
            {
                return Task.FromResult(new PasswordPromptResult { Cancelled = true });
            }

            public Task<string> PromptPassphraseAsync(string keyName)
            {
                return Task.FromResult(string.Empty);
            }

            public Task<IReadOnlyList<string>> PromptKeyboardInteractiveAsync(AuthPromptEventArgs args)
            {
                return Task.FromResult((IReadOnlyList<string>)new string[0]);
            }

            public Task<AgentUnlockResult> PromptAgentUnlockAsync(
                IReadOnlyList<AgentKeyChoice> choices, string hostDisplay)
            {
                return Task.FromResult(new AgentUnlockResult { Cancelled = true });
            }
        }

        [Theory]
        [InlineData(SshErrorCode.None)]
        [InlineData(SshErrorCode.AuthPublicKeyFailed)]
        public async Task PrivateKeyBytes_AreZeroedAfterAuthentication(SshErrorCode authResult)
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
                new TrustAllPrompter(), new NoCredentialPrompter(),
                new ManualTimerFactory(), new ImmediateDispatcher());

            await keys.AddAsync(new KeyEntry
            {
                Id = "kz",
                Name = "zero",
                KeyType = "ssh-ed25519",
                Bits = 256,
                Format = "openssh",
                Encrypted = false,
                PublicKeyOpenSsh = "ssh-ed25519 AAAA zero",
                FingerprintSha256 = "SHA256:kz",
                CreatedAt = "2026-10-08T00:00:00Z"
            });
            await secrets.SetAsync(SecretKeys.KeyPrivate("kz"), Pem);

            Host host = Defaults.NewHost();
            host.Name = "key-host";
            host.HostName = "10.0.0.3";
            host.Port = 22;
            host.Username = "root";
            host.AuthType = AuthType.Key;
            host.KeyId = "kz";
            await hosts.AddAsync(host);

            var native = new FakeSshSession
            {
                HostKeyOnConnect = SampleKey,
                ConnectResult = SshErrorCode.None,
                PublicKeyResult = authResult,
                OpenShellResult = SshErrorCode.None
            };
            factory.Queue.Enqueue(native);

            await manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Contains("AuthPublicKey", native.Calls);
            Assert.NotNull(native.LastPrivateKey);
            Assert.NotEmpty(native.LastPrivateKey);
            Assert.True(native.LastPrivateKey.All(b => b == 0), "私钥字节数组未被清零");
        }
    }
}
