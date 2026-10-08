using System.Collections.Generic;

namespace SshTool.Core.Storage
{
    // 评审（PR #1）：受保护文件解密失败时，区分「密文真的坏了 / 密钥已不在」（永久，可隔离）
    // 与「DPAPI/平台暂时出错」（内存不足、RPC/服务未就绪、拒绝访问等，不能据此丢弃有效凭据）。
    // 只认明确表示「数据或密钥无效」的 HRESULT；未知错误一律按暂时性处理（宁可抛错，也不丢数据）。
    public static class SecureFileFailurePolicy
    {
        // NTE_BAD_DATA：数据损坏/不是本密钥加密的
        public const int NteBadData = unchecked((int)0x80090005);
        // NTE_BAD_KEY
        public const int NteBadKey = unchecked((int)0x80090003);
        // NTE_NO_KEY：对应密钥不存在（重置/换机恢复 LocalFolder 后）
        public const int NteNoKey = unchecked((int)0x8009000D);
        // NTE_BAD_KEYSET：密钥集不存在
        public const int NteBadKeyset = unchecked((int)0x80090016);
        // NTE_DECRYPTION_FAILURE
        public const int NteDecryptionFailure = unchecked((int)0x80090034);
        // CRYPT_E_ASN1_BADTAG：保护描述/信封格式非法（DPAPI-NG blob 损坏）
        public const int CryptAsn1BadTag = unchecked((int)0x8009310B);
        // HRESULT_FROM_WIN32(ERROR_INVALID_DATA)
        public const int Win32InvalidData = unchecked((int)0x8007000D);

        private static readonly HashSet<int> CorruptionCodes = new HashSet<int>
        {
            NteBadData, NteBadKey, NteNoKey, NteBadKeyset, NteDecryptionFailure, CryptAsn1BadTag, Win32InvalidData
        };

        public static bool IsGenuineCorruption(int hresult)
        {
            return CorruptionCodes.Contains(hresult);
        }

        // 每次解密尝试的 HRESULT（至少一次）全部属于「真损坏」才隔离；
        // 只要有一次是暂时性/未知错误，就保留文件并把错误抛给调用方。
        public static bool ShouldQuarantine(IReadOnlyList<int> attemptHResults)
        {
            if (attemptHResults == null || attemptHResults.Count == 0)
            {
                return false;
            }
            for (int i = 0; i < attemptHResults.Count; i++)
            {
                if (!IsGenuineCorruption(attemptHResults[i]))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
