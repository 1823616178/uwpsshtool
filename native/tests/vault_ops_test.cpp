// S03: 保险库高层操作单测（doc/03-SYNC-PROTOCOL.md §3.3/§3.4）。
//
// 覆盖：建库/密码解包/恢复密钥解包/文档加解密/Rewrap 全链路；
// 篡改 ciphertext / hash / AAD（vaultId、keyVersion）/ nonce 均解密失败；
// 非法 KDF 参数被拒；非规范 Base64 被拒；KDF 参数读信封（篡改即解不开）。
// 为控制 Argon2 耗时，多数用例使用合法小参数（8192/1/1）；默认生产参数
// （65536/3/1）由 DefaultParamsEndToEnd 与 VaultGolden 覆盖。
#include <gtest/gtest.h>

#include "crypto/vault.hpp"

#include <cstring>
#include <string>
#include <vector>

using sshclient::crypto::Base64StdDecode;
using sshclient::crypto::Base64StdEncode;
using sshclient::crypto::CreateVault;
using sshclient::crypto::DecryptDocument;
using sshclient::crypto::DefaultKdfParameters;
using sshclient::crypto::DocumentSeal;
using sshclient::crypto::EncryptDocument;
using sshclient::crypto::KdfParameters;
using sshclient::crypto::RewrapVault;
using sshclient::crypto::SecureClear;
using sshclient::crypto::UnlockWithPassword;
using sshclient::crypto::UnlockWithRecovery;
using sshclient::crypto::ValidateKdfParameters;
using sshclient::crypto::VaultWrap;
using sshclient::crypto::kKeyBytes;

namespace {

KdfParameters FastKdf()
{
    KdfParameters kdf;
    kdf.memoryKib = 8192;
    kdf.iterations = 1;
    kdf.parallelism = 1;
    return kdf;
}

} // namespace

TEST(VaultOpsTest, Base64StdRoundTripAndCanonical)
{
    const std::uint8_t raw[] = {0x00, 0x01, 0xFE, 0xFF, 0x10};
    const std::string b64 = Base64StdEncode(raw, sizeof(raw));
    EXPECT_FALSE(b64.empty());
    std::vector<std::uint8_t> back;
    ASSERT_TRUE(Base64StdDecode(b64, &back));
    ASSERT_EQ(back.size(), sizeof(raw));
    EXPECT_EQ(std::memcmp(back.data(), raw, sizeof(raw)), 0);

    // 规范形式检查（桌面端 decodeBase64 语义）
    std::vector<std::uint8_t> out;
    EXPECT_FALSE(Base64StdDecode("ABC", &out));   // 长度 % 4 != 0
    EXPECT_FALSE(Base64StdDecode("AB C", &out));  // 空白
    EXPECT_FALSE(Base64StdDecode("AB*C", &out));  // 非字母表
    EXPECT_TRUE(Base64StdDecode("AQ==", &out));   // 0x01 的规范编码
    ASSERT_EQ(out.size(), 1U);
    EXPECT_EQ(out[0], 0x01);
    EXPECT_FALSE(Base64StdDecode("AR==", &out));  // 尾比特非零 → 重编码不等
    EXPECT_FALSE(Base64StdDecode("AQ", &out));    // 缺填充 → 长度非法
    EXPECT_FALSE(Base64StdDecode("A===", &out));  // 超过 2 个填充
    EXPECT_TRUE(Base64StdDecode("", &out));       // 空串按空字节成功
    EXPECT_TRUE(out.empty());
}

