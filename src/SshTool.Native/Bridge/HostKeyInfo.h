#pragma once

// N09a：对端主机密钥信息（01-DESIGN §6.2）。由 native 构造，C# 只读。

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            public ref class HostKeyInfo sealed
            {
            public:
                property Platform::String^ KeyType { Platform::String^ get(); }
                property Platform::String^ FingerprintSha256 { Platform::String^ get(); }
                property Platform::String^ RandomArt { Platform::String^ get(); }

            internal:
                HostKeyInfo(Platform::String^ keyType, Platform::String^ fingerprintSha256,
                            Platform::String^ randomArt);

            private:
                Platform::String^ keyType_;
                Platform::String^ fingerprintSha256_;
                Platform::String^ randomArt_;
            };
        }
    }
}
