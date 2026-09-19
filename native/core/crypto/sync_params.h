// S03: 同步协议密码学常量（doc/03-SYNC-PROTOCOL.md §3.1 的 native 唯一出处）。
//
// 与桌面端 src/main/security/crypto-vault.ts、src/shared/sync-types.ts 及
// C# SshTool.Core/Sync/Protocol/SyncConstants.cs 逐项对齐（同名对应，
// 命名按 native 惯例加 k 前缀）。改动任一值都会破坏跨端互通，禁止。
// crypto/* 与后续桥接只准 include 本文件或 aad.hpp，禁止再写这些字面量。
#pragma once

#include <cstddef>
#include <string_view>

namespace sshclient {
namespace crypto {

// ---- 文档与算法 ----
inline constexpr int kSchemaVersion = 1;
inline constexpr std::string_view kKdfAlgorithm = "argon2id";
inline constexpr int kDefaultKdfMemoryKib = 65536;
inline constexpr int kDefaultKdfIterations = 3;
inline constexpr int kDefaultKdfParallelism = 1;

// ---- KDF 参数允许范围（解锁时校验信封，§3.1/§3.3） ----
inline constexpr int kKdfMemoryKibMin = 8192;
inline constexpr int kKdfMemoryKibMax = 1048576;
inline constexpr int kKdfIterationsMin = 1;
inline constexpr int kKdfIterationsMax = 20;
inline constexpr int kKdfParallelismMin = 1;
inline constexpr int kKdfParallelismMax = 16;

inline constexpr std::size_t kKeyBytes = 32;
inline constexpr std::size_t kKdfSaltBytes = 16;

// 历史别名（与鸿蒙端 vault 实现同名，便于对拍）。
inline constexpr std::size_t kArgon2HashLen = kKeyBytes;
inline constexpr std::size_t kKdfSaltLen = kKdfSaltBytes;

inline constexpr std::string_view kAesAlgorithm = "AES-256-GCM";
inline constexpr std::size_t kNonceBytes = 12;
inline constexpr std::size_t kTagBytes = 16; // tag 拼在密文末尾
inline constexpr std::size_t kAesNonceLen = kNonceBytes;
inline constexpr std::size_t kAesTagLen = kTagBytes;

// ---- AAD 域（§3.2） ----
inline constexpr std::string_view kAadDomainDocument = "ssh-port-mapper/sync-document/v1";
inline constexpr std::string_view kAadDomainPasswordWrap = "ssh-port-mapper/vault-key/password/v1";
inline constexpr std::string_view kAadDomainRecoveryWrap = "ssh-port-mapper/vault-key/recovery/v1";
inline constexpr std::string_view kHkdfInfoRecoveryKek = "ssh-port-mapper/recovery-kek/v1";
inline constexpr std::size_t kHkdfOutLen = 32;

// 历史别名（与鸿蒙端 aad 实现同名）。
inline constexpr std::string_view kAadDomainSyncDocument = kAadDomainDocument;
inline constexpr std::string_view kAadDomainVaultKeyPassword = kAadDomainPasswordWrap;
inline constexpr std::string_view kAadDomainVaultKeyRecovery = kAadDomainRecoveryWrap;

inline constexpr std::string_view kRecoveryKeyPrefix = "SPM1";
inline constexpr std::size_t kRecoveryKeyRawB64urlLen = 43;
inline constexpr std::size_t kRecoveryKeyCheckHexLen = 12;
inline constexpr std::size_t kCiphertextHashHexLen = 64;

inline constexpr std::size_t kDocumentMaxBytes = 2097152;
inline constexpr std::size_t kPrivateKeyMaxBytes = 262144;
inline constexpr int kServersMax = 5000;
inline constexpr int kTunnelsMax = 10000;
inline constexpr int kGroupsMax = 1000;

// 历史别名。
inline constexpr int kHostCountMax = kServersMax;

} // namespace crypto
} // namespace sshclient