TEST(VaultOpsTest, ValidateKdfParametersRanges)
{
    EXPECT_TRUE(ValidateKdfParameters(DefaultKdfParameters()));
    EXPECT_TRUE(ValidateKdfParameters(FastKdf()));

    KdfParameters max;
    max.memoryKib = 1048576;
    max.iterations = 20;
    max.parallelism = 16;
    EXPECT_TRUE(ValidateKdfParameters(max)); // 仅断言范围接受，不实际哈希（1 GiB × 20 轮）

    const KdfParameters good = FastKdf();
    KdfParameters bad = good;
    bad.algorithm = "argon2i";
    EXPECT_FALSE(ValidateKdfParameters(bad));
    bad = good;
    bad.algorithm = "";
    EXPECT_FALSE(ValidateKdfParameters(bad));
    bad = good;
    bad.memoryKib = 8191;
    EXPECT_FALSE(ValidateKdfParameters(bad));
    bad = good;
    bad.memoryKib = 1048577;
    EXPECT_FALSE(ValidateKdfParameters(bad));
    bad = good;
    bad.memoryKib = 0;
    EXPECT_FALSE(ValidateKdfParameters(bad));
    bad = good;
    bad.iterations = 0;
    EXPECT_FALSE(ValidateKdfParameters(bad));
    bad = good;
    bad.iterations = 21;
    EXPECT_FALSE(ValidateKdfParameters(bad));
    bad = good;
    bad.parallelism = 0;
    EXPECT_FALSE(ValidateKdfParameters(bad));
    bad = good;
    bad.parallelism = 17;
    EXPECT_FALSE(ValidateKdfParameters(bad));
}

TEST(VaultOpsTest, CreateUnlockEncryptDecrypt)
{
    const KdfParameters kdf = FastKdf();
    VaultWrap wrap;
    std::string recovery;
    std::uint8_t key[kKeyBytes];
    ASSERT_TRUE(CreateVault("password12ab", 1, kdf, &wrap, &recovery, key));
    EXPECT_EQ(wrap.keyVersion, 1);
    EXPECT_FALSE(wrap.kdfSalt.empty());
    EXPECT_EQ(wrap.kdfParameters.memoryKib, 8192U);
    EXPECT_EQ(recovery.substr(0, 5), "SPM1-");

    std::uint8_t unlocked[kKeyBytes];
    ASSERT_TRUE(UnlockWithPassword("password12ab", wrap, unlocked));
    EXPECT_EQ(std::memcmp(key, unlocked, kKeyBytes), 0);

    std::uint8_t bad[kKeyBytes];
    EXPECT_FALSE(UnlockWithPassword("wrong-password", wrap, bad));

    std::uint8_t via_rec[kKeyBytes];
    ASSERT_TRUE(UnlockWithRecovery(recovery, wrap, via_rec));
    EXPECT_EQ(std::memcmp(key, via_rec, kKeyBytes), 0);
    EXPECT_FALSE(UnlockWithRecovery("SPM1-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA-"
                                    "0123456789AB",
                                    wrap, bad));

    DocumentSeal seal;
    ASSERT_TRUE(EncryptDocument(key, "vault-id-1", 1, 1, "{\"schemaVersion\":1}", &seal));
    EXPECT_EQ(seal.algorithm, "AES-256-GCM");
    EXPECT_EQ(seal.ciphertextHash.size(), 64U);

    std::string plain;
    ASSERT_TRUE(DecryptDocument(key, "vault-id-1", seal, &plain));
    EXPECT_EQ(plain, "{\"schemaVersion\":1}");

    // AAD 字段 vaultId 不一致 → 失败
    std::string wrong_id;
    EXPECT_FALSE(DecryptDocument(key, "other-vault", seal, &wrong_id));

    // AAD 字段 keyVersion 不一致 → 失败
    DocumentSeal wrong_kv = seal;
    wrong_kv.keyVersion = 2;
    std::string wrong_kv_out;
    EXPECT_FALSE(DecryptDocument(key, "vault-id-1", wrong_kv, &wrong_kv_out));

    // algorithm 非法 → 失败
    DocumentSeal wrong_alg = seal;
    wrong_alg.algorithm = "AES-256-CBC";
    std::string wrong_alg_out;
    EXPECT_FALSE(DecryptDocument(key, "vault-id-1", wrong_alg, &wrong_alg_out));

    VaultWrap wrap2;
    std::string recovery2;
    ASSERT_TRUE(RewrapVault(key, "new-password1", 1, kdf, &wrap2, &recovery2));
    std::uint8_t unlocked2[kKeyBytes];
    ASSERT_TRUE(UnlockWithPassword("new-password1", wrap2, unlocked2));
    EXPECT_EQ(std::memcmp(key, unlocked2, kKeyBytes), 0);
    EXPECT_FALSE(UnlockWithPassword("password12ab", wrap2, bad));
    EXPECT_NE(recovery2, recovery);

    SecureClear(key, sizeof(key));
    SecureClear(unlocked, sizeof(unlocked));
    SecureClear(via_rec, sizeof(via_rec));
    SecureClear(unlocked2, sizeof(unlocked2));
}

