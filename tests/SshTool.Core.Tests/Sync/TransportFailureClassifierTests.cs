using SshTool.Core.Sync.Api;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // fix/login-feedback：「请求一定还没发出」的 HRESULT 判定（决定 https→http 回退后能否当场重放登录 POST）。
    public class TransportFailureClassifierTests
    {
        [Theory]
        [InlineData(unchecked((int)0x80072EE7))] // 12007 名字解析失败
        [InlineData(unchecked((int)0x80072EFD))] // 12029 无法连接
        [InlineData(unchecked((int)0x80072F7D))] // 12157 安全通道错误（对端不是 TLS 的典型结果）
        [InlineData(unchecked((int)0x80090326))] // SEC_E_ILLEGAL_MESSAGE（握手收到明文 HTTP 应答）
        [InlineData(unchecked((int)0x80090308))] // SEC_E_INVALID_TOKEN
        [InlineData(unchecked((int)0x80090325))] // SEC_E_UNTRUSTED_ROOT
        [InlineData(unchecked((int)0x80090322))] // SEC_E_WRONG_PRINCIPAL
        [InlineData(unchecked((int)0x80090331))] // SEC_E_ALGORITHM_MISMATCH
        [InlineData(unchecked((int)0x80072F0D))] // 12045 证书颁发机构无效
        [InlineData(unchecked((int)0x80072F06))] // 12038 证书 CN 不符
        [InlineData(unchecked((int)0x800B0109))] // CERT_E_UNTRUSTEDROOT
        public void HandshakeAndConnectFailures_ArePreSend(int hresult)
        {
            Assert.True(TransportFailureClassifier.IsPreSendHResult(hresult));
        }

        [Theory]
        [InlineData(unchecked((int)0x80072EFE))] // 12030 连接中止（结果不明）
        [InlineData(unchecked((int)0x80072EFF))] // 12031 连接重置（结果不明）
        [InlineData(unchecked((int)0x80072EE2))] // 12002 超时
        [InlineData(unchecked((int)0x80090330))] // SEC_E_DECRYPT_FAILURE（可能发生在请求发出之后）
        [InlineData(unchecked((int)0x80004005))] // E_FAIL
        [InlineData(0)]
        public void AmbiguousFailures_AreNotPreSend(int hresult)
        {
            Assert.False(TransportFailureClassifier.IsPreSendHResult(hresult));
        }
    }
}
