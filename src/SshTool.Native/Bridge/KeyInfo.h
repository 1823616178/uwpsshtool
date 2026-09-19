#pragma once

// K01：密钥解析结果（01-DESIGN.md §6.2 KeyInfo；04-TASKS K01）。
// 由 native 构造，C# 只读。字段语义见 native/core/crypto/keytool.h：
// KeyType 为空 + Encrypted=true 表示“加密且未给短语，类型从 PEM 头判定不出”
// （如 ENCRYPTED PRIVATE KEY）；此时公钥/指纹亦为空，上层据此提示输入短语。
// Comment 为 §6.2 草图之外的增补（openssh-key-v1 私钥区注释；PEM/加密时为空），
// 供密钥管理页显示与 .pub 行往返比对。

#include "crypto/keytool.h"

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            public ref class KeyInfo sealed
            {
            public:
                property Platform::String^ KeyType { Platform::String^ get(); }
                property int Bits { int get(); }
                property Platform::String^ Format { Platform::String^ get(); }
                property bool Encrypted { bool get(); }
                property Platform::String^ PublicKeyOpenSsh { Platform::String^ get(); }
                property Platform::String^ FingerprintSha256 { Platform::String^ get(); }
                property Platform::String^ Comment { Platform::String^ get(); }

            internal:
                static KeyInfo^ FromInfo(const sshclient::crypto::keytool::KeyInspectInfo& info);

            private:
                KeyInfo(Platform::String^ keyType, int bits, Platform::String^ format,
                        bool encrypted, Platform::String^ publicKeyOpenSsh,
                        Platform::String^ fingerprintSha256, Platform::String^ comment);

                Platform::String^ keyType_;
                int bits_;
                Platform::String^ format_;
                bool encrypted_;
                Platform::String^ publicKeyOpenSsh_;
                Platform::String^ fingerprintSha256_;
                Platform::String^ comment_;
            };
        }
    }
}