// 生产默认参数（65536/3/1）端到端一次：建库 + 密码解包一致。
TEST(VaultOpsTest, DefaultParamsEndToEnd)
{
    VaultWrap wrap;
    std::string recovery;
    std::uint8_t key[kKeyBytes];
    ASSERT_TRUE(CreateVault("default-params-pw", 1, &wrap, &recovery, key));
    EXPECT_EQ(wrap.kdfParameters.memoryKib, 65536U);
    std::uint8_t unlocked[kKeyBytes];
    ASSERT_TRUE(UnlockWithPassword("default-params-pw", wrap, unlocked));
    EXPECT_EQ(std::memcmp(key, unlocked, kKeyBytes), 0);
    SecureClear(key, sizeof(key));
    SecureClear(unlocked, sizeof(unlocked));
}

TEST(VaultOpsTest, DecryptTamperRejected)
{
    const KdfParameters kdf = FastKdf();
    VaultWrap wrap;
    std::string recovery;
    std::uint8_t key[kKeyBytes];
    ASSERT_TRUE(CreateVault("tamper-pw-01", 1, kdf, &wrap, &recovery, key));

    DocumentSeal seal;
    ASSERT_TRUE(EncryptDocument(key, "vault-tamper", 1, 1, "secret-bytes", &seal));

    // 篡改 ciphertext（首字节翻转）→ hash 先验即失败
    {
        DocumentSeal t = seal;
        std::vector<std::uint8_t> ct;
        ASSERT_TRUE(Base64StdDecode(t.ciphertext, &ct));
        ct[0] ^= 0x01;
        t.ciphertext = Base64StdEncode(ct.data(), ct.size());
        std::string out;
        EXPECT_FALSE(DecryptDocument(key, "vault-tamper", t, &out));
    }
    // 篡改 ciphertextHash → 常量时间比对失败
    {
        DocumentSeal t = seal;
        t.ciphertextHash[0] = (t.ciphertextHash[0] == '0' ? '1' : '0');
        std::string out;
        EXPECT_FALSE(DecryptDocument(key, "vault-tamper", t, &out));
    }
    // 全零 hash → 失败
    {
        DocumentSeal t = seal;
        t.ciphertextHash = std::string(64, '0');
        std::string out;
        EXPECT_FALSE(DecryptDocument(key, "vault-tamper", t, &out));
    }
    // 篡改 nonce → 失败
    {
        DocumentSeal t = seal;
        std::vector<std::uint8_t> nonce;
        ASSERT_TRUE(Base64StdDecode(t.nonce, &nonce));
        nonce[0] ^= 0x01;
        t.nonce = Base64StdEncode(nonce.data(), nonce.size());
        std::string out;
        EXPECT_FALSE(DecryptDocument(key, "vault-tamper", t, &out));
    }
    // 非规范 Base64 ciphertext → 失败
    {
        DocumentSeal t = seal;
        t.ciphertext = "AR==";
        t.ciphertextHash = std::string(64, '0');
        std::string out;
        EXPECT_FALSE(DecryptDocument(key, "vault-tamper", t, &out));
    }
    // 错误 vaultKey → 失败（hash 通过，GCM tag 失败）
    {
        std::uint8_t other[kKeyBytes];
        std::memset(other, 0x5A, sizeof(other));
        std::string out;
        EXPECT_FALSE(DecryptDocument(other, "vault-tamper", seal, &out));
        SecureClear(other, sizeof(other));
    }

    SecureClear(key, sizeof(key));
}

