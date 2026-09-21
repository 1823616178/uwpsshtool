using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Sessions;
using SshTool.Core.Tests.Fakes;
using Xunit;

namespace SshTool.Core.Tests.Sessions
{
    // N09b 验收：FakeSshSession 自身单测（脚本化行为可预期）。
    public class FakeSshSessionTests
    {
        [Fact]
        public async Task ConnectAsync_ReturnsScriptedCode_AndRecordsRequest()
        {
            var fake = new FakeSshSession { ConnectResult = SshErrorCode.ConnectionRefused };
            var request = new SshConnectRequest { Host = "h", Port = 2222, Username = "u" };

            SshErrorCode code = await fake.ConnectAsync(request);

            Assert.Equal(SshErrorCode.ConnectionRefused, code);
            Assert.Equal(new[] { "Connect" }, fake.Calls);
            Assert.Same(request, fake.LastConnectRequest);
        }

        [Fact]
        public async Task AuthMethods_ReturnScriptedCodes_AndRecordArguments()
        {
            var fake = new FakeSshSession
            {
                PasswordResult = SshErrorCode.AuthPasswordFailed,
                PublicKeyResult = SshErrorCode.PrivateKeyLoadFailed,
                KeyboardInteractiveResult = SshErrorCode.AuthKeyboardInteractiveFailed,
            };
            var key = new byte[] { 1, 2, 3 };

            Assert.Equal(SshErrorCode.AuthPasswordFailed, await fake.AuthenticatePasswordAsync("pw"));
            Assert.Equal(SshErrorCode.PrivateKeyLoadFailed, await fake.AuthenticatePublicKeyAsync(key, "pp"));
            Assert.Equal(SshErrorCode.AuthKeyboardInteractiveFailed,
                await fake.AuthenticateKeyboardInteractiveAsync());

            Assert.Equal(new[] { "AuthPassword", "AuthPublicKey", "AuthKeyboardInteractive" }, fake.Calls);
            Assert.Equal("pw", fake.LastPassword);
            Assert.Same(key, fake.LastPrivateKey);
            Assert.Equal("pp", fake.LastPassphrase);
        }

        [Fact]
        public async Task OpenShellAndExec_ReturnScripted_AndRecord()
        {
            var fake = new FakeSshSession
            {
                OpenShellResult = SshErrorCode.None,
                ExecResult = new SshExecResult(42, "out", "err"),
            };

            Assert.Equal(SshErrorCode.None, await fake.OpenShellAsync(113, 37));
            SshExecResult result = await fake.ExecAsync("uname -a");

            Assert.Equal(113, fake.LastOpenShellCols);
            Assert.Equal(37, fake.LastOpenShellRows);
            Assert.Equal("uname -a", fake.LastExecCommand);
            Assert.Equal(new[] { "OpenShell", "Exec" }, fake.Calls);
            Assert.Equal(42, result.ExitCode);
            Assert.Equal("out", result.Stdout);
            Assert.Equal("err", result.Stderr);
        }

        [Fact]
        public void WriteResizeProbeNowClose_RecordCalls()
        {
            var fake = new FakeSshSession();
            var data = new byte[] { 9, 8 };

            fake.Write(data);
            fake.Resize(100, 30);
            fake.ProbeNow();
            fake.ProbeNow();
            fake.Close();

            Assert.Equal(new[] { "Write", "Resize", "ProbeNow", "ProbeNow", "Close" }, fake.Calls);
            Assert.Same(data, fake.LastWritten);
            Assert.Equal(100, fake.LastResizeCols);
            Assert.Equal(30, fake.LastResizeRows);
            Assert.Equal(2, fake.ProbeNowCount);
            Assert.Equal(1, fake.CloseCount);
        }

        [Fact]
        public void FireStateChanged_UpdatesState_AndRaisesEvent()
        {
            var fake = new FakeSshSession();
            SessionStateChangedEventArgs received = null;
            fake.StateChanged += (s, e) => received = e;

            fake.FireStateChanged(SessionStateKind.Error, SshErrorCode.KeepaliveTimeout, "boom");

            Assert.Equal(SessionStateKind.Error, fake.State);
            Assert.NotNull(received);
            Assert.Equal(SessionStateKind.Error, received.State);
            Assert.Equal(SshErrorCode.KeepaliveTimeout, received.ErrorCode);
            Assert.Equal("boom", received.Detail);
        }

