// S03: 保险库原语单测（doc/03-SYNC-PROTOCOL.md §3）。
//
// 覆盖：AES-GCM 往返与篡改拒绝 / 恢复密钥 10000 次随机往返与逐字符篡改被拒 /
// HKDF 确定性 / 黄金向量（Argon2id 默认参数 + 文档 AAD 加密 + ciphertextHash）。
// 失败一律返回 false（不抛异常），密钥材料用完清零。
#include <gtest/gtest.h>

#include "crypto/aad.hpp"
#include "crypto/vault.hpp"
#include "vault_golden_vectors.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <random>
#include <string>
#include <vector>

using sshclient::crypto::Aes256GcmDecrypt;
using sshclient::crypto::Aes256GcmEncrypt;
using sshclient::crypto::Argon2idHash;
using sshclient::crypto::CiphertextHashHex;
using sshclient::crypto::DecodeRecoveryKey;
using sshclient::crypto::DefaultKdfParameters;
using sshclient::crypto::DeriveRecoveryKek;
using sshclient::crypto::EncodeRecoveryKey;
using sshclient::crypto::HexDecode;
using sshclient::crypto::HexLower;
using sshclient::crypto::HkdfSha256;
using sshclient::crypto::KdfParameters;
using sshclient::crypto::SecureClear;
using sshclient::crypto::SyncDocumentAad;
using sshclient::crypto::kKeyBytes;
using sshclient::crypto::kCiphertextHashHexLen;
using sshclient::crypto::kHkdfOutLen;
using sshclient::crypto::kNonceBytes;
using sshclient::crypto::kTagBytes;

TEST(VaultTest, HexRoundTrip)
{
    const std::uint8_t raw[] = {0xDE, 0xAD, 0xBE, 0xEF};
    EXPECT_EQ(HexLower(raw, 4), "deadbeef");
    std::vector<std::uint8_t> back;
    ASSERT_TRUE(HexDecode("deadbeef", &back));
    ASSERT_EQ(back.size(), 4U);
    EXPECT_EQ(back[0], 0xDE);
    EXPECT_FALSE(HexDecode("zz", &back));
    EXPECT_FALSE(HexDecode("abc", &back)); // 奇数长度
}

TEST(VaultTest, AesGcmRoundTripAndTamperRejected)
{
    std::uint8_t key[kKeyBytes];
    std::uint8_t nonce[kNonceBytes];
    for (std::size_t i = 0; i < kKeyBytes; ++i) {
        key[i] = static_cast<std::uint8_t>(i + 1);
    }
    for (std::size_t i = 0; i < kNonceBytes; ++i) {
        nonce[i] = static_cast<std::uint8_t>(0xA0 + i);
    }
    const char pt[] = "hello vault";
    std::vector<std::uint8_t> ct;
    ASSERT_TRUE(Aes256GcmEncrypt(key, nonce, "aad-v1",
                                 reinterpret_cast<const std::uint8_t *>(pt), sizeof(pt) - 1,
                                 &ct));
    EXPECT_EQ(ct.size(), (sizeof(pt) - 1) + kTagBytes);

    std::vector<std::uint8_t> back;
    ASSERT_TRUE(Aes256GcmDecrypt(key, nonce, "aad-v1", ct.data(), ct.size(), &back));
    ASSERT_EQ(back.size(), sizeof(pt) - 1);
    EXPECT_EQ(std::memcmp(back.data(), pt, back.size()), 0);

    std::vector<std::uint8_t> wrong_aad;
    EXPECT_FALSE(Aes256GcmDecrypt(key, nonce, "aad-v2", ct.data(), ct.size(), &wrong_aad));

    ct[0] ^= 0x01;
    std::vector<std::uint8_t> tampered;
    EXPECT_FALSE(Aes256GcmDecrypt(key, nonce, "aad-v1", ct.data(), ct.size(), &tampered));

    // 短于 tag 的输入直接拒绝
    std::vector<std::uint8_t> short_ct(kTagBytes - 1, 0);
    std::vector<std::uint8_t> short_out;
    EXPECT_FALSE(
        Aes256GcmDecrypt(key, nonce, "aad-v1", short_ct.data(), short_ct.size(), &short_out));
    SecureClear(key, sizeof(key));
}

