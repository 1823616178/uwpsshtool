// S04: 桌面端互通向量测试（doc/03-SYNC-PROTOCOL.md §3.5、§10.2）。
//
// 向量由 tools/sync-vectors/generate.mjs 生成（desktop_vectors.h + desktop-vectors.json
// + desktop-document-*.json 同内容），生成原语与桌面端 crypto-vault.ts 逐行对齐，
// 并经 tsx 只读导入桌面端解密侧自检通过。本测试断言 Lumia native 端：
//   1. 密码/恢复密钥两种解包均得到同一 vaultKey；
//   2. 3 份文档密文均能解开且明文与夹具一致；
//   3. 相同 salt/nonce 下重新加密逐字节等于向量（跨实现确定性）。
// 向量文件一旦提交禁止修改；本文件只读向量，不含任何真实用户密钥材料。
#include <gtest/gtest.h>

#include <cstring>
#include <string>
#include <vector>

#include "crypto/aad.hpp"
#include "crypto/vault.hpp"
#include "desktop_vectors.h"

namespace {

using sshclient::crypto::Aes256GcmEncrypt;
using sshclient::crypto::Argon2idHash;
using sshclient::crypto::Base64StdDecode;
using sshclient::crypto::CiphertextHashHex;
using sshclient::crypto::DecryptDocument;
using sshclient::crypto::DecodeRecoveryKey;
using sshclient::crypto::DeriveRecoveryKek;
using sshclient::crypto::DocumentSeal;
using sshclient::crypto::HexDecode;
using sshclient::crypto::KdfParameters;
using sshclient::crypto::SecureClear;
using sshclient::crypto::SyncDocumentAad;
using sshclient::crypto::UnlockWithPassword;
using sshclient::crypto::UnlockWithRecovery;
using sshclient::crypto::VaultKeyPasswordAad;
using sshclient::crypto::VaultKeyRecoveryAad;
using sshclient::crypto::VaultWrap;
namespace desktop = sshclient::crypto::desktop;

VaultWrap MakeDesktopWrap()
{
    VaultWrap wrap;
    wrap.keyVersion = desktop::kKeyVersion;
    wrap.passwordWrappedKey = desktop::kPasswordWrappedKeyB64;
    wrap.passwordWrapNonce = desktop::kPasswordWrapNonceB64;
    wrap.recoveryWrappedKey = desktop::kRecoveryWrappedKeyB64;
    wrap.recoveryWrapNonce = desktop::kRecoveryWrapNonceB64;
    wrap.kdfSalt = desktop::kKdfSaltB64;
    wrap.kdfParameters.algorithm = desktop::kKdfAlgorithm;
    wrap.kdfParameters.memoryKib = desktop::kKdfMemoryKib;
    wrap.kdfParameters.iterations = desktop::kKdfIterations;
    wrap.kdfParameters.parallelism = desktop::kKdfParallelism;
    return wrap;
}

DocumentSeal MakeDesktopSeal(const char *nonce_b64, const char *ct_b64, const char *hash)
{
    DocumentSeal seal;
    seal.schemaVersion = desktop::kSchemaVersion;
    seal.keyVersion = desktop::kKeyVersion;
    seal.algorithm = "AES-256-GCM";
    seal.nonce = nonce_b64;
    seal.ciphertext = ct_b64;
    seal.ciphertextHash = hash;
    return seal;
}

std::string HexOf(const std::uint8_t *data, std::size_t len)
{
    return sshclient::crypto::HexLower(data, len);
}

} // namespace

