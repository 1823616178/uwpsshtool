using System;
using System.Threading;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Hosts;
using SshTool.Core.Models;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;

namespace SshTool.Core.Sessions
{
    public enum TestConnectStage
    {
        Resolve = 0,
        Handshake = 1,
        HostKey = 2,
        Auth = 3,
        Complete = 4
    }

    public sealed class TestConnectResult
    {
        public bool Success { get; set; }
        public TestConnectStage Stage { get; set; }
        public string MessageKey { get; set; }
        public SshErrorCode ErrorCode { get; set; }
    }

    // D07：用编辑草稿测连，不开 shell；15 s 总超时。
    public sealed class ConnectionTester
    {
        private readonly KnownHostRepository _knownHosts;
        private readonly ISecretStore _secrets;
        private readonly ISshSessionFactory _factory;
        private readonly IHostKeyPrompter _hostKeys;
        private readonly IUiDispatcher _ui;

        public ConnectionTester(
            KnownHostRepository knownHosts,
            ISecretStore secrets,
            ISshSessionFactory factory,
            IHostKeyPrompter hostKeys,
            IUiDispatcher ui)
        {
            if (knownHosts == null) throw new ArgumentNullException("knownHosts");
            if (secrets == null) throw new ArgumentNullException("secrets");
            if (factory == null) throw new ArgumentNullException("factory");
            if (hostKeys == null) throw new ArgumentNullException("hostKeys");
            if (ui == null) throw new ArgumentNullException("ui");
            _knownHosts = knownHosts;
            _secrets = secrets;
            _factory = factory;
            _hostKeys = hostKeys;
            _ui = ui;
            TimeoutMs = 15000;
        }

        public int TimeoutMs { get; set; }

        public event EventHandler<TestConnectStage> StageChanged;

        public async Task<TestConnectResult> RunAsync(
            Host draft, CredentialDraft creds, CancellationToken cancel)
        {
            if (draft == null)
            {
                throw new ArgumentNullException("draft");
            }
            using (var timeout = new CancellationTokenSource())
            {
                timeout.CancelAfter(TimeoutMs);
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, timeout.Token))
                {
                    try
                    {
                        return await RunCoreAsync(draft, creds ?? CredentialDraft.ForNew(), linked.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        if (cancel.IsCancellationRequested)
                        {
                            return Fail(TestConnectStage.Handshake, "TestConnect_Cancelled", SshErrorCode.None);
                        }
                        return Fail(TestConnectStage.Handshake, "TestConnect_Timeout", SshErrorCode.ConnectTimeout);
                    }
                }
            }
        }

        private async Task<TestConnectResult> RunCoreAsync(
            Host draft, CredentialDraft creds, CancellationToken cancel)
        {
            Raise(TestConnectStage.Resolve);
            cancel.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(draft.HostName))
            {
                return Fail(TestConnectStage.Resolve, "Error_101", SshErrorCode.DnsResolutionFailed);
            }

            Raise(TestConnectStage.Handshake);
            ISshSession native = _factory.Create();
            try
            {
                KnownHost known = await FindKnownAsync(draft.HostName, draft.Port).ConfigureAwait(false);
                HostKeyInfo accepted = null;
                bool writeKnown = false;
                EventHandler<HostKeyCheckEventArgs> onKey = (s, e) =>
                {
                    HostKeyVerdict v = HostKeyVerifier.Verify(known, draft.HostFingerprint, e.Info);
                    if (v.Kind == HostKeyVerdictKind.Accept)
                    {
                        accepted = e.Info;
                        writeKnown = v.WriteKnownHost;
                        e.Accept();
                        return;
                    }
                    if (v.Kind == HostKeyVerdictKind.RejectMismatch)
                    {
                        e.Reject();
                        return;
                    }
                    _ui.Post(() =>
                    {
                        var ignore = PromptUnknownAsync(e, HostLabel(draft), a => { accepted = a; writeKnown = true; });
                    });
                };
                native.HostKeyCheck += onKey;
                var req = new SshConnectRequest
                {
                    Host = draft.HostName,
                    Port = draft.Port < 1 ? 22 : draft.Port,
                    Username = draft.Username,
                    ConnectTimeoutMs = TimeoutMs,
                    TermType = string.IsNullOrEmpty(draft.TermType) ? "xterm-256color" : draft.TermType
                };
                Task<SshErrorCode> connectTask = native.ConnectAsync(req);
                Task timeoutTask = Task.Delay(TimeoutMs, cancel);
                Task finished = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);
                if (finished != connectTask)
                {
                    cancel.ThrowIfCancellationRequested();
                    throw new OperationCanceledException(cancel);
                }
                SshErrorCode code = await connectTask.ConfigureAwait(false);
                native.HostKeyCheck -= onKey;
                cancel.ThrowIfCancellationRequested();
                if (code != SshErrorCode.None)
                {
                    return FromConnectError(code);
                }

