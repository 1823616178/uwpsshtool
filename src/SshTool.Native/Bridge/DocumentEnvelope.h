#pragma once

// S05：加密文档信封（01-DESIGN.md §6.2 DocumentEnvelope；04-TASKS S05）。
// 即 PUT sync/document 的请求体（03-SYNC-PROTOCOL.md §3.4）：
// { schemaVersion, keyVersion, algorithm, nonce, ciphertext, ciphertextHash }。

#include "crypto/vault.hpp"

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            public ref class DocumentEnvelope sealed
            {
            public:
                DocumentEnvelope();

                property int SchemaVersion;
                property int KeyVersion;
                property Platform::String^ Algorithm;
                property Platform::String^ Nonce;
                property Platform::String^ Ciphertext;
                property Platform::String^ CiphertextHash;

            internal:
                bool TryGetSeal(sshclient::crypto::DocumentSeal* out);
                static DocumentEnvelope^ FromSeal(const sshclient::crypto::DocumentSeal& seal);
            };
        }
    }
}
