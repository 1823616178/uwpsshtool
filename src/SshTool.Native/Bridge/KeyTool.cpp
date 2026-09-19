#include "pch.h"
#include "Bridge/KeyTool.h"

#include "Bridge/BridgeUtil.h"
#include "Bridge/KeyInfo.h"

#include "crypto/keytool.h"
#include "crypto/vault.hpp"

#include <memory>
#include <string>

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            namespace keytool = sshclient::crypto::keytool;
            namespace crypto = sshclient::crypto;
            using Windows::Foundation::IAsyncOperation;
            using Windows::Security::Cryptography::CryptographicBuffer;
            using Windows::Storage::Streams::IBuffer;

            namespace
            {
                void ClearString(std::string& value)
                {
                    if (!value.empty())
                    {
                        crypto::SecureClear(&value[0], value.size());
                    }
                }

                IBuffer^ ToKeyBuffer(const std::string& pem)
                {
                    if (pem.empty())
                    {
                        return nullptr;
                    }
                    auto bytes = ref new Platform::Array<uint8>(
                        static_cast<unsigned int>(pem.size()));
                    std::memcpy(bytes->Data, pem.data(), pem.size());
                    return CryptographicBuffer::CreateFromByteArray(bytes);
                }

                // 注意：create_async 要求外层 lambda 的 operator() 为 const
                // （见 SshSession.cpp 注释），敏感捕获一律经 shared_ptr<string>
                // 传入——指针本身 const，指向内容可清零后返回。
            }

            IAsyncOperation<IBuffer^>^ KeyTool::GenerateEd25519Async(Platform::String^ comment)
            {
                auto commentText = std::make_shared<std::string>(ToUtf8(comment));
                return concurrency::create_async(
                    [commentText]() -> IBuffer^ {
                        std::string pem;
                        const bool ok = keytool::GenerateEd25519(*commentText, &pem);
                        ClearString(*commentText);
                        if (!ok)
                        {
                            return nullptr;
                        }
                        IBuffer^ buffer = ToKeyBuffer(pem);
                        ClearString(pem);
                        return buffer;
                    });
            }

            IAsyncOperation<IBuffer^>^ KeyTool::GenerateRsaAsync(int bits,
                                                                 Platform::String^ comment)
            {
                // PEM 格式无注释字段：comment 仅为与 GenerateEd25519Async 对齐的
                // 保留参数（由上层存 KeyEntry 元数据），此处不进入私钥内容。
                (void)comment;
                return concurrency::create_async(
                    [bits]() -> IBuffer^ {
                        std::string pem;
                        const bool ok = keytool::GenerateRsa(bits, &pem);
                        if (!ok)
                        {
                            return nullptr;
                        }
                        IBuffer^ buffer = ToKeyBuffer(pem);
                        ClearString(pem);
                        return buffer;
                    });
            }

            IAsyncOperation<KeyInfo^>^ KeyTool::InspectAsync(
                const Platform::Array<uint8>^ privateKey, Platform::String^ passphrase)
            {
                auto keyText = std::make_shared<std::string>();
                if (privateKey != nullptr && privateKey->Length > 0)
                {
                    keyText->assign(reinterpret_cast<const char*>(privateKey->Data),
                                    privateKey->Length);
                }
                auto phrase = std::make_shared<std::string>(ToUtf8(passphrase));
                return concurrency::create_async(
                    [keyText, phrase]() -> KeyInfo^ {
                        keytool::KeyInspectInfo info;
                        const bool ok =
                            keytool::InspectPrivateKey(*keyText, *phrase, &info);
                        ClearString(*keyText);
                        ClearString(*phrase);
                        if (!ok)
                        {
                            return nullptr;
                        }
                        return KeyInfo::FromInfo(info);
                    });
            }
        }
    }
}
