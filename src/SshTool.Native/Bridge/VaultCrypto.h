#pragma once

// S05：保险库密码学 WinRT 桥（01-DESIGN.md §6.2 VaultCrypto；04-TASKS S05）。
//
// 底层是 S03 的 native/core/crypto（OpenSSL AES-256-GCM + libargon2 ref.c +
// HKDF-SHA256），KDF 参数一律读信封（踩坑 #1），解密前先验 ciphertextHash
// （踩坑 #2），Base64 要求规范形式，密钥材料用完即清零。
//
// 线程与失败语义（S05 要点）：
//   - 所有方法经 concurrency::create_async 在后台线程执行（Argon2id 默认参数
//     在 Lumia 上约数秒，绝不能占 UI 线程，见 D13）；
//   - 任何失败（密码错误、恢复密钥无效、密文/AAD 篡改、参数非法）一律返回
//     nullptr，不抛异常、不区分失败原因；
//   - 本文件不记录任何日志，调用方（NativeVaultCrypto）只记相位与耗时，
//     绝不记密码/恢复密钥/vaultKey/明文/密文（日志脱敏）。

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            ref class DocumentEnvelope;
            ref class VaultEnvelope;
            ref class VaultSetupResult;

            public ref class VaultCrypto sealed
            {
            public:
                static Windows::Foundation::IAsyncOperation<VaultSetupResult^>^ CreateAsync(
                    Platform::String^ syncPassword, int keyVersion);

                // 失败返回 nullptr（密码错误与参数非法不区分）。
                static Windows::Foundation::IAsyncOperation<Platform::String^>^ UnwrapWithPasswordAsync(
                    VaultEnvelope^ env, Platform::String^ syncPassword);
                static Windows::Foundation::IAsyncOperation<Platform::String^>^ UnwrapWithRecoveryKeyAsync(
                    VaultEnvelope^ env, Platform::String^ recoveryKey);

                static Windows::Foundation::IAsyncOperation<DocumentEnvelope^>^ EncryptDocumentAsync(
                    Platform::String^ vaultKeyB64, Platform::String^ vaultId, int schemaVersion,
                    int keyVersion, const Platform::Array<uint8>^ utf8Plaintext);

                // 失败返回 nullptr；成功返回明文字节（IBuffer）。
                static Windows::Foundation::IAsyncOperation<Windows::Storage::Streams::IBuffer^>^
                    DecryptDocumentAsync(Platform::String^ vaultKeyB64,
                                         Platform::String^ vaultId, DocumentEnvelope^ env);
            };
        }
    }
}
