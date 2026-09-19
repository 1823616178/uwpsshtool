// S03: 保险库密码学（doc/03-SYNC-PROTOCOL.md §3 的 native 实现）。
//
//   Argon2id（ref.c，ARGON2_NO_THREADS）/ AES-256-GCM / HKDF-SHA256 /
//   恢复密钥 / ciphertextHash。参数一律来自 sync_params.h。
//
// 约定（§3.3/§3.4 + S03 要点）：
//   - KDF 参数作为入参，创建与解锁一律做范围校验（踩坑 #1：禁止写死常量）。
//   - 解包/解密失败统一返回 false，不抛异常，不区分失败原因。
//   - 解密前先常量时间比对 ciphertextHash；Base64 必须规范形式。
//   - 恢复密钥校验段比较大小写不敏感（常量时间）。
//   - 所有密钥材料用完即 OPENSSL_cleanse。
// 禁止 include NAPI / WinRT / hilog：纯逻辑，UWP 与宿主机测试共用。
#pragma once

#include "sync_params.h"

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>
#include <vector>

namespace sshclient {
namespace crypto {

// ---- KDF 参数（与桌面端 Argon2idKdfParameters / C# KdfParametersData 同构） ----
struct KdfParameters {
    std::string algorithm = std::string(kKdfAlgorithm);
    std::uint32_t memoryKib = static_cast<std::uint32_t>(kDefaultKdfMemoryKib);
    std::uint32_t iterations = static_cast<std::uint32_t>(kDefaultKdfIterations);
    std::uint32_t parallelism = static_cast<std::uint32_t>(kDefaultKdfParallelism);
};

inline KdfParameters DefaultKdfParameters()
{
    return KdfParameters();
}

// 范围校验（§3.1）：algorithm == "argon2id"，memory 8192–1048576，
// iterations 1–20，parallelism 1–16。失败一律 false（上层不区分原因）。
bool ValidateKdfParameters(const KdfParameters &params);

void SecureClear(void *ptr, std::size_t len);

// 长度相等时常量时间比较（CRYPTO_memcmp）；长度不等直接 false。
bool ConstantTimeEqual(std::string_view a, std::string_view b);

std::string HexLower(const std::uint8_t *data, std::size_t len);
std::string HexUpper(const std::uint8_t *data, std::size_t len);
bool HexDecode(std::string_view hex, std::vector<std::uint8_t> *out);

// Argon2id（ref.c；parallelism > 1 时 NO_THREADS 下串行执行，结果与多线程一致）。
// password 为空、salt 为空、参数非法 → false。
bool Argon2idHash(std::string_view password, const std::uint8_t *salt, std::size_t salt_len,
                  const KdfParameters &params, std::uint8_t out[kKeyBytes]);

bool HkdfSha256(const std::uint8_t *ikm, std::size_t ikm_len, const std::uint8_t *salt,
                std::size_t salt_len, std::string_view info, std::uint8_t *out, std::size_t out_len);

/** 密文 = AES-256-GCM(pt) || tag(16)。成功时 out 长度为 pt_len + kTagBytes。 */
bool Aes256GcmEncrypt(const std::uint8_t key[kKeyBytes],
                      const std::uint8_t nonce[kNonceBytes], std::string_view aad,
                      const std::uint8_t *plaintext, std::size_t plaintext_len,
                      std::vector<std::uint8_t> *out);

bool Aes256GcmDecrypt(const std::uint8_t key[kKeyBytes],
                      const std::uint8_t nonce[kNonceBytes], std::string_view aad,
                      const std::uint8_t *ct_and_tag, std::size_t ct_and_tag_len,
                      std::vector<std::uint8_t> *plaintext);

/** SHA-256(密文含 tag) → 64 位小写十六进制 */
std::string CiphertextHashHex(const std::uint8_t *ct_and_tag, std::size_t len);

/** SPM1-<b64url43>-<12 位十六进制校验>（校验段写出大写，读入大小写均可） */
std::string EncodeRecoveryKey(const std::uint8_t raw[kKeyBytes]);
bool DecodeRecoveryKey(std::string_view encoded, std::uint8_t raw[kKeyBytes]);

/** HKDF-SHA256(salt 空, info = kHkdfInfoRecoveryKek) */
bool DeriveRecoveryKek(const std::uint8_t raw[kKeyBytes], std::uint8_t kek[kHkdfOutLen]);

/** 标准 Base64（信封 nonce / ciphertext / kdfSalt）。解码要求规范形式：
 *  字符集 + 长度 % 4 == 0 + 解码再编码与原文逐字节相等，否则 false。 */
std::string Base64StdEncode(const std::uint8_t *data, std::size_t len);
bool Base64StdDecode(std::string_view in, std::vector<std::uint8_t> *out);

bool RandomBytes(std::uint8_t *out, std::size_t len);

/** 密码/恢复密钥包裹后的保险库信封（字段为标准 Base64，§3.3 envelope） */
struct VaultWrap {
    int keyVersion = 1;
    std::string passwordWrappedKey;
    std::string passwordWrapNonce;
    std::string recoveryWrappedKey;
    std::string recoveryWrapNonce;
    std::string kdfSalt;
    KdfParameters kdfParameters;
};

struct DocumentSeal {
    int schemaVersion = kSchemaVersion;
    int keyVersion = 1;
    std::string algorithm = std::string(kAesAlgorithm);
    std::string nonce;
    std::string ciphertext;
    std::string ciphertextHash;
};

/** CreateVaultSetup(syncPassword, keyVersion)（§3.3）。失败时 vaultKey 已清零。 */
bool CreateVault(std::string_view password, int keyVersion, const KdfParameters &kdf,
                 VaultWrap *wrap, std::string *recoveryKey, std::uint8_t vaultKey[kKeyBytes]);
bool CreateVault(std::string_view password, int keyVersion, VaultWrap *wrap,
                 std::string *recoveryKey, std::uint8_t vaultKey[kKeyBytes]);

/** UnwrapWithPassword/UnwrapWithRecovery（§3.3）：KDF 参数一律读信封并校验。 */
bool UnlockWithPassword(std::string_view password, const VaultWrap &wrap,
                        std::uint8_t vaultKey[kKeyBytes]);

bool UnlockWithRecovery(std::string_view recoveryKey, const VaultWrap &wrap,
                        std::uint8_t vaultKey[kKeyBytes]);

bool RewrapVault(const std::uint8_t vaultKey[kKeyBytes], std::string_view newPassword,
                 int keyVersion, const KdfParameters &kdf, VaultWrap *wrap,
                 std::string *recoveryKey);
bool RewrapVault(const std::uint8_t vaultKey[kKeyBytes], std::string_view newPassword,
                 int keyVersion, VaultWrap *wrap, std::string *recoveryKey);

/** EncryptSyncDocument（§3.4）。 */
bool EncryptDocument(const std::uint8_t vaultKey[kKeyBytes], std::string_view vaultId,
                     int schemaVersion, int keyVersion, std::string_view plaintext,
                     DocumentSeal *out);

/** DecryptSyncDocument(envelope, vaultKey, vaultId)（§3.4）：
 *  algorithm 必须为 AES-256-GCM；ciphertext 规范解码后先常量时间比对
 *  ciphertextHash，通过才做 AES-GCM 解密；AAD 取自 seal 内版本号。 */
bool DecryptDocument(const std::uint8_t vaultKey[kKeyBytes], std::string_view vaultId,
                     const DocumentSeal &seal, std::string *plaintext);

} // namespace crypto
} // namespace sshclient