                Raise(TestConnectStage.HostKey);
                if (writeKnown && accepted != null)
                {
                    await WriteKnownAsync(draft, accepted).ConfigureAwait(false);
                }

                Raise(TestConnectStage.Auth);
                code = await AuthenticateAsync(draft, creds, native).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();
                if (code != SshErrorCode.None)
                {
                    return Fail(TestConnectStage.Auth, "Error_" + ((int)code).ToString(), code);
                }

                Raise(TestConnectStage.Complete);
                return new TestConnectResult
                {
                    Success = true,
                    Stage = TestConnectStage.Complete,
                    MessageKey = "TestConnect_Ok",
                    ErrorCode = SshErrorCode.None
                };
            }
            finally
            {
                native.Close();
                native.Dispose();
            }
        }

        // fix/functional-pass：对话框「主机」处显示主机名称/地址（旧实现误传 e.Info.KeyType，显示成 ssh-ed25519）。
        internal static string HostLabel(Host draft)
        {
            if (draft == null)
            {
                return string.Empty;
            }
            string address = draft.HostName ?? string.Empty;
            int port = draft.Port < 1 ? 22 : draft.Port;
            if (port != 22)
            {
                address = address + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (!string.IsNullOrWhiteSpace(draft.Name) && !string.Equals(draft.Name, draft.HostName, StringComparison.Ordinal))
            {
                return draft.Name + " (" + address + ")";
            }
            return address;
        }

        private async Task PromptUnknownAsync(HostKeyCheckEventArgs e, string hostLabel, Action<HostKeyInfo> onAccept)
        {
            bool ok;
            try
            {
                ok = await _hostKeys.PromptUnknownAsync(e.Info, hostLabel).ConfigureAwait(true);
            }
            catch (Exception)
            {
                ok = false; // 提示器失败：fail-closed，别让测试连接干等到超时
            }
            if (ok)
            {
                onAccept(e.Info);
                e.Accept();
            }
            else
            {
                e.Reject();
            }
        }

        private async Task<SshErrorCode> AuthenticateAsync(Host draft, CredentialDraft creds, ISshSession native)
        {
            if (creds.AuthType == AuthType.Key)
            {
                return SshErrorCode.NoLocalCredential;
            }
            string password = creds.Password;
            if (string.IsNullOrEmpty(password) && creds.HasSavedPassword && !string.IsNullOrEmpty(draft.Id))
            {
                password = await _secrets.GetAsync(SecretKeys.HostPassword(draft.Id)).ConfigureAwait(false);
            }
            if (string.IsNullOrEmpty(password))
            {
                return SshErrorCode.NoLocalCredential;
            }
            return await native.AuthenticatePasswordAsync(password).ConfigureAwait(false);
        }

        private async Task WriteKnownAsync(Host draft, HostKeyInfo info)
        {
            KnownHost existing = await FindKnownAsync(draft.HostName, draft.Port).ConfigureAwait(false);
            if (existing != null)
            {
                return;
            }
            await _knownHosts.AddAsync(new KnownHost
            {
                Id = IdGenerator.NewId(),
                Host = draft.HostName,
                Port = draft.Port < 1 ? 22 : draft.Port,
                KeyType = info.KeyType,
                FingerprintSha256 = info.FingerprintSha256,
                AddedAt = DateTime.UtcNow.ToString("o"),
                LastSeenAt = DateTime.UtcNow.ToString("o")
            }, ChangeOrigin.User).ConfigureAwait(false);
        }

        private async Task<KnownHost> FindKnownAsync(string hostName, int port)
        {
            var all = await _knownHosts.GetAllAsync().ConfigureAwait(false);
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i].Host, hostName, StringComparison.OrdinalIgnoreCase)
                    && all[i].Port == port)
                {
                    return all[i];
                }
            }
            return null;
        }

        private static TestConnectResult FromConnectError(SshErrorCode code)
        {
            TestConnectStage stage = TestConnectStage.Handshake;
            int n = (int)code;
            if (code == SshErrorCode.DnsResolutionFailed)
            {
                stage = TestConnectStage.Resolve;
            }
            else if (code == SshErrorCode.UnknownHostKey || code == SshErrorCode.HostKeyMismatch)
            {
                stage = TestConnectStage.HostKey;
            }
            else if (n >= 200 && n < 300)
            {
                stage = TestConnectStage.Auth;
            }
            return Fail(stage, "Error_" + n.ToString(), code);
        }

        private static TestConnectResult Fail(TestConnectStage stage, string key, SshErrorCode code)
        {
            return new TestConnectResult
            {
                Success = false,
                Stage = stage,
                MessageKey = key,
                ErrorCode = code
            };
        }

        private void Raise(TestConnectStage stage)
        {
            EventHandler<TestConnectStage> handler = StageChanged;
            if (handler != null)
            {
                handler(this, stage);
            }
        }
    }
}
