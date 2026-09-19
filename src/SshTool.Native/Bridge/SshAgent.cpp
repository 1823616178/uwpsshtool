#include "pch.h"
#include "Bridge/SshAgent.h"

#include "Bridge/BridgeUtil.h"
#include "crypto/vault.hpp" // SecureClear（与 KeyTool 同一清零原语）

#include <memory>
#include <string>

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            namespace crypto = sshclient::crypto;
            using Windows::Foundation::IAsyncOperation;

            namespace
            {
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

            SshAgent::SshAgent()
                : agent_(std::make_unique<sshclient::ssh::SshAgent>())
            {
            }

            IAsyncOperation<bool>^ SshAgent::UnlockAsync(
                Platform::String^ keyId,
                const Platform::Array<uint8>^ privateKey,
                Platform::String^ passphrase)
            {
                if (keyId == nullptr || privateKey == nullptr)
                {
                    throw ref new Platform::NullReferenceException();
                }
                auto idText = std::make_shared<std::string>(ToUtf8(keyId));
                auto keyText = std::make_shared<std::string>();
                if (privateKey->Length > 0)
                {
                    keyText->assign(reinterpret_cast<const char*>(privateKey->Data),
                                    privateKey->Length);
                }
                auto phrase = std::make_shared<std::string>(ToUtf8(passphrase));
                SshAgent^ self = this;
                return concurrency::create_async(
                    [self, idText, keyText, phrase]() -> bool {
                        const bool ok = self->agent_->unlock(*idText, *keyText, *phrase);
                        ClearString(*keyText);
                        ClearString(*phrase);
                        ClearString(*idText);
                        return ok;
                    });
            }

            bool SshAgent::Lock(Platform::String^ keyId)
            {
                if (keyId == nullptr)
                {
                    throw ref new Platform::NullReferenceException();
                }
                return agent_->lock(ToUtf8(keyId));
            }

            void SshAgent::LockAll()
            {
                agent_->lockAll();
            }

            void SshAgent::SetTimeout(int minutes)
            {
                agent_->setTimeout(minutes < 0 ? 0u : static_cast<std::uint32_t>(minutes));
            }

            int SshAgent::TimeoutMinutes::get()
            {
                return static_cast<int>(agent_->timeoutMinutes());
            }

            bool SshAgent::IsLocked(Platform::String^ keyId)
            {
                if (keyId == nullptr)
                {
                    throw ref new Platform::NullReferenceException();
                }
                return agent_->isLocked(ToUtf8(keyId));
            }

            int SshAgent::KeyCount::get()
            {
                return static_cast<int>(agent_->keyCount());
            }

            sshclient::ssh::SshAgent* SshAgent::Core()
            {
                return agent_.get();
            }
        }
    }
}
