#include "pch.h"
#include "Bridge/HostKeyInfo.h"

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            HostKeyInfo::HostKeyInfo(Platform::String^ keyType,
                                     Platform::String^ fingerprintSha256,
                                     Platform::String^ randomArt)
                : keyType_(keyType), fingerprintSha256_(fingerprintSha256), randomArt_(randomArt)
            {
            }

            Platform::String^ HostKeyInfo::KeyType::get() { return keyType_; }
            Platform::String^ HostKeyInfo::FingerprintSha256::get() { return fingerprintSha256_; }
            Platform::String^ HostKeyInfo::RandomArt::get() { return randomArt_; }
        }
    }
}
