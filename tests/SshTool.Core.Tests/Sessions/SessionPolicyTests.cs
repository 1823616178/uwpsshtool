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
    // P01：SessionManager 的策略挂起语义（405 保留面孔 / 原地重连 / 探测 / 挂起标记）。
    // 对齐鸿蒙端 suspendAllForPolicy / resumeAllSuspended（只断连接、不销毁面孔）。
    public class SessionPolicyTests
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
            public bool CancelPassword;
            public int PasswordCount;

            public Task<PasswordPromptResult> PromptPasswordAsync(string hostDisplay, string errorMessage)
            {
                PasswordCount++;
                if (CancelPassword)
                {
                    return Task.FromResult(new PasswordPromptResult { Cancelled = true });
                }
                return Task.FromResult(new PasswordPromptResult { Password = "pw", Remember = true });
            }

            public Task<string> PromptPassphraseAsync(string keyName)
            {
                return Task.FromResult("ph");
            }

            public Task<IReadOnlyList<string>> PromptKeyboardInteractiveAsync(AuthPromptEventArgs args)
            {
                return Task.FromResult((IReadOnlyList<string>)new[] { "token" });
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
                    new FakeHostKeyPrompter(), Creds, Timers, new ImmediateDispatcher());
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
                await Task.Delay(10);
            }
            Assert.Equal(want, info.State);
        }

        [Fact]
        public async Task SuspendAllForPolicy_DisconnectsButKeepsFace()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            fx.Factory.Queue.Enqueue(ReadySession());
            fx.Factory.Queue.Enqueue(ReadySession());
            SessionInfo first = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            SessionInfo second = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            FakeSshSession native1 = (FakeSshSession)first.NativeSession;
            FakeSshSession native2 = (FakeSshSession)second.NativeSession;

            int n = fx.Manager.SuspendAllForPolicy("后台测试原因");

            Assert.Equal(2, n);
            // 面孔保留：SessionInfo 还在列表里，SessionId 不变
            Assert.Equal(2, fx.Manager.Sessions.Count);
            Assert.Same(first, fx.Manager.Sessions[0]);
            Assert.Same(second, fx.Manager.Sessions[1]);
            // 405 + 原因 + 挂起标记 + native 已释放
            Assert.Equal(SessionUiState.Disconnected, first.State);
            Assert.Equal(SshErrorCode.PolicyDisconnect, first.ErrorCode);
            Assert.Equal("后台测试原因", first.ErrorMessage);
            Assert.True(first.PolicySuspended);
            Assert.Null(first.NativeSession);
            Assert.Equal(1, native1.CloseCount);
            Assert.Equal(1, native1.DisposeCount);
            Assert.Equal(1, native2.CloseCount);
            // 幂等：第二次无动作
            Assert.Equal(0, fx.Manager.SuspendAllForPolicy("x"));
        }

        [Fact]
        public async Task ResumeSuspended_ReconnectsInPlace()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            fx.Factory.Queue.Enqueue(ReadySession());
            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            string id = info.SessionId;
            fx.Factory.Queue.Enqueue(ReadySession());

            fx.Manager.SuspendAllForPolicy("r");
            Assert.Equal(1, fx.Manager.ActiveSessionCount); // 面孔仍在

            int n = fx.Manager.ResumeSuspended();

            Assert.Equal(1, n);
            Assert.False(info.PolicySuspended);
            Assert.Equal(0, info.ReconnectAttempt);
            await WaitForStateAsync(info, SessionUiState.Connected);
            Assert.Equal(id, info.SessionId); // SessionId 不变
            Assert.NotNull(info.NativeSession);
            Assert.Equal(0, fx.Manager.ResumeSuspended()); // 幂等
        }

        [Fact]
        public async Task ProbeAll_OnlyProbesConnected()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            fx.Factory.Queue.Enqueue(ReadySession());
            fx.Factory.Queue.Enqueue(ReadySession());
            SessionInfo connected = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            SessionInfo other = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            FakeSshSession native = (FakeSshSession)connected.NativeSession;

            fx.Manager.CancelReconnect(other.SessionId); // 进入终态 Disconnected
            int n = fx.Manager.ProbeAll();

            Assert.Equal(1, n);
            Assert.Equal(1, native.ProbeNowCount);
        }

        [Fact]
        public async Task Suspend_SkipsUserCancelledAndAuthError()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            fx.Factory.Queue.Enqueue(ReadySession());
            SessionInfo cancelled = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            fx.Manager.CancelReconnect(cancelled.SessionId);

            fx.Creds.CancelPassword = true;
            FakeSshSession bad = ReadySession();
            bad.PasswordResult = SshErrorCode.AuthPasswordFailed; // 存量密码失效 → 转人工 → 取消
            fx.Factory.Queue.Enqueue(bad);
            SessionInfo failed = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            Assert.Equal(SessionUiState.Error, failed.State);
            Assert.Equal(SshErrorCode.NoLocalCredential, failed.ErrorCode);

            // 终态文案不被 405 覆写
            Assert.Equal(0, fx.Manager.SuspendAllForPolicy("r"));
            Assert.Equal(SessionUiState.Disconnected, cancelled.State);
            Assert.Equal(SshErrorCode.NoLocalCredential, failed.ErrorCode);
            Assert.False(cancelled.PolicySuspended);
            Assert.False(failed.PolicySuspended);
        }

        [Fact]
        public async Task SuspendMarks_OnlineBeforeSuspend()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            fx.Factory.Queue.Enqueue(ReadySession());
            fx.Factory.Queue.Enqueue(ReadySession());
            SessionInfo connected = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            SessionInfo other = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            fx.Manager.CancelReconnect(other.SessionId);

            fx.Manager.MarkOnlineBeforeSuspend();
            Assert.True(connected.WasOnlineBeforeSuspend);
            Assert.False(other.WasOnlineBeforeSuspend);

            fx.Manager.ClearSuspendMarks();
            Assert.False(connected.WasOnlineBeforeSuspend);
        }

        [Fact]
        public async Task Suspend_ReconnectingSession_IsAlive()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            fx.Factory.Queue.Enqueue(ReadySession());
            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            FakeSshSession native = (FakeSshSession)info.NativeSession;

            // 模拟远端断开 → 进入退避重连（计时器待发）
            native.FireStateChanged(SessionStateKind.Disconnected, SshErrorCode.RemoteClosed);
            Assert.Equal(SessionUiState.Reconnecting, info.State);

            int n = fx.Manager.SuspendAllForPolicy("r");
            Assert.Equal(1, n);
            Assert.True(info.PolicySuspended);
            Assert.Equal(SshErrorCode.PolicyDisconnect, info.ErrorCode);
        }
    }
}
