#include "pch.h"
#include "Bridge/DocumentEnvelope.h"

#include "Bridge/BridgeUtil.h"

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            namespace crypto = sshclient::crypto;

            DocumentEnvelope::DocumentEnvelope()
            {
                SchemaVersion = crypto::kSchemaVersion;
                KeyVersion = 1;
                Algorithm = ToPlatform(std::string(crypto::kAesAlgorithm));
                Nonce = ref new Platform::String(L"");
                Ciphertext = ref new Platform::String(L"");
                CiphertextHash = ref new Platform::String(L"");
            }

            bool DocumentEnvelope::TryGetSeal(crypto::DocumentSeal* out)
            {
                if (out == nullptr)
                {
                    return false;
                }
                if (SchemaVersion < 1 || KeyVersion < 1)
                {
                    return false;
                }
                out->schemaVersion = SchemaVersion;
                out->keyVersion = KeyVersion;
                out->algorithm = ToUtf8(Algorithm);
                out->nonce = ToUtf8(Nonce);
                out->ciphertext = ToUtf8(Ciphertext);
                out->ciphertextHash = ToUtf8(CiphertextHash);
                return true;
            }

            DocumentEnvelope^ DocumentEnvelope::FromSeal(const crypto::DocumentSeal& seal)
            {
                auto envelope = ref new DocumentEnvelope();
                envelope->SchemaVersion = seal.schemaVersion;
                envelope->KeyVersion = seal.keyVersion;
                envelope->Algorithm = ToPlatform(seal.algorithm);
                envelope->Nonce = ToPlatform(seal.nonce);
                envelope->Ciphertext = ToPlatform(seal.ciphertext);
                envelope->CiphertextHash = ToPlatform(seal.ciphertextHash);
                return envelope;
            }
        }
    }
}
