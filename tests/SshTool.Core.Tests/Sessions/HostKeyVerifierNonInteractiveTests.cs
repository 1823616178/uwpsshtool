using SshTool.Core.Models;
using SshTool.Core.Sessions;
using Xunit;

namespace SshTool.Core.Tests.Sessions
{
    // 评审（PR #1）：后台隧道（无交互）遇到未知主机必须拒绝、不写 known_hosts；
    // 其余判定与交互入口 Verify 一致。
    public class HostKeyVerifierNonInteractiveTests
    {
        private static readonly HostKeyInfo SampleKey =
            new HostKeyInfo("ssh-ed25519", "SHA256:tunnel123", "art");

        [Fact]
        public void UnknownHost_NoPin_RejectUnknown_AndNoWrite()
        {
            HostKeyVerdict v = HostKeyVerifier.VerifyNonInteractive(null, null, SampleKey);
            Assert.Equal(HostKeyVerdictKind.RejectUnknown, v.Kind);
            Assert.False(v.WriteKnownHost);
            Assert.Equal(HostKeyVerdictKind.RejectUnknown,
                HostKeyVerifier.VerifyNonInteractive(null, string.Empty, SampleKey).Kind);
        }

        [Fact]
        public void KnownHostMatch_Accept()
        {
            var known = new KnownHost { FingerprintSha256 = SampleKey.FingerprintSha256 };
            HostKeyVerdict v = HostKeyVerifier.VerifyNonInteractive(known, null, SampleKey);
            Assert.Equal(HostKeyVerdictKind.Accept, v.Kind);
            Assert.False(v.WriteKnownHost);
        }

        [Fact]
        public void KnownHostMismatch_RejectMismatch()
        {
            var known = new KnownHost { FingerprintSha256 = "SHA256:other" };
            Assert.Equal(HostKeyVerdictKind.RejectMismatch,
                HostKeyVerifier.VerifyNonInteractive(known, null, SampleKey).Kind);
        }

        [Fact]
        public void PinnedMatch_AcceptAndWrite()
        {
            HostKeyVerdict v = HostKeyVerifier.VerifyNonInteractive(null, SampleKey.FingerprintSha256, SampleKey);
            Assert.Equal(HostKeyVerdictKind.Accept, v.Kind);
            Assert.True(v.WriteKnownHost);
        }

        [Fact]
        public void PinnedMismatch_RejectMismatch()
        {
            Assert.Equal(HostKeyVerdictKind.RejectMismatch,
                HostKeyVerifier.VerifyNonInteractive(null, "SHA256:pinned", SampleKey).Kind);
        }

        [Fact]
        public void MissingPresentedKey_RejectMismatch()
        {
            Assert.Equal(HostKeyVerdictKind.RejectMismatch,
                HostKeyVerifier.VerifyNonInteractive(null, null, null).Kind);
        }

        [Fact]
        public void InteractiveVerify_NeverReturnsRejectUnknown()
        {
            Assert.Equal(HostKeyVerdictKind.PromptUnknown, HostKeyVerifier.Verify(null, null, SampleKey).Kind);
        }
    }
}
