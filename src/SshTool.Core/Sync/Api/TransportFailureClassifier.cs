namespace SshTool.Core.Sync.Api
{
    // fix/login-feedback：判定传输层异常的 HRESULT 是否属于「请求字节一定还没发出」的阶段
    //（名字解析、建连、TLS 握手 / 证书校验）。从 App 的 UwpHttpTransport 挪到 Core 以便单测。
    // 只有这类失败才允许在 https → http 回退后重发非幂等请求（如登录 POST，见 ApiClient.IsSafeToReplay）；
    // 连接中途断开（ConnectionAborted / Reset）结果不明，不在此列。
    // 现网同步服务器是纯 HTTP：https 握手收到明文 HTTP 应答，Schannel 报 SEC_E_ILLEGAL_MESSAGE 等；
    // 若真机上报的是本表之外的握手错误，登录会先失败一次（文案提示「已切换，请再点一次」）。
    public static class TransportFailureClassifier
    {
        // WinINet（HRESULT_FROM_WIN32）
        public const int HResultNameNotResolved = unchecked((int)0x80072EE7);      // 12007
        public const int HResultCannotConnect = unchecked((int)0x80072EFD);        // 12029
        public const int HResultSecurityChannelError = unchecked((int)0x80072F7D); // 12157
        public const int HResultSecCertDateInvalid = unchecked((int)0x80072F05);   // 12037
        public const int HResultSecCertCnInvalid = unchecked((int)0x80072F06);     // 12038
        public const int HResultInvalidCa = unchecked((int)0x80072F0D);            // 12045
        public const int HResultSecCertErrors = unchecked((int)0x80072F17);        // 12055
        public const int HResultSecInvalidCert = unchecked((int)0x80072F89);       // 12169
        public const int HResultSecCertRevoked = unchecked((int)0x80072F8A);       // 12170

        // Schannel / SSPI 握手阶段（不含 SEC_E_DECRYPT_FAILURE 等可能发生在请求发出之后的错误）
        public const int HResultSecInvalidToken = unchecked((int)0x80090308);
        public const int HResultSecUnsupportedFunction = unchecked((int)0x80090302);
        public const int HResultSecWrongPrincipal = unchecked((int)0x80090322);
        public const int HResultSecUntrustedRoot = unchecked((int)0x80090325);
        public const int HResultSecIllegalMessage = unchecked((int)0x80090326);
        public const int HResultSecCertUnknown = unchecked((int)0x80090327);
        public const int HResultSecCertExpired = unchecked((int)0x80090328);
        public const int HResultSecAlgorithmMismatch = unchecked((int)0x80090331);

        // CryptoAPI 证书链校验
        public const int HResultCertExpired = unchecked((int)0x800B0101);
        public const int HResultCertUntrustedRoot = unchecked((int)0x800B0109);
        public const int HResultCertCnNoMatch = unchecked((int)0x800B010F);

        private static readonly int[] PreSend = new int[]
        {
            HResultNameNotResolved,
            HResultCannotConnect,
            HResultSecurityChannelError,
            HResultSecCertDateInvalid,
            HResultSecCertCnInvalid,
            HResultInvalidCa,
            HResultSecCertErrors,
            HResultSecInvalidCert,
            HResultSecCertRevoked,
            HResultSecInvalidToken,
            HResultSecUnsupportedFunction,
            HResultSecWrongPrincipal,
            HResultSecUntrustedRoot,
            HResultSecIllegalMessage,
            HResultSecCertUnknown,
            HResultSecCertExpired,
            HResultSecAlgorithmMismatch,
            HResultCertExpired,
            HResultCertUntrustedRoot,
            HResultCertCnNoMatch,
        };

        public static bool IsPreSendHResult(int hresult)
        {
            for (int i = 0; i < PreSend.Length; i++)
            {
                if (PreSend[i] == hresult)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