// KDF 参数必须读信封：创建一个小参数信封，篡改其参数后解包必失败；
// 非法参数创建直接被拒。
TEST(VaultOpsTest, EnvelopeKdfParametersHonored)
{
    const KdfParameters kdf = FastKdf();
    VaultWrap wrap;
    std::string recovery;
    std::uint8_t key[kKeyBytes];
    ASSERT_TRUE(CreateVault("kdf-envelope", 1, kdf, &wrap, &recovery, key));

    // 信封参数被改大（8192 → 16384）：派生出不同口令钥 → 解包失败
    {
        VaultWrap t = wrap;
        t.kdfParameters.memoryKib = 16384;
        std::uint8_t out[kKeyBytes];
        EXPECT_FALSE(UnlockWithPassword("kdf-envelope", t, out));
    }
    // 信封算法名被改：范围校验直接拒绝
    {
        VaultWrap t = wrap;
        t.kdfParameters.algorithm = "argon2i";
        std::uint8_t out[kKeyBytes];
        EXPECT_FALSE(UnlockWithPassword("kdf-envelope", t, out));
    }
    // 信封 salt 损坏：解码失败
    {
        VaultWrap t = wrap;
        t.kdfSalt = "AR==";
        std::uint8_t out[kKeyBytes];
        EXPECT_FALSE(UnlockWithPassword("kdf-envelope", t, out));
    }

    // 非法参数创建被拒（每类一个代表）
    {
        VaultWrap w;
        std::string r;
        std::uint8_t k[kKeyBytes];
        KdfParameters bad = kdf;
        bad.memoryKib = 0;
        EXPECT_FALSE(CreateVault("pw", 1, bad, &w, &r, k));
        bad = kdf;
        bad.iterations = 21;
        EXPECT_FALSE(CreateVault("pw", 1, bad, &w, &r, k));
        bad = kdf;
        bad.parallelism = 0;
        EXPECT_FALSE(CreateVault("pw", 1, bad, &w, &r, k));
        EXPECT_FALSE(CreateVault("", 1, kdf, &w, &r, k));   // 空口令
        EXPECT_FALSE(CreateVault("pw", 0, kdf, &w, &r, k)); // keyVersion 非法
    }

    SecureClear(key, sizeof(key));
}

// 固定 key/nonce/AAD 下加密确定（S04 向量工具的前置性质）。
TEST(VaultOpsTest, AesGcmDeterministicWithFixedNonce)
{
    std::uint8_t key[kKeyBytes];
    std::uint8_t nonce[12];
    for (std::size_t i = 0; i < kKeyBytes; ++i) {
        key[i] = static_cast<std::uint8_t>(i);
    }
    for (std::size_t i = 0; i < 12; ++i) {
        nonce[i] = static_cast<std::uint8_t>(0x10 + i);
    }
    const std::string aad = "ssh-port-mapper/sync-document/v1|12:vault-golden|1:1|1:1";
    const std::string pt = "s2-golden-plaintext";
    std::vector<std::uint8_t> ct1;
    std::vector<std::uint8_t> ct2;
    ASSERT_TRUE(sshclient::crypto::Aes256GcmEncrypt(
        key, nonce, aad, reinterpret_cast<const std::uint8_t *>(pt.data()), pt.size(), &ct1));
    ASSERT_TRUE(sshclient::crypto::Aes256GcmEncrypt(
        key, nonce, aad, reinterpret_cast<const std::uint8_t *>(pt.data()), pt.size(), &ct2));
    EXPECT_EQ(ct1, ct2);
    SecureClear(key, sizeof(key));
}