// S04 验收 native 其一：向量解包与解密成功。
TEST(DesktopVectors, UnwrapBothYieldsSameVaultKey)
{
    const VaultWrap wrap = MakeDesktopWrap();
    EXPECT_EQ(wrap.kdfParameters.algorithm, "argon2id");
    EXPECT_EQ(wrap.kdfParameters.memoryKib, 65536U);
    EXPECT_EQ(wrap.kdfParameters.iterations, 3U);
    EXPECT_EQ(wrap.kdfParameters.parallelism, 1U);

    std::uint8_t via_password[sshclient::crypto::kKeyBytes];
    ASSERT_TRUE(UnlockWithPassword(desktop::kSyncPassword, wrap, via_password));
    EXPECT_EQ(HexOf(via_password, sizeof(via_password)), desktop::kVaultKeyHex);
    EXPECT_EQ(std::memcmp(via_password, desktop::kVaultKey, sizeof(via_password)), 0);

    std::uint8_t via_recovery[sshclient::crypto::kKeyBytes];
    ASSERT_TRUE(UnlockWithRecovery(desktop::kRecoveryKey, wrap, via_recovery));
    EXPECT_EQ(HexOf(via_recovery, sizeof(via_recovery)), desktop::kVaultKeyHex);
    EXPECT_EQ(std::memcmp(via_password, via_recovery, sizeof(via_password)), 0);

    // 恢复密钥串与 raw 字节一致（Encode/Decode 往返由 S03 覆盖，此处只核对向量登记值）。
    std::uint8_t raw[sshclient::crypto::kKeyBytes];
    ASSERT_TRUE(DecodeRecoveryKey(desktop::kRecoveryKey, raw));
    EXPECT_EQ(std::memcmp(raw, desktop::kRecoveryRaw, sizeof(raw)), 0);

    SecureClear(via_password, sizeof(via_password));
    SecureClear(via_recovery, sizeof(via_recovery));
    SecureClear(raw, sizeof(raw));
}

TEST(DesktopVectors, DecryptAllDocuments)
{
    const VaultWrap wrap = MakeDesktopWrap();
    std::uint8_t key[sshclient::crypto::kKeyBytes];
    ASSERT_TRUE(UnlockWithPassword(desktop::kSyncPassword, wrap, key));

    const struct Case {
        const char *nonce_b64;
        const char *ct_b64;
        const char *hash;
        const char *plaintext;
        const char *plaintext_sha256;
    } cases[] = {
        {desktop::kDocEmptyNonceB64, desktop::kDocEmptyCiphertextB64,
         desktop::kDocEmptyCiphertextHash, desktop::kDocEmptyPlaintext,
         desktop::kDocEmptyPlaintextSha256},
        {desktop::kDocTypicalNonceB64, desktop::kDocTypicalCiphertextB64,
         desktop::kDocTypicalCiphertextHash, desktop::kDocTypicalPlaintext,
         desktop::kDocTypicalPlaintextSha256},
        {desktop::kDocSecretsNonceB64, desktop::kDocSecretsCiphertextB64,
         desktop::kDocSecretsCiphertextHash, desktop::kDocSecretsPlaintext,
         desktop::kDocSecretsPlaintextSha256},
    };
    for (const auto &c : cases) {
        const DocumentSeal seal = MakeDesktopSeal(c.nonce_b64, c.ct_b64, c.hash);
        std::string plain;
        ASSERT_TRUE(DecryptDocument(key, desktop::kVaultId, seal, &plain)) << c.plaintext_sha256;
        EXPECT_EQ(plain, c.plaintext);

        // ciphertextHash 登记值与 sha256(密文) 一致（解密前先验的输入）。
        std::vector<std::uint8_t> ct;
        ASSERT_TRUE(Base64StdDecode(c.ct_b64, &ct));
        EXPECT_EQ(CiphertextHashHex(ct.data(), ct.size()), std::string(c.hash));
    }
    SecureClear(key, sizeof(key));
}

