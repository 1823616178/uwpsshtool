// S15 私钥指纹向量（由 tools/sync-vectors/private-keys.mjs 生成，禁止手改）。
// fingerprint 经桌面端 node_modules/ssh2 的 utils.parseKey(...).getPublicSSH() + SHA256 计算；
// PKCS#8 两类 ssh2 不支持，指纹取自 ssh-keygen .pub 的 wire blob（与 getPublicSSH 同物）。
// 与 tests/fixtures/sync/private-key-vectors.json 同内容；用法见 keytool_test.cpp KeytoolInteropTest。
#pragma once

#include <cstddef>

namespace sshclient {
namespace crypto {
namespace privkey_vectors {

struct Entry {
    const char* file;
    const char* keyType;
    const char* format;
    bool encrypted;
    const char* fingerprint;
};

inline constexpr Entry kEntries[] = {
    {"ecdsa256_openssh", "ecdsa-sha2-nistp256", "openssh", false, "SHA256:UJrZXMGpcZinQmImA1XLqwoMjrDYamoLaGGSwZ96ct4"},
    {"ecdsa256_openssh_enc", "ecdsa-sha2-nistp256", "openssh", true, "SHA256:gnqPGEyDP3LPm3x+AZttdBMV+LHReZEYAx0wZY2k7Rw"},
    {"ecdsa256_pem", "ecdsa-sha2-nistp256", "pem", false, "SHA256:g8kFBlsvX4TvmTuj95FpX63QNRP5mTA+wS8RDImJYHM"},
    {"ed25519_openssh", "ssh-ed25519", "openssh", false, "SHA256:yUjvGHYmmaSO8ZPFpMC28TReVGeR3FkkcVj3rSpFG5o"},
    {"ed25519_openssh_enc", "ssh-ed25519", "openssh", true, "SHA256:Kt7nAiMYqX3aCxwlk8I6mkpb3+C4NSDAxdGV4xbRuIM"},
    {"rsa3072_openssh", "ssh-rsa", "openssh", false, "SHA256:YMyjFtTO5gbh3EkFKgccRHf1C4fpCfuK/Ef6HU+1EX8"},
    {"rsa3072_openssh_enc", "ssh-rsa", "openssh", true, "SHA256:mTHmshYeYDmV3kbctMKgh8c47tj8hEKE5XBh9Exshks"},
    {"rsa3072_pem", "ssh-rsa", "pem", false, "SHA256:sbq1ZDO6nyRdE5tVw+RYev/FnGeRurZpfbCIosdvmfU"},
    {"rsa3072_pem_enc", "ssh-rsa", "pem", true, "SHA256:lVKB4oa4VrXb6IwrkA2UV7sku1BuL3ILvo1wG6k130s"},
    {"rsa3072_pkcs8", "ssh-rsa", "pem", false, "SHA256:7B2vS6QZ/8vVkyv4b9VSzgMqrCXmZzMCxEwK0jiubVo"},
    {"rsa3072_pkcs8_enc", "ssh-rsa", "pem", true, "SHA256:oq30y+YjN2/MZuWs6yqQpWQz3B7sFd8XMBjRGfgxD1g"},
};

inline constexpr std::size_t kEntryCount = 11;

} // namespace privkey_vectors
} // namespace crypto
} // namespace sshclient
