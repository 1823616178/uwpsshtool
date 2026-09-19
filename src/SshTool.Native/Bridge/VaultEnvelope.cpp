#include "pch.h"
#include "Bridge/VaultEnvelope.h"

#include "Bridge/BridgeUtil.h"

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            namespace crypto = sshclient::crypto;

            VaultEnvelope::VaultEnvelope()
            {
                KeyVersion = 1;
                PasswordWrappedKey = ref new Platform::String(L"");
                PasswordWrapNonce = ref new Platform::String(L"");
                RecoveryWrappedKey = ref new Platform::String(L"");
                RecoveryWrapNonce = ref new Platform::String(L"");
                KdfSalt = ref new Platform::String(L"");
                KdfAlgorithm = ToPlatform(std::string(crypto::kKdfAlgorithm));
                KdfMemory = crypto::kDefaultKdfMemoryKib;
                KdfIterations = crypto::kDefaultKdfIterations;
                KdfParallelism = crypto::kDefaultKdfParallelism;
            }

            bool VaultEnvelope::TryGetWrap(crypto::VaultWrap* out)
            {
                if (out == nullptr)
                {
                    return false;
                }
                if (KeyVersion < 1 || KdfMemory < 0 || KdfIterations < 0 || KdfParallelism < 0)
                {
                    return false;
                }
                out->keyVersion = KeyVersion;
                out->passwordWrappedKey = ToUtf8(PasswordWrappedKey);
                out->passwordWrapNonce = ToUtf8(PasswordWrapNonce);
                out->recoveryWrappedKey = ToUtf8(RecoveryWrappedKey);
                out->recoveryWrapNonce = ToUtf8(RecoveryWrapNonce);
                out->kdfSalt = ToUtf8(KdfSalt);
                out->kdfParameters.algorithm = ToUtf8(KdfAlgorithm);
                out->kdfParameters.memoryKib = static_cast<std::uint32_t>(KdfMemory);
                out->kdfParameters.iterations = static_cast<std::uint32_t>(KdfIterations);
                out->kdfParameters.parallelism = static_cast<std::uint32_t>(KdfParallelism);
                return true;
            }

            VaultEnvelope^ VaultEnvelope::FromWrap(const crypto::VaultWrap& wrap)
            {
                auto envelope = ref new VaultEnvelope();
                envelope->KeyVersion = wrap.keyVersion;
                envelope->PasswordWrappedKey = ToPlatform(wrap.passwordWrappedKey);
                envelope->PasswordWrapNonce = ToPlatform(wrap.passwordWrapNonce);
                envelope->RecoveryWrappedKey = ToPlatform(wrap.recoveryWrappedKey);
                envelope->RecoveryWrapNonce = ToPlatform(wrap.recoveryWrapNonce);
                envelope->KdfSalt = ToPlatform(wrap.kdfSalt);
                envelope->KdfAlgorithm = ToPlatform(wrap.kdfParameters.algorithm);
                envelope->KdfMemory = static_cast<int>(wrap.kdfParameters.memoryKib);
                envelope->KdfIterations = static_cast<int>(wrap.kdfParameters.iterations);
                envelope->KdfParallelism = static_cast<int>(wrap.kdfParameters.parallelism);
                return envelope;
            }
        }
    }
}
