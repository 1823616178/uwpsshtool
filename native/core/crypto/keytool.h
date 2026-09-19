// K01: SSH 私钥生成与解析（doc/01-DESIGN.md §6.1 keytool；doc/03-SYNC-PROTOCOL.md §9）。
//
//   生成：ed25519（openssh-key-v1 未加密，手写组装：MAGIC + checkint + padding，
//         与 OpenSSH PROTOCOL.key 一致）/ RSA-3072/4096（PKCS#8 PEM，OpenSSL）。
//   解析：openssh-key-v1（手写解析：公钥 blob 直接取自文件头，无需短语；读
//         cipher/kdf 名判定是否加密）/ PEM 传统格式与 PKCS#8（OpenSSL
//         PEM_read_bio_PrivateKey，支持短语）。
//   导出："ssh-xxx <base64> <comment>" 公钥行；SHA256 公钥指纹（与 ssh-keygen
//         -lf 一致："SHA256:" + base64 无填充）。
//
// 约定：
//   - 纯逻辑（标准库 + OpenSSL），UWP 与宿主机测试共用；失败一律返回 false，
//     不抛异常、不区分失败原因（调用方只关心可用/不可用）。
//   - format 取值与同步协议 §4.1 对齐："openssh" / "pem"。
//   - encrypted 是文件属性：加密文件用正确短语解开后仍报告 true。
//   - 加密 PEM/PKCS#8 无短语时返回部分成功（encrypted=true，KeyType 仅当 PEM
//     头能直接判定〈如 RSA/DSA/EC 传统头〉时填写，bits=0，无公钥/指纹）；
//     短语非空但错误时返回失败。openssh-key-v1 加密时不校验短语（bcrypt 解密
//     不在此层实现，短语正确性由 libssh2 在认证时判定，见 N05）：公钥信息可
//     直接从文件头得出，短语被忽略。
//   - 私钥/短语材料用完即 OPENSSL_cleanse；PEM 生成忽略 comment（PEM 格式无
//     注释字段，comment 由上层存 KeyEntry 元数据）。
#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace sshclient {
namespace crypto {
namespace keytool {

// ---- 常量 ----
inline constexpr int kEd25519Bits = 256;
inline constexpr int kRsaBits3072 = 3072;
inline constexpr int kRsaBits4096 = 4096;

// ---- 解析结果 ----
struct KeyInspectInfo {
    std::string keyType; // "ssh-ed25519" / "ssh-rsa" / "ecdsa-sha2-nistpXXX" /
                         // "ssh-dss"；加密 PEM 无短语且头判定不出时为空
    int bits = 0;        // ed25519 256；ecdsa 256/384/521；RSA/DSA 模数位数；
                         // 未知时为 0
    std::string format;  // "openssh" / "pem"
    bool encrypted = false;
    std::string publicKeyOpenSsh;  // "ssh-xxx AAAA... comment"；无公钥信息时为空
    std::string fingerprintSha256; // "SHA256:..."（与 ssh-keygen -lf 一致）；无时为空
    std::string comment;           // openssh-key-v1 私钥区注释；PEM/加密时为空
    std::vector<uint8_t> publicKeyBlob; // SSH wire 公钥 blob；无时为空
};

// text 为私钥文件全文（UTF-8，LF/CRLF 均可）；passphrase 为 UTF-8 短语（无则空串）。
bool InspectPrivateKey(const std::string& privateKeyText, const std::string& passphrase,
                       KeyInspectInfo* out);

// 生成 ed25519 openssh-key-v1 未加密私钥（全文，LF 结尾）；comment 可为空。
bool GenerateEd25519(const std::string& comment, std::string* outPrivateKey);

// 生成 RSA 私钥（PKCS#8 PEM 全文，LF 结尾）；bits 仅接受 3072/4096。
bool GenerateRsa(int bits, std::string* outPrivateKey);

// ---- 纯函数（导出与指纹，供测试与上层复用） ----

// SHA256(data) -> "SHA256:<base64 无填充>"；OpenSSL 失败时返回空串。
std::string FingerprintSha256OfBlob(const uint8_t* data, size_t len);

// "keyType base64(blob) comment"（comment 为空时不带尾空格）。
std::string BuildPublicKeyLine(const std::string& keyType, const uint8_t* blob, size_t len,
                               const std::string& comment);

} // namespace keytool
} // namespace crypto
} // namespace sshclient
