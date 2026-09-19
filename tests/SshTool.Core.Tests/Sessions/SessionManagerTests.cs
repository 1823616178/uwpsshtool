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
    public class SessionManagerTests
    {
        private static readonly HostKeyInfo SampleKey =
            new HostKeyInfo("ssh-ed25519", "SHA256:abc123def456", "art");

        private sealed class FakeHostKeyPrompter : IHostKeyPrompter
        {
            public bool AcceptUnknown = true;
            public bool AcceptMismatch = false;
            public int UnknownCount;
            public int MismatchCount;

            public Task<bool> PromptUnknownAsync(HostKeyInfo info, string hostDisplay)
            {
                UnknownCount++;
                return Task.FromResult(AcceptUnknown);
            }

            public Task<bool> PromptMismatchAsync(HostKeyInfo info, string hostDisplay, string previousFingerprint)
            {
                MismatchCount++;
                return Task.FromResult(AcceptMismatch);
            }
        }

        private sealed class FakeCredentialPrompter : ICredentialPrompter
        {
            public string Password = "pw";
            public bool Remember = true;
            public bool Cancel;
            public int PasswordCount;
            public IReadOnlyList<string> KiAnswers = new[] { "token" };

            public Task<PasswordPromptResult> PromptPasswordAsync(string hostDisplay, string errorMessage)
            {
                PasswordCount++;
                if (Cancel)
                {
                    return Task.FromResult(new PasswordPromptResult { Cancelled = true });
                }
                return Task.FromResult(new PasswordPromptResult { Password = Password, Remember = Remember });
            }

            public Task<string> PromptPassphraseAsync(string keyName)
            {
                return Task.FromResult("ph");
            }

            public Task<IReadOnlyList<string>> PromptKeyboardInteractiveAsync(AuthPromptEventArgs args)
            {
                return Task.FromResult(KiAnswers);
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
            public readonly SessionManager Manager;

            public Fixture()
            {
                Hosts = new HostRepository(Fs);
                Known = new KnownHostRepository(Fs);
                Keys = new KeyRepository(Fs);
                Settings = new SettingsRepository(new InMemorySettingsStore());
                Manager = new SessionManager(
                    Hosts, Known, Keys, Secrets, Settings, Factory,
                    HostKeys, Creds, Timers, new ImmediateDispatcher());
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

            public FakeSshSession Enqueue(FakeSshSession session)
            {
                Factory.Queue.Enqueue(session);
                return session;
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

        [Fact]
        public void Verifier_KnownMatch_Accept()
        {
            var known = new KnownHost { FingerprintSha256 = SampleKey.FingerprintSha256 };
            HostKeyVerdict v = HostKeyVerifier.Verify(known, null, SampleKey);
            Assert.Equal(HostKeyVerdictKind.Accept, v.Kind);
            Assert.False(v.WriteKnownHost);
        }

        [Fact]
        public void Verifier_KnownMismatch_Reject()
        {
            var known = new KnownHost { FingerprintSha256 = "other" };
            Assert.Equal(HostKeyVerdictKind.RejectMismatch, HostKeyVerifier.Verify(known, null, SampleKey).Kind);
        }

        [Fact]
        public void Verifier_SyncFingerprintMatch_AcceptAndWrite()
        {
            HostKeyVerdict v = HostKeyVerifier.Verify(null, SampleKey.FingerprintSha256, SampleKey);
            Assert.Equal(HostKeyVerdictKind.Accept, v.Kind);
            Assert.True(v.WriteKnownHost);
        }

        [Fact]
        public void Verifier_Unknown_Prompt()
        {
            Assert.Equal(HostKeyVerdictKind.PromptUnknown, HostKeyVerifier.Verify(null, null, SampleKey).Kind);
        }

        [Fact]
        public void ShouldReconnect_AuthAndMismatch_False()
        {
            Assert.False(ReconnectScheduler.ShouldReconnect(SshErrorCode.AuthPasswordFailed));
            Assert.False(ReconnectScheduler.ShouldReconnect(SshErrorCode.HostKeyMismatch));
            Assert.True(ReconnectScheduler.ShouldReconnect(SshErrorCode.RemoteClosed));
            Assert.Equal(1, ReconnectScheduler.DelaySeconds(0));
            Assert.Equal(30, ReconnectScheduler.DelaySeconds(99));
        }

        [Fact]
        public async Task Open_TofuAccept_WritesKnownHost()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            fx.Enqueue(ReadySession());
            fx.HostKeys.AcceptUnknown = true;

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.Equal(1, fx.HostKeys.UnknownCount);
            Assert.Single(await fx.Known.GetAllAsync());
            Host saved = await fx.Hosts.GetByIdAsync(host.Id);
            Assert.Equal(SampleKey.FingerprintSha256, saved.HostFingerprint);
            Assert.False(string.IsNullOrEmpty(saved.LastConnectedAt));
        }

        [Fact]
        public async Task Open_TofuReject_Error303()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            fx.Enqueue(ReadySession());
            fx.HostKeys.AcceptUnknown = false;

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Error, info.State);
            Assert.Equal(SshErrorCode.HostKeyMismatch, info.ErrorCode);
            Assert.Empty(await fx.Known.GetAllAsync());
        }

        [Fact]
        public async Task Open_KnownMismatch_RejectsWithoutReconnect()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            await fx.Known.AddAsync(new KnownHost
            {
                Id = "k1",
                Host = host.HostName,
                Port = 22,
                FingerprintSha256 = "SHA256:old"
            });
            fx.Enqueue(ReadySession());

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Error, info.State);
            Assert.Equal(SshErrorCode.HostKeyMismatch, info.ErrorCode);
            Assert.Equal(1, fx.HostKeys.MismatchCount);
            Assert.Equal(0, fx.HostKeys.UnknownCount);
        }

        [Fact]
        public async Task Open_SyncFingerprintMatch_WritesKnownHostWithoutPrompt()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            host.HostFingerprint = SampleKey.FingerprintSha256;
            await fx.Hosts.UpdateAsync(host);
            fx.Enqueue(ReadySession());

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.Equal(0, fx.HostKeys.UnknownCount);
            Assert.Single(await fx.Known.GetAllAsync());
        }

        [Fact]
        public async Task Open_NoPassword_PromptAndRemember()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            fx.Enqueue(ReadySession());
            fx.Creds.Password = "secret";
            fx.Creds.Remember = true;

            await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(1, fx.Creds.PasswordCount);
            Assert.Equal("secret", await fx.Secrets.GetAsync(SecretKeys.HostPassword(host.Id)));
        }

        [Fact]
        public async Task Open_PasswordWrong_RetriesThreeTimes()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            var native = ReadySession();
            native.PasswordResult = SshErrorCode.AuthPasswordFailed;
            fx.Enqueue(native);

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(3, fx.Creds.PasswordCount);
            Assert.Equal(SshErrorCode.AuthPasswordFailed, info.ErrorCode);
            Assert.Equal(SessionUiState.Error, info.State);
        }

        [Fact]
        public async Task Open_KeyboardInteractive_Succeeds()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            await fx.Secrets.SetAsync(SecretKeys.HostPassword(host.Id), "stale");
            var native = ReadySession();
            native.PasswordResult = SshErrorCode.AuthKeyboardInteractiveFailed;
            native.KeyboardInteractiveResult = SshErrorCode.None;
            native.KiPrompts = new[] { "Code:" };
            fx.Enqueue(native);

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.Equal(new[] { "token" }, native.AuthAnswers);
            Assert.Contains("AuthKeyboardInteractive", native.Calls);
        }

        [Fact]
        public async Task Disconnect_AutoReconnects()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            FakeSshSession first = fx.Enqueue(ReadySession());
            FakeSshSession second = fx.Enqueue(ReadySession());

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            Assert.Equal(SessionUiState.Connected, info.State);

            first.FireStateChanged(SessionStateKind.Disconnected, SshErrorCode.RemoteClosed);
            Assert.Equal(SessionUiState.Reconnecting, info.State);
            Assert.Equal(1, info.ReconnectAttempt);

            fx.Timers.FirePending();
            await Task.Yield();

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.Same(second, info.NativeSession);
            Assert.Contains("Connect", second.Calls);
        }

        [Fact]
        public async Task AuthError_DoesNotReconnect()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            var native = ReadySession();
            native.PasswordResult = SshErrorCode.AuthPasswordFailed;
            fx.Enqueue(native);
            fx.Creds.Cancel = true;

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Error, info.State);
            Assert.Equal(SshErrorCode.NoLocalCredential, info.ErrorCode);
        }

        [Fact]
        public async Task Close_StopsReconnect()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            FakeSshSession first = fx.Enqueue(ReadySession());
            fx.Enqueue(ReadySession());

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            first.FireStateChanged(SessionStateKind.Disconnected, SshErrorCode.RemoteClosed);
            fx.Manager.Close(info.SessionId);

            fx.Timers.FirePending();
            Assert.Equal(0, fx.Manager.ActiveSessionCount);
            Assert.Equal(SessionUiState.Closed, info.State);
        }

        [Fact]
        public async Task LateEvent_FromOldHandle_Ignored()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            FakeSshSession first = fx.Enqueue(ReadySession());
            FakeSshSession second = fx.Enqueue(ReadySession());

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            first.FireStateChanged(SessionStateKind.Disconnected, SshErrorCode.RemoteClosed);
            fx.Timers.FirePending();
            await Task.Yield();
            Assert.Equal(SessionUiState.Connected, info.State);

            first.FireStateChanged(SessionStateKind.Error, SshErrorCode.SocketError);
            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.Same(second, info.NativeSession);
        }

        [Fact]
        public async Task CloseAll_RemovesSessions()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            fx.Enqueue(ReadySession());
            fx.Enqueue(ReadySession());
            await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            Assert.Equal(2, fx.Manager.ActiveSessionCount);

            fx.Manager.CloseAll();
            Assert.Equal(0, fx.Manager.ActiveSessionCount);
        }

        [Fact]
        public async Task CancelReconnect_LeavesDisconnected()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            FakeSshSession first = fx.Enqueue(ReadySession());
            await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            SessionInfo info = fx.Manager.Sessions[0];
            first.FireStateChanged(SessionStateKind.Disconnected, SshErrorCode.RemoteClosed);
            fx.Manager.CancelReconnect(info.SessionId);
            Assert.Equal(SessionUiState.Disconnected, info.State);
        }

        [Fact]
        public async Task HostStatus_MostActiveWins()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            fx.Enqueue(ReadySession());
            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            Assert.Equal(HostListStatus.Connected, fx.Manager.GetStatus(host.Id));
            info.State = SessionUiState.Reconnecting;
            Assert.Equal(HostListStatus.Reconnecting, fx.Manager.GetStatus(host.Id));
        }
    }
}