// S03 验收：恢复密钥 10000 次随机往返与逐字符篡改被拒。
// 随机源用固定种子 mt19937_64（结果可复现；真随机性由 RandomBytes 的
// RAND_bytes 保证，见 VaultOpsTest.CreateUnlockEncryptDecrypt）。
TEST(VaultTest, RecoveryKeyTenThousandRoundTripsAndTamperRejected)
{
    std::mt19937_64 rng(0x533033u); // "S03"
    std::uint8_t raw[kKeyBytes];
    std::uint8_t decoded[kKeyBytes];
    for (int i = 0; i < 10000; ++i) {
        for (std::size_t j = 0; j < kKeyBytes; ++j) {
            raw[j] = static_cast<std::uint8_t>(rng() & 0xFF);
        }
        const std::string enc = EncodeRecoveryKey(raw);
        ASSERT_FALSE(enc.empty()) << i;
        ASSERT_TRUE(DecodeRecoveryKey(enc, decoded)) << enc;
        EXPECT_EQ(std::memcmp(decoded, raw, kKeyBytes), 0);

        // 末字符翻转必被拒（校验段覆盖）
        std::string flipped = enc;
        flipped[flipped.size() - 1] = flipped.back() == 'A' ? 'B' : 'A';
        EXPECT_FALSE(DecodeRecoveryKey(flipped, decoded));
    }
    SecureClear(raw, sizeof(raw));
    SecureClear(decoded, sizeof(decoded));
}

// 对同一恢复密钥逐位置篡改：每个位置换一个同字符集的不同字符，必须全部被拒。
TEST(VaultTest, RecoveryKeyEveryPositionTamperRejected)
{
    const std::string enc = EncodeRecoveryKey(sshclient::crypto::golden::kRecoveryRaw);
    ASSERT_FALSE(enc.empty());
    std::uint8_t decoded[kKeyBytes];
    ASSERT_TRUE(DecodeRecoveryKey(enc, decoded)); // 先确认原串可解
    for (std::size_t i = 0; i < enc.size(); ++i) {
        if (enc[i] == '-') {
            continue; // 分隔符位置跳过（结构破坏类见 RejectsBadShape）
        }
        std::string tampered = enc;
        tampered[i] = (enc[i] == 'A' ? 'B' : 'A');
        EXPECT_FALSE(DecodeRecoveryKey(tampered, decoded)) << "pos " << i;
    }
    SecureClear(decoded, sizeof(decoded));
}

TEST(VaultTest, RecoveryKeyRejectsBadShape)
{
    std::uint8_t raw[kKeyBytes];
    EXPECT_FALSE(DecodeRecoveryKey("", raw));
    EXPECT_FALSE(DecodeRecoveryKey("SPM1-abc", raw));
    EXPECT_FALSE(DecodeRecoveryKey("XXX-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA-0123456789AB",
                                   raw));
    EXPECT_FALSE(DecodeRecoveryKey(
        "SPM1-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA-zzzzzzzzzzzz", raw)); // 非 hex
    EXPECT_FALSE(DecodeRecoveryKey(
        "SPM1-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA*-0123456789AB", raw)); // 非 b64url
}

// 校验段大小写不敏感（§3.3）：全小写校验段仍可解。
TEST(VaultTest, RecoveryKeyChecksumCaseInsensitive)
{
    std::string enc = EncodeRecoveryKey(sshclient::crypto::golden::kRecoveryRaw);
    ASSERT_FALSE(enc.empty());
    for (std::size_t i = enc.rfind('-') + 1; i < enc.size(); ++i) {
        enc[i] = static_cast<char>(std::tolower(static_cast<unsigned char>(enc[i])));
    }
    std::uint8_t decoded[kKeyBytes];
    EXPECT_TRUE(DecodeRecoveryKey(enc, decoded));
    EXPECT_EQ(std::memcmp(decoded, sshclient::crypto::golden::kRecoveryRaw, kKeyBytes), 0);
    SecureClear(decoded, sizeof(decoded));
}

