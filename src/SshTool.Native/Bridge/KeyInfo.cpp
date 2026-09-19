#include "pch.h"
#include "Bridge/KeyInfo.h"

#include "Bridge/BridgeUtil.h"

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            KeyInfo::KeyInfo(Platform::String^ keyType, int bits, Platform::String^ format,
                             bool encrypted, Platform::String^ publicKeyOpenSsh,
                             Platform::String^ fingerprintSha256, Platform::String^ comment)
                : keyType_(keyType)
                , bits_(bits)
                , format_(format)
                , encrypted_(encrypted)
                , publicKeyOpenSsh_(publicKeyOpenSsh)
                , fingerprintSha256_(fingerprintSha256)
                , comment_(comment)
            {
            }

            KeyInfo^ KeyInfo::FromInfo(const sshclient::crypto::keytool::KeyInspectInfo& info)
            {
                return ref new KeyInfo(ToPlatform(info.keyType), info.bits, ToPlatform(info.format),
                                       info.encrypted, ToPlatform(info.publicKeyOpenSsh),
                                       ToPlatform(info.fingerprintSha256), ToPlatform(info.comment));
            }

            Platform::String^ KeyInfo::KeyType::get() { return keyType_; }
            int KeyInfo::Bits::get() { return bits_; }
            Platform::String^ KeyInfo::Format::get() { return format_; }
            bool KeyInfo::Encrypted::get() { return encrypted_; }
            Platform::String^ KeyInfo::PublicKeyOpenSsh::get() { return publicKeyOpenSsh_; }
            Platform::String^ KeyInfo::FingerprintSha256::get() { return fingerprintSha256_; }
            Platform::String^ KeyInfo::Comment::get() { return comment_; }
        }
    }
}
