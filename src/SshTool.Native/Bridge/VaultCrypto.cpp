#include "pch.h"
#include "Bridge/VaultCrypto.h"

#include "Bridge/BridgeUtil.h"
#include "Bridge/DocumentEnvelope.h"
#include "Bridge/VaultEnvelope.h"
#include "Bridge/VaultSetupResult.h"

#include "crypto/vault.hpp"

#include <cstring>
#include <memory>
#include <string>
#include <vector>

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            namespace crypto = sshclient::crypto;
            using Windows::Foundation::IAsyncOperation;
            using Windows::Security::Cryptography::CryptographicBuffer;
            using Windows::Storage::Streams::IBuffer;

            namespace
            {
                // 标准 Base64 的 vaultKey（32 字节）→ 二进制；失败 false。
                bool DecodeVaultKey(const std::string& b64, std::uint8_t key[crypto::kKeyBytes])
                {
                    std::vector<std::uint8_t> raw;
                    if (!crypto::Base64StdDecode(b64, &raw) || raw.size() != crypto::kKeyBytes)
                    {
                        return false;
                    }
                    std::memcpy(key, raw.data(), crypto::kKeyBytes);
                    crypto::SecureClear(raw.data(), raw.size());
                    return true;
                }

                Platform::String^ EncodeVaultKey(const std::uint8_t key[crypto::kKeyBytes])
                {
                    const std::string b64 = crypto::Base64StdEncode(key, crypto::kKeyBytes);
                    return ToPlatform(b64);
                }

                void ClearString(std::string& value)
                {
                    if (!value.empty())
                    {
                        crypto::SecureClear(&value[0], value.size());
                    }
                }

                // 注意：create_async 要求外层 lambda 的 operator() 为 const
                // （见 SshSession.cpp 注释），敏感捕获一律经 shared_ptr<string>
                // 传入——指针本身 const，指向内容可清零后返回。
            }

            IAsyncOperation<VaultSetupResult^>^ VaultCrypto::CreateAsync(
                Platform::String^ syncPassword, int keyVersion)
            {
                // 调用线程快照口令，异步体不再回读 WinRT 参数。
                auto password = std::make_shared<std::string>(ToUtf8(syncPassword));
                return concurrency::create_async(
                    [password, keyVersion]() -> VaultSetupResult^ {
                        if (password->empty() || keyVersion < 1)
                        {
                            ClearString(*password);
                            return nullptr;
                        }
                        crypto::VaultWrap wrap;
                        std::string recoveryKey;
                        std::uint8_t vaultKey[crypto::kKeyBytes];
                        // 默认 KDF 参数（与桌面端 DEFAULT_KDF_PARAMETERS 一致，
                        // 出处见 crypto::DefaultKdfParameters）。
                        const bool ok = crypto::CreateVault(*password, keyVersion, &wrap,
                                                            &recoveryKey, vaultKey);
                        ClearString(*password);
                        if (!ok)
                        {
                            return nullptr;
                        }
                        auto result = ref new VaultSetupResult();
                        result->Envelope = VaultEnvelope::FromWrap(wrap);
                        result->RecoveryKey = ToPlatform(recoveryKey);
                        result->VaultKeyBase64 = EncodeVaultKey(vaultKey);
                        crypto::SecureClear(vaultKey, sizeof(vaultKey));
                        ClearString(recoveryKey);
                        if (result->VaultKeyBase64 == nullptr ||
                            result->VaultKeyBase64->IsEmpty())
                        {
                            return nullptr;
                        }
                        return result;
                    });
            }

            IAsyncOperation<Platform::String^>^ VaultCrypto::UnwrapWithPasswordAsync(
                VaultEnvelope^ env, Platform::String^ syncPassword)
            {
                auto password = std::make_shared<std::string>(ToUtf8(syncPassword));
                // 信封跨线程快照为 core 结构：异步体不再碰 WinRT 对象。
                crypto::VaultWrap wrap;
                const bool captured = (env != nullptr) && env->TryGetWrap(&wrap);
                return concurrency::create_async(
                    [password, wrap, captured]() -> Platform::String^ {
                        if (!captured || password->empty())
                        {
                            ClearString(*password);
                            return nullptr;
                        }
                        std::uint8_t vaultKey[crypto::kKeyBytes];
                        const bool ok = crypto::UnlockWithPassword(*password, wrap, vaultKey);
                        ClearString(*password);
                        if (!ok)
                        {
                            return nullptr;
                        }
                        Platform::String^ b64 = EncodeVaultKey(vaultKey);
                        crypto::SecureClear(vaultKey, sizeof(vaultKey));
                        return (b64 != nullptr && !b64->IsEmpty()) ? b64 : nullptr;
                    });
            }

            IAsyncOperation<Platform::String^>^ VaultCrypto::UnwrapWithRecoveryKeyAsync(
                VaultEnvelope^ env, Platform::String^ recoveryKey)
            {
                auto recovery = std::make_shared<std::string>(ToUtf8(recoveryKey));
                crypto::VaultWrap wrap;
                const bool captured = (env != nullptr) && env->TryGetWrap(&wrap);
                return concurrency::create_async(
                    [recovery, wrap, captured]() -> Platform::String^ {
                        if (!captured || recovery->empty())
                        {
                            ClearString(*recovery);
                            return nullptr;
                        }
                        std::uint8_t vaultKey[crypto::kKeyBytes];
                        const bool ok = crypto::UnlockWithRecovery(*recovery, wrap, vaultKey);
                        ClearString(*recovery);
                        if (!ok)
                        {
                            return nullptr;
                        }
                        Platform::String^ b64 = EncodeVaultKey(vaultKey);
                        crypto::SecureClear(vaultKey, sizeof(vaultKey));
                        return (b64 != nullptr && !b64->IsEmpty()) ? b64 : nullptr;
                    });
            }

            IAsyncOperation<DocumentEnvelope^>^ VaultCrypto::EncryptDocumentAsync(
                Platform::String^ vaultKeyB64, Platform::String^ vaultId, int schemaVersion,
                int keyVersion, const Platform::Array<uint8>^ utf8Plaintext)
            {
                const std::string keyText = ToUtf8(vaultKeyB64);
                const std::string vaultIdText = ToUtf8(vaultId);
                auto plaintext = std::make_shared<std::string>();
                if (utf8Plaintext != nullptr && utf8Plaintext->Length > 0)
                {
                    plaintext->assign(reinterpret_cast<const char*>(utf8Plaintext->Data),
                                      utf8Plaintext->Length);
                }
                return concurrency::create_async(
                    [keyText, vaultIdText, schemaVersion, keyVersion,
                     plaintext]() -> DocumentEnvelope^ {
                        if (keyText.empty() || vaultIdText.empty() || schemaVersion < 1 ||
                            keyVersion < 1)
                        {
                            ClearString(*plaintext);
                            return nullptr;
                        }
                        if (plaintext->size() > crypto::kDocumentMaxBytes)
                        {
                            ClearString(*plaintext);
                            return nullptr;
                        }
                        std::uint8_t vaultKey[crypto::kKeyBytes];
                        if (!DecodeVaultKey(keyText, vaultKey))
                        {
                            ClearString(*plaintext);
                            return nullptr;
                        }
                        crypto::DocumentSeal seal;
                        const bool ok = crypto::EncryptDocument(
                            vaultKey, vaultIdText, schemaVersion, keyVersion, *plaintext,
                            &seal);
                        crypto::SecureClear(vaultKey, sizeof(vaultKey));
                        ClearString(*plaintext);
                        if (!ok)
                        {
                            return nullptr;
                        }
                        return DocumentEnvelope::FromSeal(seal);
                    });
            }

            IAsyncOperation<IBuffer^>^ VaultCrypto::DecryptDocumentAsync(
                Platform::String^ vaultKeyB64, Platform::String^ vaultId, DocumentEnvelope^ env)
            {
                const std::string keyText = ToUtf8(vaultKeyB64);
                const std::string vaultIdText = ToUtf8(vaultId);
                crypto::DocumentSeal seal;
                const bool captured = (env != nullptr) && env->TryGetSeal(&seal);
                return concurrency::create_async(
                    [keyText, vaultIdText, seal, captured]() -> IBuffer^ {
                        if (!captured || keyText.empty() || vaultIdText.empty())
                        {
                            return nullptr;
                        }
                        std::uint8_t vaultKey[crypto::kKeyBytes];
                        if (!DecodeVaultKey(keyText, vaultKey))
                        {
                            return nullptr;
                        }
                        std::string plaintext;
                        const bool ok =
                            crypto::DecryptDocument(vaultKey, vaultIdText, seal, &plaintext);
                        crypto::SecureClear(vaultKey, sizeof(vaultKey));
                        if (!ok)
                        {
                            return nullptr;
                        }
                        IBuffer^ buffer;
                        if (plaintext.empty())
                        {
                            buffer = ref new Windows::Storage::Streams::Buffer(0);
                        }
                        else
                        {
                            auto bytes = ref new Platform::Array<uint8>(
                                static_cast<unsigned int>(plaintext.size()));
                            std::memcpy(bytes->Data, plaintext.data(), plaintext.size());
                            buffer = CryptographicBuffer::CreateFromByteArray(bytes);
                        }
                        ClearString(plaintext);
                        return buffer;
                    });
            }
        }
    }
}
