using SshTool.Core.Storage;
using Xunit;

namespace SshTool.Core.Tests.Storage
{
    public class SecureFileFailurePolicyTests
    {
        private const int EOutOfMemory = unchecked((int)0x8007000E);
        private const int EAccessDenied = unchecked((int)0x80070005);
        private const int RpcServerUnavailable = unchecked((int)0x800706BA);
        private const int EFail = unchecked((int)0x80004005);

        [Theory]
        [InlineData(SecureFileFailurePolicy.NteBadData)]
        [InlineData(SecureFileFailurePolicy.NteBadKey)]
        [InlineData(SecureFileFailurePolicy.NteNoKey)]
        [InlineData(SecureFileFailurePolicy.NteBadKeyset)]
        [InlineData(SecureFileFailurePolicy.NteDecryptionFailure)]
        [InlineData(SecureFileFailurePolicy.CryptAsn1BadTag)]
        [InlineData(SecureFileFailurePolicy.Win32InvalidData)]
        public void CorruptionCodes_AreGenuine(int hr)
        {
            Assert.True(SecureFileFailurePolicy.IsGenuineCorruption(hr));
        }

        [Theory]
        [InlineData(EOutOfMemory)]
        [InlineData(EAccessDenied)]
        [InlineData(RpcServerUnavailable)]
        [InlineData(EFail)]
        [InlineData(0)]
        public void TransientOrUnknownCodes_AreNotCorruption(int hr)
        {
            Assert.False(SecureFileFailurePolicy.IsGenuineCorruption(hr));
        }

        [Fact]
        public void Quarantine_OnlyWhenEveryAttemptIsCorruption()
        {
            Assert.True(SecureFileFailurePolicy.ShouldQuarantine(
                new[] { SecureFileFailurePolicy.NteBadData, SecureFileFailurePolicy.NteBadData }));
            Assert.False(SecureFileFailurePolicy.ShouldQuarantine(
                new[] { RpcServerUnavailable, RpcServerUnavailable }));
            Assert.False(SecureFileFailurePolicy.ShouldQuarantine(
                new[] { SecureFileFailurePolicy.NteBadData, EOutOfMemory }));
            Assert.False(SecureFileFailurePolicy.ShouldQuarantine(
                new[] { EOutOfMemory, SecureFileFailurePolicy.NteBadData }));
        }

        [Fact]
        public void Quarantine_NoAttempts_False()
        {
            Assert.False(SecureFileFailurePolicy.ShouldQuarantine(new int[0]));
            Assert.False(SecureFileFailurePolicy.ShouldQuarantine(null));
        }
    }
}
