using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Sessions;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;
using SshTool.Core.Tests.Fakes;
using Xunit;

namespace SshTool.Core.Tests.Sessions
{
    // P02：SessionManager.OnNetworkChanged（对齐鸿蒙端 notifyNetworkChanged）——
    // 退避中的会话立即重连、已连接的 ProbeNow、策略挂起/终态/拨号中不动。
    public class SessionManagerNetworkTests
    {
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
            public Task<PasswordPromptResult> PromptPasswordAsync(string hostDisplay, string errorMessage)
            {
                return Task.FromResult(new PasswordPromptResult { Password = "pw", Remember = true });
            }

            public Task<string> PromptPassphraseAsync(string keyName)
            {
                return Task.FromResult("ph");
            }

            public Task<System.Collections.Generic.IReadOnlyList<string>> PromptKeyboardInteractiveAsync(AuthPromptEventArgs args)
            {
                return Task.FromResult<System.Collections.Generic.IReadOnlyList<string>>(new[] { "token" });
            }

            public Task<AgentUnlockResult> PromptAgentUnlockAsync(
                System.Collections.Generic.IReadOnlyList<AgentKeyChoice> choices, string hostDisplay)
            {
                return Task.FromResult(new AgentUnlockResult { Cancelled = true });
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
            public readonly ManualTimerFactory Timers = new ManualTimerFactory();
            public readonly SessionManager Manager;

            public Fixture()
            {
                Hosts = new HostRepository(Fs);
                Known = new KnownHostRepository(Fs);
                Keys = new KeyRepository(Fs);
                Settings = new SettingsRepository(new InMemorySettingsStore());
                Manager = new SessionManager(
                    Hosts, Known, Keys, Secrets, Settings, Factory,
                    new FakeHostKeyPrompter(), new FakeCredentialPrompter(),
                    Timers, new ImmediateDispatcher());
            }

            public async Task<Host> AddHostAsync()
            {
                Host host = Defaults.NewHost();
                host.Name = "web";
                host.HostName = "10.0.0.1";
                host.Port = 22;
                host.Username = "root";
                host.AuthType = AuthType.Password;
                await Hosts.AddAsync(host);
                return host;
            }
        }

        private static FakeSshSession ReadySession()
        {
            return new FakeSshSession
            {
                HostKeyOnConnect = SampleKey,
                ConnectResult = SshErrorCode.None,
                PasswordResult = SshErrorCode.None,
                OpenShellResult = SshErrorCode.None
            };
        }

        private static async Task WaitForStateAsync(SessionInfo info, SessionUiState want)
        {
            for (int i = 0; i < 200 && info.State != want; i++)
            {
                await Task.Delay(10).ConfigureAwait(false);
            }
            Assert.Equal(want, info.State);
        }

        [Fact]
        public async Task ConnectedSession_Probed()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            FakeSshSession native = ReadySession();
            fx.Factory.Queue.Enqueue(native);

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            Assert.Equal(SessionUiState.Connected, info.State);

            int affected = fx.Manager.OnNetworkChanged();

            Assert.Equal(1, affected);
            Assert.Equal(1, native.ProbeNowCount);
            Assert.Equal(SessionUiState.Connected, info.State);
        }

        [Fact]
        public async Task ReconnectingSession_ReconnectsImmediately()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            FakeSshSession first = ReadySession();
            FakeSshSession second = ReadySession();
            fx.Factory.Queue.Enqueue(first);

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            first.FireStateChanged(SessionStateKind.Disconnected, SshErrorCode.RemoteClosed);
            Assert.Equal(SessionUiState.Reconnecting, info.State);
            Assert.Equal(1, info.ReconnectAttempt);

            fx.Factory.Queue.Enqueue(second);
            int affected = fx.Manager.OnNetworkChanged();

            Assert.Equal(1, affected);
            await WaitForStateAsync(info, SessionUiState.Connected);
            Assert.Same(second, info.NativeSession);
            Assert.Equal(0, info.ReconnectAttempt);
        }

        [Fact]
        public async Task PolicySuspended_Skipped()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            FakeSshSession native = ReadySession();
            fx.Factory.Queue.Enqueue(native);

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            fx.Manager.SuspendAllForPolicy("后台断开");
            Assert.True(info.PolicySuspended);

            Assert.Equal(0, fx.Manager.OnNetworkChanged());
            Assert.Equal(0, native.ProbeNowCount);
        }

        [Fact]
        public async Task DisconnectedWithoutTimer_Skipped()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            FakeSshSession first = ReadySession();
            fx.Factory.Queue.Enqueue(first);

            await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            SessionInfo info = fx.Manager.Sessions[0];
            first.FireStateChanged(SessionStateKind.Disconnected, SshErrorCode.RemoteClosed);
            fx.Manager.CancelReconnect(info.SessionId);
            Assert.Equal(SessionUiState.Disconnected, info.State);

            Assert.Equal(0, fx.Manager.OnNetworkChanged());
        }

        [Fact]
        public void NoSessions_Zero()
        {
            var fx = new Fixture();
            Assert.Equal(0, fx.Manager.OnNetworkChanged());
        }

        [Fact]
        public async Task MixedSessionSet_ConvergesEachKind()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();

            // 会话 A：保持已连接 → 应被 ProbeNow。
            FakeSshSession nativeA = ReadySession();
            fx.Factory.Queue.Enqueue(nativeA);
            SessionInfo infoA = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            // 会话 B：打入退避 → 应立即重连。
            FakeSshSession nativeB1 = ReadySession();
            FakeSshSession nativeB2 = ReadySession();
            fx.Factory.Queue.Enqueue(nativeB1);
            SessionInfo infoB = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            nativeB1.FireStateChanged(SessionStateKind.Disconnected, SshErrorCode.RemoteClosed);
            Assert.Equal(SessionUiState.Reconnecting, infoB.State);
            fx.Factory.Queue.Enqueue(nativeB2);

            int affected = fx.Manager.OnNetworkChanged();

            Assert.Equal(2, affected);
            Assert.Equal(1, nativeA.ProbeNowCount);
            await WaitForStateAsync(infoB, SessionUiState.Connected);
            Assert.Same(nativeB2, infoB.NativeSession);
        }
    }
}
