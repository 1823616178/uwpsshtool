#pragma once

// S05：保险库信封（01-DESIGN.md §6.2 VaultEnvelope；04-TASKS S05）。
// 由 C# 侧填充后传入解包/解密，或由 CreateAsync 产出。字段与桌面端
// VaultKeyEnvelope（sync-types.ts）逐键对应；KDF 参数一律来自本信封，
// native 只做范围校验（踩坑 #1），绝不写死常量。

#include "crypto/vault.hpp"

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            public ref class VaultEnvelope sealed
            {
            public:
                VaultEnvelope();

                property int KeyVersion;
                property Platform::String^ PasswordWrappedKey;
                property Platform::String^ PasswordWrapNonce;
                property Platform::String^ RecoveryWrappedKey;
                property Platform::String^ RecoveryWrapNonce;
                property Platform::String^ KdfSalt;
                property Platform::String^ KdfAlgorithm;
                property int KdfMemory;
                property int KdfIterations;
                property int KdfParallelism;

            internal:
                // WinRT 属性 → core VaultWrap（含 KDF 范围校验入口）。
                // 失败返回 false（调用方统一转 nullptr，不区分原因）。
                bool TryGetWrap(sshclient::crypto::VaultWrap* out);
                static VaultEnvelope^ FromWrap(const sshclient::crypto::VaultWrap& wrap);
            };
        }
    }
}