        [Fact]
        public void FireHostKeyCheck_SubscriberAccepts_DecisionRecorded()
        {
            var fake = new FakeSshSession();
            var info = new HostKeyInfo("ssh-ed25519", "SHA256:abc", "art");
            HostKeyCheckEventArgs received = null;
            fake.HostKeyCheck += (s, e) =>
            {
                received = e;
                e.Accept();
            };

            fake.FireHostKeyCheck(info);

            Assert.Equal(true, fake.HostKeyDecision);
            Assert.Same(info, received.Info);
            Assert.True(received.IsDecided);
        }

        [Fact]
        public void HostKeyCheckArgs_SecondDecisionIgnored()
        {
            var fake = new FakeSshSession();
            HostKeyCheckEventArgs args = fake.FireHostKeyCheck(new HostKeyInfo("t", "f", "a"));

            args.Reject();
            args.Accept(); // 二次作答忽略

            Assert.Equal(false, fake.HostKeyDecision);
            Assert.True(args.IsDecided);
        }

        [Fact]
        public void FireAuthPrompt_RespondRecordsAnswers_SecondAnswerIgnored()
        {
            var fake = new FakeSshSession();
            AuthPromptEventArgs received = null;
            fake.AuthPrompt += (s, e) => received = e;

            AuthPromptEventArgs args = fake.FireAuthPrompt(new[] { "Password: " }, new[] { false });
            Assert.NotNull(received);
            Assert.Equal(new[] { "Password: " }, args.Prompts.ToArray());
            Assert.Equal(new[] { false }, args.Echo.ToArray());
            Assert.Null(fake.AuthAnswered);

            args.Respond(new[] { "secret" });
            Assert.Equal(true, fake.AuthAnswered);
            Assert.Equal(new[] { "secret" }, fake.AuthAnswers.ToArray());

            args.Cancel(); // 二次答复忽略
            Assert.Equal(true, fake.AuthAnswered);
        }

        [Fact]
        public void AuthPromptArgs_CancelRecordsEmptyAnswer()
        {
            var fake = new FakeSshSession();
            AuthPromptEventArgs args = fake.FireAuthPrompt(new string[0], new bool[0]);

            args.Cancel();

            Assert.Equal(false, fake.AuthAnswered);
            Assert.Empty(fake.AuthAnswers);
        }

        [Fact]
        public void FireContentDirty_RaisesEvent()
        {
            var fake = new FakeSshSession();
            int count = 0;
            fake.ContentDirty += (s, e) => count++;

            fake.FireContentDirty();
            fake.FireContentDirty();

            Assert.Equal(2, count);
        }

        [Fact]
        public void Dispose_CountsCalls()
        {
            var fake = new FakeSshSession();

            fake.Dispose();
            fake.Dispose();

            Assert.Equal(2, fake.DisposeCount); // fake 不做幂等吞并，留给调用方断言
        }

        [Fact]
        public void IdAndScreen_ExposeAsISshSession()
        {
            var fake = new FakeSshSession { Id = "sess-7" };
            ISshSession session = fake;

            Assert.Equal("sess-7", session.Id);
            Assert.Null(session.Screen);
            Assert.Equal(SessionStateKind.Idle, session.State);
        }

        [Fact]
        public void Factory_Create_ReturnsConfiguredSession_AndRecords()
        {
            var session = new FakeSshSession { Id = "s-9" };
            var factory = new FakeSshSessionFactory { Next = session };

            ISshSession created = factory.Create();

            Assert.Same(session, created);
            Assert.Equal("s-9", created.Id);
            Assert.Equal(new[] { session }, factory.Created);
        }

        [Fact]
        public void ConnectRequest_DefaultsMatchNativeConnectOptions()
        {
            var request = new SshConnectRequest();

            Assert.Equal(22, request.Port);
            Assert.Equal(15000, request.ConnectTimeoutMs);
            Assert.Equal(30, request.KeepaliveSeconds);
            Assert.Equal("xterm-256color", request.TermType);
            Assert.Equal(80, request.Cols);
            Assert.Equal(24, request.Rows);
            Assert.Equal(string.Empty, request.JumpSessionId);
        }
    }
}
