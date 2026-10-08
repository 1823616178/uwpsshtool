using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

            public Task<PasswordPromptResult> PromptPasswordAsync(string hostDisplay, int retriesLeft)
            {
                PasswordCount++;
                if (Cancel)
                {
                    return Task.FromResult(new PasswordPromptResult { Cancelled = true });
                }
                return Task.FromResult(new PasswordPromptResult { Password = Password, Remember = Remember });
            }

            public Task<PassphrasePromptResult> PromptPassphraseAsync(string keyName, bool previousWrong)
            {
                return Task.FromResult(new PassphrasePromptResult { Passphrase = "ph" });
            }

            public Task<IReadOnlyList<string>> PromptKeyboardInteractiveAsync(AuthPromptEventArgs args)
            {
                return Task.FromResult(KiAnswers);
            }

            public Task<AgentUnlockResult> PromptAgentUnlockAsync(
                IReadOnlyList<AgentKeyChoice> choices, string hostDisplay)
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
            Assert.Equal(HostKeyVerdictKind.PromptUnknown, HostKeyVerifier.Verify(null, string.Empty, SampleKey).Kind);
        }

        // opt/full-pass：钉住指纹（随同步漫游）与对端不一致、本机又没有 known_hosts 记录时，
        // 必须按不匹配拒绝，不能退化成首次连接的 TOFU 弹框，也不能写 known_hosts。
        [Fact]
        public void Verifier_PinnedFingerprintMismatch_NoKnownHost_RejectMismatch()
        {
            HostKeyVerdict v = HostKeyVerifier.Verify(null, "SHA256:pinned-from-sync", SampleKey);
            Assert.Equal(HostKeyVerdictKind.RejectMismatch, v.Kind);
            Assert.False(v.WriteKnownHost);
        }

        // known_hosts 记录优先于钉住指纹：两者冲突时以本机 known_hosts 为准。
        [Fact]
        public void Verifier_KnownHostTakesPrecedenceOverPinned()
        {
            var known = new KnownHost { FingerprintSha256 = SampleKey.FingerprintSha256 };
            Assert.Equal(HostKeyVerdictKind.Accept,
                HostKeyVerifier.Verify(known, "SHA256:stale-pin", SampleKey).Kind);
        }

        [Fact]
        public async Task Open_PinnedFingerprintMismatch_ShowsMismatchNotTofu()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            host.HostFingerprint = "SHA256:pinned-from-sync";
            await fx.Hosts.UpdateAsync(host);
            fx.Enqueue(ReadySession());
            fx.HostKeys.AcceptUnknown = true; // 即便用户习惯性点「信任」，也不该有机会

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Error, info.State);
            Assert.Equal(SshErrorCode.HostKeyMismatch, info.ErrorCode);
            Assert.Equal(1, fx.HostKeys.MismatchCount);
            Assert.Equal(0, fx.HostKeys.UnknownCount);
            Assert.Empty(await fx.Known.GetAllAsync());
            Host saved = await fx.Hosts.GetByIdAsync(host.Id);
            Assert.Equal("SHA256:pinned-from-sync", saved.HostFingerprint);
        }

        // fix/functional-pass：不匹配对话框选「移除旧记录并重试」→ 清 known_hosts + 钉住指纹，
        // 重连走首次连接确认，信任后写入新指纹。
        [Fact]
        public async Task Open_MismatchRemoveAndRetry_ClearsTrustAndRepromptsAsUnknown()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            host.HostFingerprint = "SHA256:old";
            await fx.Hosts.UpdateAsync(host);
            await fx.Known.AddAsync(new KnownHost
            {
                Id = "kh1", Host = host.HostName, Port = 22, KeyType = "ssh-ed25519", FingerprintSha256 = "SHA256:old"
            });
            fx.Enqueue(ReadySession());
            fx.Enqueue(ReadySession());
            fx.HostKeys.AcceptMismatch = true;
            fx.HostKeys.AcceptUnknown = true;

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            for (int i = 0; i < 200 && info.State != SessionUiState.Connected; i++)
            {
                await Task.Delay(10);
            }

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.Equal(1, fx.HostKeys.MismatchCount);
            Assert.Equal(1, fx.HostKeys.UnknownCount);
            KnownHost kh = await fx.Known.FindAsync(host.HostName, 22);
            Assert.NotNull(kh);
            Assert.Equal(SampleKey.FingerprintSha256, kh.FingerprintSha256);
            Host saved = await fx.Hosts.GetByIdAsync(host.Id);
            Assert.Equal(SampleKey.FingerprintSha256, saved.HostFingerprint);
        }

        [Fact]
        public async Task Open_MismatchCancelled_KeepsTrustRecords()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            await fx.Known.AddAsync(new KnownHost
            {
                Id = "kh1", Host = host.HostName, Port = 22, KeyType = "ssh-ed25519", FingerprintSha256 = "SHA256:old"
            });
            fx.Enqueue(ReadySession());
            fx.HostKeys.AcceptMismatch = false;

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            await Task.Delay(30);

            Assert.Equal(SessionUiState.Error, info.State);
            Assert.Equal(1, fx.Factory.Created.Count);
            Assert.NotNull(await fx.Known.FindAsync(host.HostName, 22));
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

        // code-review-pass：开 shell 期间被新一轮重连顶掉的旧尝试，拿到失败码后
        // 不得再排重连计时器（否则几秒后会把已连上的新会话拆掉），成功时也不得改状态。
        [Fact]
        public async Task StaleAttempt_ShellResultAfterReconnect_Ignored()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            FakeSshSession first = fx.Enqueue(ReadySession());
            first.OpenShellHold = new TaskCompletionSource<SshErrorCode>();
            FakeSshSession second = fx.Enqueue(ReadySession());

            Task<SessionInfo> opening = fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            Assert.Contains("OpenShell", first.Calls);
            SessionInfo info = fx.Manager.Sessions[0];

            fx.Manager.ReconnectNow(info.SessionId);
            await Task.Yield();
            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.Same(second, info.NativeSession);

            // 旧 native 已被关闭，它挂着的开 shell 以失败返回。
            first.OpenShellHold.SetResult(SshErrorCode.RemoteClosed);
            await opening;

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.Equal(0, info.ReconnectAttempt);
            Assert.Same(second, info.NativeSession);
            fx.Timers.FirePending();
            Assert.Same(second, info.NativeSession);
            Assert.Equal(2, fx.Factory.Created.Count);
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

        // WRONG_THREAD 回归：后台线程改 SessionInfo 时字段同步更新、通知必须经
        // IUiDispatcher 封送，否则 XAML（StatusDot 的 Ellipse 等 STA 对象）在后台线程
        // 被触碰会抛 RPC_E_WRONG_THREAD（0x8001010E，栈顶常见 CanvasFigureSegmentOptions）。
        private sealed class RecordingDispatcher : IUiDispatcher
        {
            public int PostCount;
            public void Post(Action action)
            {
                PostCount++;
                if (action != null)
                {
                    action();
                }
            }

            public Task RunAsync(Func<Task> action)
            {
                return action == null ? Task.FromResult(0) : action();
            }

            public Task<T> RunAsync<T>(Func<Task<T>> action)
            {
                return action();
            }
        }

        [Fact]
        public async Task SessionInfo_NotifyViaDispatcher_FieldSync()
        {
            var fs = new InMemoryFileSystem();
            var hosts = new HostRepository(fs);
            var known = new KnownHostRepository(fs);
            var keys = new KeyRepository(fs);
            var secrets = new InMemorySecretStore();
            var settings = new SettingsRepository(new InMemorySettingsStore());
            var factory = new FakeSshSessionFactory();
            var dispatcher = new RecordingDispatcher();
            var manager = new SessionManager(
                hosts, known, keys, secrets, settings, factory,
                new FakeHostKeyPrompter(), new FakeCredentialPrompter(),
                new ManualTimerFactory(), dispatcher);

            Host host = Defaults.NewHost();
            host.Name = "web";
            host.HostName = "10.0.0.1";
            host.Port = 22;
            host.Username = "root";
            host.AuthType = AuthType.Password;
            await hosts.AddAsync(host);
            factory.Queue.Enqueue(ReadySession());

            SessionInfo info = await manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            Assert.Equal(SessionUiState.Connected, info.State);

            int before = dispatcher.PostCount;
            string raised = null;
            info.PropertyChanged += (s, e) => raised = e.PropertyName;
            // 模拟 I/O/线程池线程改属性：字段必须立即可见，通知走 dispatcher.Post。
            await Task.Run(() => { info.State = SessionUiState.Reconnecting; });
            Assert.Equal(SessionUiState.Reconnecting, info.State);
            Assert.True(dispatcher.PostCount > before);
            Assert.Equal("State", raised);
        }

        // U10：shell 打开后自动执行 tmux 附着 + 初始命令（每条追加 \r）。
        [Fact]
        public async Task Open_WithTmuxAndInitCommands_SendsAutoRunAfterOpenShell()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            host.TmuxAutoAttach = true;
            host.TmuxSessionName = "my sess";
            host.InitCommands = new List<string> { "cd /srv", "", "htop" };
            await fx.Hosts.UpdateAsync(host, ChangeOrigin.User);
            FakeSshSession native = fx.Enqueue(ReadySession());

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Connected, info.State);
            int openShellIndex = native.Calls.IndexOf("OpenShell");
            Assert.True(openShellIndex >= 0);
            Assert.Equal(3, native.Writes.Count);
            Assert.True(native.Calls.IndexOf("Write") > openShellIndex);
            Assert.Equal("tmux new-session -A -s my_sess\r", Encoding.UTF8.GetString(native.Writes[0]));
            Assert.Equal("cd /srv\r", Encoding.UTF8.GetString(native.Writes[1]));
            Assert.Equal("htop\r", Encoding.UTF8.GetString(native.Writes[2]));
        }

        // U10：全部关闭时不发送任何自动执行命令。
        [Fact]
        public async Task Open_NoAutoRunConfig_NoWrite()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            FakeSshSession native = fx.Enqueue(ReadySession());

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.Empty(native.Writes);
        }

        // U10：重连后再次发送，靠 tmux 附着找回现场。
        [Fact]
        public async Task Reconnect_SendsAutoRunAgain()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            host.TmuxAutoAttach = true;
            host.TmuxSessionName = "main";
            await fx.Hosts.UpdateAsync(host, ChangeOrigin.User);
            FakeSshSession first = fx.Enqueue(ReadySession());
            FakeSshSession second = fx.Enqueue(ReadySession());

            SessionInfo info = await fx.Manager.OpenAsync(new SessionOpenRequest { HostId = host.Id });
            Assert.Single(first.Writes);

            first.FireStateChanged(SessionStateKind.Disconnected, SshErrorCode.RemoteClosed);
            fx.Timers.FirePending();
            await Task.Yield();

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.Same(second, info.NativeSession);
            Assert.Single(second.Writes);
            Assert.Equal("tmux new-session -A -s main\r", Encoding.UTF8.GetString(second.Writes[0]));
        }

        // F03：OpenShell=false（SFTP 专用连接）——不开 shell、不发 AutoRun，
        // 认证通过即 Connected；重连也保持无 shell。
        [Fact]
        public async Task Open_NoShell_ConnectedWithoutShellOrAutoRun()
        {
            var fx = new Fixture();
            Host host = await fx.AddHostAsync();
            host.TmuxAutoAttach = true;
            host.TmuxSessionName = "main";
            await fx.Hosts.UpdateAsync(host, ChangeOrigin.User);
            FakeSshSession native = fx.Enqueue(ReadySession());

            SessionInfo info = await fx.Manager.OpenAsync(
                new SessionOpenRequest { HostId = host.Id, OpenShell = false });

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.False(info.ShellOpened);
            Assert.DoesNotContain(native.Calls, c => c == "OpenShell");
            Assert.Empty(native.Writes);

            // 重连保持无 shell 形态。
            FakeSshSession second = fx.Enqueue(ReadySession());
            native.FireStateChanged(SessionStateKind.Disconnected, SshErrorCode.RemoteClosed);
            fx.Timers.FirePending();
            await Task.Yield();

            Assert.Equal(SessionUiState.Connected, info.State);
            Assert.Same(second, info.NativeSession);
            Assert.False(info.ShellOpened);
            Assert.DoesNotContain(second.Calls, c => c == "OpenShell");
        }
    }
}
