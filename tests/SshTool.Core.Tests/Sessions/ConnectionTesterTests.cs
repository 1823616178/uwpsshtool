using System.Threading;
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
    public class ConnectionTesterTests
    {
        private static readonly HostKeyInfo Key =
            new HostKeyInfo("ssh-ed25519", "SHA256:testfp", "art");

        private sealed class AcceptAllKeys : IHostKeyPrompter
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

        private static Host Draft()
        {
            Host h = Defaults.NewHost();
            h.Name = "t";
            h.HostName = "10.0.0.8";
            h.Username = "u";
            h.Port = 22;
            return h;
        }

        private static ConnectionTester Tester(FakeSshSessionFactory factory, KnownHostRepository known)
        {
            return new ConnectionTester(
                known,
                new InMemorySecretStore(),
                factory,
                new AcceptAllKeys(),
                new ImmediateDispatcher())
            {
                TimeoutMs = 200
            };
        }

        [Fact]
        public async Task EmptyHost_FailsResolve()
        {
            var fs = new InMemoryFileSystem();
            var tester = Tester(new FakeSshSessionFactory(), new KnownHostRepository(fs));
            Host h = Draft();
            h.HostName = "";
            TestConnectResult r = await tester.RunAsync(h, CredentialDraft.ForNew(), CancellationToken.None);
            Assert.False(r.Success);
            Assert.Equal(TestConnectStage.Resolve, r.Stage);
            Assert.Equal("Error_101", r.MessageKey);
        }

        [Fact]
        public async Task HandshakeFail_UsesErrorKey()
        {
            var factory = new FakeSshSessionFactory();
            factory.Queue.Enqueue(new FakeSshSession { ConnectResult = SshErrorCode.ConnectionRefused });
            var tester = Tester(factory, new KnownHostRepository(new InMemoryFileSystem()));
            CredentialDraft creds = CredentialDraft.ForNew();
            creds.SetPassword("x");
            TestConnectResult r = await tester.RunAsync(Draft(), creds, CancellationToken.None);
            Assert.False(r.Success);
            Assert.Equal(TestConnectStage.Handshake, r.Stage);
            Assert.Equal("Error_103", r.MessageKey);
        }

        [Fact]
        public async Task HostKeyMismatch_StageHostKey()
        {
            var factory = new FakeSshSessionFactory();
            factory.Queue.Enqueue(new FakeSshSession
            {
                HostKeyOnConnect = Key,
                ConnectResult = SshErrorCode.None
            });
            var known = new KnownHostRepository(new InMemoryFileSystem());
            await known.AddAsync(new KnownHost
            {
                Id = "k",
                Host = "10.0.0.8",
                Port = 22,
                FingerprintSha256 = "other"
            });
            var tester = Tester(factory, known);
            CredentialDraft creds = CredentialDraft.ForNew();
            creds.SetPassword("x");
            TestConnectResult r = await tester.RunAsync(Draft(), creds, CancellationToken.None);
            Assert.Equal(TestConnectStage.HostKey, r.Stage);
            Assert.Equal("Error_303", r.MessageKey);
        }

        [Fact]
        public async Task AuthFail_StageAuth()
        {
            var factory = new FakeSshSessionFactory();
            factory.Queue.Enqueue(new FakeSshSession
            {
                HostKeyOnConnect = Key,
                PasswordResult = SshErrorCode.AuthPasswordFailed
            });
            var tester = Tester(factory, new KnownHostRepository(new InMemoryFileSystem()));
            CredentialDraft creds = CredentialDraft.ForNew();
            creds.SetPassword("bad");
            TestConnectResult r = await tester.RunAsync(Draft(), creds, CancellationToken.None);
            Assert.Equal(TestConnectStage.Auth, r.Stage);
            Assert.Equal("Error_201", r.MessageKey);
        }

        [Fact]
        public async Task Success_NoShell()
        {
            var native = new FakeSshSession { HostKeyOnConnect = Key };
            var factory = new FakeSshSessionFactory();
            factory.Queue.Enqueue(native);
            var tester = Tester(factory, new KnownHostRepository(new InMemoryFileSystem()));
            CredentialDraft creds = CredentialDraft.ForNew();
            creds.SetPassword("ok");
            TestConnectResult r = await tester.RunAsync(Draft(), creds, CancellationToken.None);
            Assert.True(r.Success);
            Assert.Equal("TestConnect_Ok", r.MessageKey);
            Assert.DoesNotContain("OpenShell", native.Calls);
        }

        [Fact]
        public async Task Timeout_WhenConnectHangs()
        {
            var native = new FakeSshSession { ConnectHold = new TaskCompletionSource<SshErrorCode>() };
            var factory = new FakeSshSessionFactory();
            factory.Queue.Enqueue(native);
            var tester = Tester(factory, new KnownHostRepository(new InMemoryFileSystem()));
            tester.TimeoutMs = 50;
            CredentialDraft creds = CredentialDraft.ForNew();
            creds.SetPassword("x");
            TestConnectResult r = await tester.RunAsync(Draft(), creds, CancellationToken.None);
            Assert.False(r.Success);
            Assert.Equal("TestConnect_Timeout", r.MessageKey);
        }

        [Fact]
        public async Task Cancel_ReturnsCancelled()
        {
            var native = new FakeSshSession { ConnectHold = new TaskCompletionSource<SshErrorCode>() };
            var factory = new FakeSshSessionFactory();
            factory.Queue.Enqueue(native);
            var tester = Tester(factory, new KnownHostRepository(new InMemoryFileSystem()));
            tester.TimeoutMs = 5000;
            var cts = new CancellationTokenSource();
            CredentialDraft creds = CredentialDraft.ForNew();
            creds.SetPassword("x");
            Task<TestConnectResult> task = tester.RunAsync(Draft(), creds, cts.Token);
            cts.Cancel();
            TestConnectResult r = await task;
            Assert.Equal("TestConnect_Cancelled", r.MessageKey);
        }
    }
}