static bool FillGoldenOutputs(std::string *vault_key_hex, std::string *ct_hex, std::string *hash,
                              std::string *recovery, std::string *kek_hex)
{
    const KdfParameters kdf = DefaultKdfParameters();
    std::uint8_t vault_key[kKeyBytes];
    if (!Argon2idHash(sshclient::crypto::golden::kPassword, sshclient::crypto::golden::kSalt,
                      sizeof(sshclient::crypto::golden::kSalt), kdf, vault_key)) {
        return false;
    }
    *vault_key_hex = HexLower(vault_key, kKeyBytes);

    const std::string aad =
        SyncDocumentAad(sshclient::crypto::golden::kVaultId,
                        sshclient::crypto::golden::kSchemaVersion,
                        sshclient::crypto::golden::kKeyVersion);
    const auto *pt = reinterpret_cast<const std::uint8_t *>(sshclient::crypto::golden::kPlaintext);
    const std::size_t pt_len = std::strlen(sshclient::crypto::golden::kPlaintext);
    std::vector<std::uint8_t> ct;
    if (!Aes256GcmEncrypt(vault_key, sshclient::crypto::golden::kNonce, aad, pt, pt_len,
                          &ct)) {
        SecureClear(vault_key, sizeof(vault_key));
        return false;
    }
    *ct_hex = HexLower(ct.data(), ct.size());
    *hash = CiphertextHashHex(ct.data(), ct.size());

    *recovery = EncodeRecoveryKey(sshclient::crypto::golden::kRecoveryRaw);
    std::uint8_t kek[kHkdfOutLen];
    if (!DeriveRecoveryKek(sshclient::crypto::golden::kRecoveryRaw, kek)) {
        SecureClear(vault_key, sizeof(vault_key));
        return false;
    }
    *kek_hex = HexLower(kek, kHkdfOutLen);

    SecureClear(vault_key, sizeof(vault_key));
    SecureClear(kek, sizeof(kek));
    return hash->size() == kCiphertextHashHexLen;
}

TEST(VaultGolden, GenerateOrAssertRegistered)
{
    std::string vault_key_hex;
    std::string ct_hex;
    std::string hash;
    std::string recovery;
    std::string kek_hex;
    ASSERT_TRUE(FillGoldenOutputs(&vault_key_hex, &ct_hex, &hash, &recovery, &kek_hex));

    if (std::getenv("SSH_GENERATE_GOLDEN") != nullptr) {
        std::printf("kVaultKeyHex[] = \"%s\";\n", vault_key_hex.c_str());
        std::printf("kCiphertextHex[] = \"%s\";\n", ct_hex.c_str());
        std::printf("kCiphertextHash[] = \"%s\";\n", hash.c_str());
        std::printf("kRecoveryKey[] = \"%s\";\n", recovery.c_str());
        std::printf("kRecoveryKekHex[] = \"%s\";\n", kek_hex.c_str());
        return;
    }

    EXPECT_EQ(vault_key_hex, sshclient::crypto::golden::kVaultKeyHex);
    EXPECT_EQ(ct_hex, sshclient::crypto::golden::kCiphertextHex);
    EXPECT_EQ(hash, sshclient::crypto::golden::kCiphertextHash);
    EXPECT_EQ(recovery, sshclient::crypto::golden::kRecoveryKey);
    EXPECT_EQ(kek_hex, sshclient::crypto::golden::kRecoveryKekHex);

    // 用登记的 vaultKey 把登记的密文解开（交叉验证 HexDecode + Decrypt 路径）。
    std::uint8_t key[kKeyBytes];
    std::vector<std::uint8_t> key_vec;
    ASSERT_TRUE(HexDecode(vault_key_hex, &key_vec));
    ASSERT_EQ(key_vec.size(), kKeyBytes);
    std::memcpy(key, key_vec.data(), kKeyBytes);
    std::vector<std::uint8_t> ct;
    ASSERT_TRUE(HexDecode(ct_hex, &ct));
    const std::string aad =
        SyncDocumentAad(sshclient::crypto::golden::kVaultId,
                        sshclient::crypto::golden::kSchemaVersion,
                        sshclient::crypto::golden::kKeyVersion);
    std::vector<std::uint8_t> pt;
    ASSERT_TRUE(Aes256GcmDecrypt(key, sshclient::crypto::golden::kNonce, aad, ct.data(),
                                 ct.size(), &pt));
    EXPECT_EQ(std::string(pt.begin(), pt.end()), sshclient::crypto::golden::kPlaintext);
    SecureClear(key, sizeof(key));
}

TEST(VaultTest, HkdfDeterministicEmptySalt)
{
    std::uint8_t ikm[32];
    for (int i = 0; i < 32; ++i) {
        ikm[i] = static_cast<std::uint8_t>(i);
    }
    std::uint8_t a[32];
    std::uint8_t b[32];
    ASSERT_TRUE(HkdfSha256(ikm, 32, nullptr, 0, sshclient::crypto::kHkdfInfoRecoveryKek, a, 32));
    ASSERT_TRUE(HkdfSha256(ikm, 32, nullptr, 0, sshclient::crypto::kHkdfInfoRecoveryKek, b, 32));
    EXPECT_EQ(std::memcmp(a, b, 32), 0);
    std::uint8_t other[32];
    ASSERT_TRUE(HkdfSha256(ikm, 32, nullptr, 0, "other-info", other, 32));
    EXPECT_NE(std::memcmp(a, other, 32), 0);
}
