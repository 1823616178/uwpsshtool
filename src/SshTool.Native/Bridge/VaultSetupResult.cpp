#include "pch.h"
#include "Bridge/VaultSetupResult.h"

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            VaultSetupResult::VaultSetupResult()
            {
                Envelope = nullptr;
                RecoveryKey = ref new Platform::String(L"");
                VaultKeyBase64 = ref new Platform::String(L"");
            }
        }
    }
}