// S04 验收 native 其二：相同 nonce/salt 下加密逐字节相等。
TEST(DesktopVectors, ReencryptWithFixedNonceIsByteIdentical)
{
    // 密码包裹：Argon2id(固定 salt) → AES-GCM(固定 nonce) 必须逐字节等于向量。
    KdfParameters kdf;
    kdf.algorithm = desktop::kKdfAlgorithm;
    kdf.memoryKib = desktop::kKdfMemoryKib;
    kdf.iterations = desktop::kKdfIterations;
    kdf.parallelism = desktop::kKdfParallelism;
    std::uint8_t pw_key[sshclient::crypto::kKeyBytes];
    ASSERT_TRUE(Argon2idHash(desktop::kSyncPassword, desktop::kKdfSalt,
                            sizeof(desktop::kKdfSalt), kdf, pw_key));

    const std::string pw_aad = VaultKeyPasswordAad(std::to_string(desktop::kKeyVersion));
    std::vector<std::uint8_t> pw_ct;
    ASSERT_TRUE(Aes256GcmEncrypt(pw_key, desktop::kPasswordWrapNonce, pw_aad,
                                desktop::kVaultKey, sizeof(desktop::kVaultKey),
                                &pw_ct));
    std::vector<std::uint8_t> expect_pw_ct;
    ASSERT_TRUE(Base64StdDecode(desktop::kPasswordWrappedKeyB64, &expect_pw_ct));
    EXPECT_EQ(pw_ct, expect_pw_ct);

    // 恢复包裹：HKDF(固定 raw) → AES-GCM(固定 nonce) 逐字节相等。
    std::uint8_t raw[sshclient::crypto::kKeyBytes];
    ASSERT_TRUE(DecodeRecoveryKey(desktop::kRecoveryKey, raw));
    std::uint8_t rec_kek[sshclient::crypto::kHkdfOutLen];
    ASSERT_TRUE(DeriveRecoveryKek(raw, rec_kek));
    const std::string rec_aad = VaultKeyRecoveryAad(std::to_string(desktop::kKeyVersion));
    std::vector<std::uint8_t> rec_ct;
    ASSERT_TRUE(Aes256GcmEncrypt(rec_kek, desktop::kRecoveryWrapNonce, rec_aad,
                                desktop::kVaultKey, sizeof(desktop::kVaultKey),
                                &rec_ct));
    std::vector<std::uint8_t> expect_rec_ct;
    ASSERT_TRUE(Base64StdDecode(desktop::kRecoveryWrappedKeyB64, &expect_rec_ct));
    EXPECT_EQ(rec_ct, expect_rec_ct);

    // 文档：固定 vaultKey/nonce/AAD 下 3 份明文逐字节相等（含 hash）。
    const struct DocCase {
        const char *nonce_b64;
        const char *ct_b64;
        const char *hash;
        const char *plaintext;
        const std::uint8_t *nonce_raw;
    } docs[] = {
        {desktop::kDocEmptyNonceB64, desktop::kDocEmptyCiphertextB64,
         desktop::kDocEmptyCiphertextHash, desktop::kDocEmptyPlaintext,
         desktop::kDocEmptyNonce},
        {desktop::kDocTypicalNonceB64, desktop::kDocTypicalCiphertextB64,
         desktop::kDocTypicalCiphertextHash, desktop::kDocTypicalPlaintext,
         desktop::kDocTypicalNonce},
        {desktop::kDocSecretsNonceB64, desktop::kDocSecretsCiphertextB64,
         desktop::kDocSecretsCiphertextHash, desktop::kDocSecretsPlaintext,
         desktop::kDocSecretsNonce},
    };
    const std::string doc_aad = SyncDocumentAad(desktop::kVaultId,
                                                std::to_string(desktop::kSchemaVersion),
                                                std::to_string(desktop::kKeyVersion));
    // 头登记的 nonce B64 必须等于 raw 字节（防止头与 JSON 两处不一致）。
    const char *nonce_b64s[] = {desktop::kDocEmptyNonceB64, desktop::kDocTypicalNonceB64,
                                desktop::kDocSecretsNonceB64};
    const std::uint8_t *nonce_raws[] = {desktop::kDocEmptyNonce, desktop::kDocTypicalNonce,
                                        desktop::kDocSecretsNonce};
    for (int i = 0; i < 3; ++i) {
        std::vector<std::uint8_t> nonce;
        ASSERT_TRUE(Base64StdDecode(nonce_b64s[i], &nonce));
        ASSERT_EQ(nonce.size(), sshclient::crypto::kNonceBytes);
        EXPECT_EQ(std::memcmp(nonce.data(), nonce_raws[i], nonce.size()), 0);
    }
    for (const auto &d : docs) {
        const auto *pt = reinterpret_cast<const std::uint8_t *>(d.plaintext);
        const std::size_t pt_len = std::strlen(d.plaintext);
        std::vector<std::uint8_t> ct;
        ASSERT_TRUE(Aes256GcmEncrypt(desktop::kVaultKey, d.nonce_raw, doc_aad, pt, pt_len,
                                    &ct));
        std::vector<std::uint8_t> expect_ct;
        ASSERT_TRUE(Base64StdDecode(d.ct_b64, &expect_ct));
        EXPECT_EQ(ct, expect_ct) << d.hash;
        EXPECT_EQ(CiphertextHashHex(ct.data(), ct.size()), std::string(d.hash));
    }

    SecureClear(pw_key, sizeof(pw_key));
    SecureClear(raw, sizeof(raw));
    SecureClear(rec_kek, sizeof(rec_kek));
}
