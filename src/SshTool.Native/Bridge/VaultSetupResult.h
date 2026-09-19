#pragma once

// S05：保险库创建结果（01-DESIGN.md §6.2 VaultSetupResult；04-TASKS S05）。
// CreateAsync 的返回值：信封 + 恢复密钥（展示一次，用户抄写）+
// vaultKeyBase64（调用方立即写入 VaultCache，见 S11）。

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            ref class VaultEnvelope;

            public ref class VaultSetupResult sealed
            {
            public:
                VaultSetupResult();

                property VaultEnvelope^ Envelope;
                property Platform::String^ RecoveryKey;
                property Platform::String^ VaultKeyBase64;
            };
        }
    }
}
