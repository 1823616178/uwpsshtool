// S03: 保险库密码学实现（doc/03-SYNC-PROTOCOL.md §3）。
//
// 与桌面端 src/main/security/crypto-vault.ts 逐字节对齐：
// AAD 编码、SPM1 恢复密钥格式、HKDF-SHA256(salt 空)、AES-256-GCM(tag 拼密文末尾)、
// 规范 Base64、ciphertextHash 先验。Argon2 只用 ref.c（NativeCore.vcxitems 与
// CMake 均只编译 ref.c，ARGON2_NO_THREADS）。
#include "vault.hpp"

#include "openssl/crypto.h"
#include "openssl/evp.h"
#include "openssl/hmac.h"
#include "openssl/rand.h"
#include "openssl/sha.h"
#include "aad.hpp"
#include "argon2.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <cstring>
#include <vector>

namespace sshclient {
namespace crypto {
namespace {

constexpr char kB64Url[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

int B64UrlIndex(char c)
{
    if (c >= 'A' && c <= 'Z') {
        return c - 'A';
    }
    if (c >= 'a' && c <= 'z') {
        return c - 'a' + 26;
    }
    if (c >= '0' && c <= '9') {
        return c - '0' + 52;
    }
    if (c == '-') {
        return 62;
    }
    if (c == '_') {
        return 63;
    }
    return -1;
}

std::string Base64UrlEncode(const std::uint8_t *data, std::size_t len)
{
    std::string out;
    out.reserve((len + 2) / 3 * 4);
    std::size_t i = 0;
    while (i + 3 <= len) {
        const std::uint32_t n = (static_cast<std::uint32_t>(data[i]) << 16) |
                                (static_cast<std::uint32_t>(data[i + 1]) << 8) |
                                static_cast<std::uint32_t>(data[i + 2]);
        out.push_back(kB64Url[(n >> 18) & 63]);
        out.push_back(kB64Url[(n >> 12) & 63]);
        out.push_back(kB64Url[(n >> 6) & 63]);
        out.push_back(kB64Url[n & 63]);
        i += 3;
    }
    if (i < len) {
        std::uint32_t n = static_cast<std::uint32_t>(data[i]) << 16;
        if (i + 1 < len) {
            n |= static_cast<std::uint32_t>(data[i + 1]) << 8;
        }
        out.push_back(kB64Url[(n >> 18) & 63]);
        out.push_back(kB64Url[(n >> 12) & 63]);
        if (i + 1 < len) {
            out.push_back(kB64Url[(n >> 6) & 63]);
        }
    }
    return out;
}

// 43 字符无填充 base64url → 32 字节。长度/字符集由调用方先校验。
bool Base64UrlDecode(std::string_view in, std::uint8_t *out, std::size_t out_len)
{
    if (in.find('=') != std::string_view::npos) {
        return false;
    }
    std::vector<std::uint8_t> acc;
    acc.reserve(out_len);
    std::uint32_t buf = 0;
    int bits = 0;
    for (char c : in) {
        const int v = B64UrlIndex(c);
        if (v < 0) {
            return false;
        }
        buf = (buf << 6) | static_cast<std::uint32_t>(v);
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            acc.push_back(static_cast<std::uint8_t>((buf >> bits) & 0xFF));
        }
    }
    if (acc.size() != out_len) {
        return false;
    }
    std::copy(acc.begin(), acc.end(), out);
    return true;
}

int HexVal(char c)
{
    if (c >= '0' && c <= '9') {
        return c - '0';
    }
    if (c >= 'a' && c <= 'f') {
        return c - 'a' + 10;
    }
    if (c >= 'A' && c <= 'F') {
        return c - 'A' + 10;
    }
    return -1;
}

std::string HexEncode(const std::uint8_t *data, std::size_t len, bool upper)
{
    static const char kLo[] = "0123456789abcdef";
    static const char kHi[] = "0123456789ABCDEF";
    const char *tab = upper ? kHi : kLo;
    std::string out;
    out.resize(len * 2);
    for (std::size_t i = 0; i < len; ++i) {
        out[i * 2] = tab[data[i] >> 4];
        out[i * 2 + 1] = tab[data[i] & 0x0F];
    }
    return out;
}

bool IsB64StdChar(char c)
{
    return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
           c == '+' || c == '/';
}

bool IsB64UrlChar(char c)
{
    return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
           c == '-' || c == '_';
}

bool IsHexChar(char c)
{
    return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}

// HKDF-Expand（RFC 5869）：OKM 前 out_len 字节。salt 空由调用方转为 32 个 0。
bool HkdfExpandSha256(const std::uint8_t *prk, std::size_t prk_len, std::string_view info,
                      std::uint8_t *out, std::size_t out_len)
{
    constexpr std::size_t kHashLen = 32;
    if (prk == nullptr || out == nullptr || out_len == 0) {
        return false;
    }
    const std::size_t blocks = (out_len + kHashLen - 1) / kHashLen;
    if (blocks > 255) {
        return false;
    }
    std::uint8_t previous[kHashLen];
    std::size_t previous_len = 0;
    std::size_t done = 0;
    // info + 1 字节计数器；单块输出时可直接拼接在栈上。
    std::vector<std::uint8_t> msg;
    msg.reserve(info.size() + 1);
    for (std::size_t i = 1; i <= blocks; ++i) {
        msg.clear();
        if (previous_len > 0) {
            msg.insert(msg.end(), previous, previous + previous_len);
        }
        msg.insert(msg.end(), reinterpret_cast<const std::uint8_t *>(info.data()),
                   reinterpret_cast<const std::uint8_t *>(info.data()) + info.size());
        msg.push_back(static_cast<std::uint8_t>(i));
        unsigned int md_len = 0;
        std::uint8_t digest[kHashLen];
        if (::HMAC(::EVP_sha256(), prk, static_cast<int>(prk_len), msg.data(), msg.size(),
                   digest, &md_len) == nullptr ||
            md_len != kHashLen) {
            SecureClear(digest, sizeof(digest));
            SecureClear(previous, sizeof(previous));
            return false;
        }
        const std::size_t take = (std::min)(kHashLen, out_len - done);
        std::memcpy(out + done, digest, take);
        done += take;
        std::memcpy(previous, digest, kHashLen);
        previous_len = kHashLen;
        SecureClear(digest, sizeof(digest));
    }
    SecureClear(previous, sizeof(previous));
    SecureClear(msg.data(), msg.size());
    return done == out_len;
}

} // namespace

bool ValidateKdfParameters(const KdfParameters &params)
{
    if (params.algorithm != kKdfAlgorithm) {
        return false;
    }
    if (params.memoryKib < static_cast<std::uint32_t>(kKdfMemoryKibMin) ||
        params.memoryKib > static_cast<std::uint32_t>(kKdfMemoryKibMax)) {
        return false;
    }
    if (params.iterations < static_cast<std::uint32_t>(kKdfIterationsMin) ||
        params.iterations > static_cast<std::uint32_t>(kKdfIterationsMax)) {
        return false;
    }
    if (params.parallelism < static_cast<std::uint32_t>(kKdfParallelismMin) ||
        params.parallelism > static_cast<std::uint32_t>(kKdfParallelismMax)) {
        return false;
    }
    return true;
}

void SecureClear(void *ptr, std::size_t len)
{
    if (ptr != nullptr && len > 0) {
        OPENSSL_cleanse(ptr, len);
    }
}

bool ConstantTimeEqual(std::string_view a, std::string_view b)
{
    if (a.size() != b.size()) {
        return false;
    }
    if (a.empty()) {
        return true;
    }
    return ::CRYPTO_memcmp(a.data(), b.data(), a.size()) == 0;
}

std::string HexLower(const std::uint8_t *data, std::size_t len)
{
    return HexEncode(data, len, false);
}

std::string HexUpper(const std::uint8_t *data, std::size_t len)
{
    return HexEncode(data, len, true);
}

bool HexDecode(std::string_view hex, std::vector<std::uint8_t> *out)
{
    if (out == nullptr || (hex.size() % 2) != 0) {
        return false;
    }
    out->assign(hex.size() / 2, 0);
    for (std::size_t i = 0; i < out->size(); ++i) {
        const int hi = HexVal(hex[i * 2]);
        const int lo = HexVal(hex[i * 2 + 1]);
        if (hi < 0 || lo < 0) {
            out->clear();
            return false;
        }
        (*out)[i] = static_cast<std::uint8_t>((hi << 4) | lo);
    }
    return true;
}

bool Argon2idHash(std::string_view password, const std::uint8_t *salt, std::size_t salt_len,
                  const KdfParameters &params, std::uint8_t out[kKeyBytes])
{
    if (out == nullptr || salt == nullptr || salt_len == 0 || password.empty()) {
        return false;
    }
    if (!ValidateKdfParameters(params)) {
        return false;
    }
    const int rc = argon2id_hash_raw(params.iterations, params.memoryKib, params.parallelism,
                                     password.data(), password.size(), salt, salt_len, out,
                                     kKeyBytes);
    if (rc != ARGON2_OK) {
        SecureClear(out, kKeyBytes);
        return false;
    }
    return true;
}

bool HkdfSha256(const std::uint8_t *ikm, std::size_t ikm_len, const std::uint8_t *salt,
                std::size_t salt_len, std::string_view info, std::uint8_t *out,
                std::size_t out_len)
{
    if (ikm == nullptr || out == nullptr || out_len == 0) {
        return false;
    }
    // RFC 5869：salt 缺省视为空串，此处按桌面端约定（salt 空）展开为 HashLen 个 0。
    std::uint8_t zero_salt[SHA256_DIGEST_LENGTH];
    const std::uint8_t *extract_salt = salt;
    std::size_t extract_salt_len = salt_len;
    if (extract_salt == nullptr || extract_salt_len == 0) {
        std::memset(zero_salt, 0, sizeof(zero_salt));
        extract_salt = zero_salt;
        extract_salt_len = sizeof(zero_salt);
    }
    std::uint8_t prk[SHA256_DIGEST_LENGTH];
    unsigned int prk_len = 0;
    if (::HMAC(::EVP_sha256(), extract_salt, static_cast<int>(extract_salt_len), ikm, ikm_len,
               prk, &prk_len) == nullptr ||
        prk_len != sizeof(prk)) {
        SecureClear(prk, sizeof(prk));
        SecureClear(zero_salt, sizeof(zero_salt));
        return false;
    }
    const bool ok = HkdfExpandSha256(prk, sizeof(prk), info, out, out_len);
    SecureClear(prk, sizeof(prk));
    SecureClear(zero_salt, sizeof(zero_salt));
    return ok;
}

bool Aes256GcmEncrypt(const std::uint8_t key[kKeyBytes],
                      const std::uint8_t nonce[kNonceBytes], std::string_view aad,
                      const std::uint8_t *plaintext, std::size_t plaintext_len,
                      std::vector<std::uint8_t> *out)
{
    if (key == nullptr || nonce == nullptr || out == nullptr) {
        return false;
    }
    if (plaintext_len > 0 && plaintext == nullptr) {
        return false;
    }
    EVP_CIPHER_CTX *ctx = EVP_CIPHER_CTX_new();
    if (ctx == nullptr) {
        return false;
    }
    bool ok = false;
    out->assign(plaintext_len + kTagBytes, 0);
    int len = 0;
    int total = 0;
    do {
        if (EVP_EncryptInit_ex(ctx, EVP_aes_256_gcm(), nullptr, nullptr, nullptr) != 1) {
            break;
        }
        if (EVP_CIPHER_CTX_ctrl(ctx, EVP_CTRL_GCM_SET_IVLEN, static_cast<int>(kNonceBytes),
                                nullptr) != 1) {
            break;
        }
        if (EVP_EncryptInit_ex(ctx, nullptr, nullptr, key, nonce) != 1) {
            break;
        }
        if (!aad.empty()) {
            if (EVP_EncryptUpdate(ctx, nullptr, &len,
                                  reinterpret_cast<const std::uint8_t *>(aad.data()),
                                  static_cast<int>(aad.size())) != 1) {
                break;
            }
        }
        if (plaintext_len > 0) {
            if (EVP_EncryptUpdate(ctx, out->data(), &len, plaintext,
                                  static_cast<int>(plaintext_len)) != 1) {
                break;
            }
            total = len;
        }
        if (EVP_EncryptFinal_ex(ctx, out->data() + total, &len) != 1) {
            break;
        }
        total += len;
        if (EVP_CIPHER_CTX_ctrl(ctx, EVP_CTRL_GCM_GET_TAG, static_cast<int>(kTagBytes),
                                out->data() + total) != 1) {
            break;
        }
        if (static_cast<std::size_t>(total) != plaintext_len) {
            break;
        }
        ok = true;
    } while (false);
    EVP_CIPHER_CTX_free(ctx);
    if (!ok) {
        out->clear();
    }
    return ok;
}

bool Aes256GcmDecrypt(const std::uint8_t key[kKeyBytes],
                      const std::uint8_t nonce[kNonceBytes], std::string_view aad,
                      const std::uint8_t *ct_and_tag, std::size_t ct_and_tag_len,
                      std::vector<std::uint8_t> *plaintext)
{
    if (key == nullptr || nonce == nullptr || plaintext == nullptr || ct_and_tag == nullptr ||
        ct_and_tag_len < kTagBytes) {
        return false;
    }
    const std::size_t ct_len = ct_and_tag_len - kTagBytes;
    EVP_CIPHER_CTX *ctx = EVP_CIPHER_CTX_new();
    if (ctx == nullptr) {
        return false;
    }
    bool ok = false;
    plaintext->assign(ct_len, 0);
    int len = 0;
    int total = 0;
    do {
        if (EVP_DecryptInit_ex(ctx, EVP_aes_256_gcm(), nullptr, nullptr, nullptr) != 1) {
            break;
        }
        if (EVP_CIPHER_CTX_ctrl(ctx, EVP_CTRL_GCM_SET_IVLEN, static_cast<int>(kNonceBytes),
                                nullptr) != 1) {
            break;
        }
        if (EVP_DecryptInit_ex(ctx, nullptr, nullptr, key, nonce) != 1) {
            break;
        }
        if (!aad.empty()) {
            if (EVP_DecryptUpdate(ctx, nullptr, &len,
                                  reinterpret_cast<const std::uint8_t *>(aad.data()),
                                  static_cast<int>(aad.size())) != 1) {
                break;
            }
        }
        if (ct_len > 0) {
            if (EVP_DecryptUpdate(ctx, plaintext->data(), &len, ct_and_tag,
                                  static_cast<int>(ct_len)) != 1) {
                break;
            }
            total = len;
        }
        if (EVP_CIPHER_CTX_ctrl(ctx, EVP_CTRL_GCM_SET_TAG, static_cast<int>(kTagBytes),
                                const_cast<std::uint8_t *>(ct_and_tag + ct_len)) != 1) {
            break;
        }
        if (EVP_DecryptFinal_ex(ctx, plaintext->data() + total, &len) != 1) {
            break;
        }
        ok = true;
    } while (false);
    EVP_CIPHER_CTX_free(ctx);
    if (!ok) {
        if (!plaintext->empty()) {
            SecureClear(plaintext->data(), plaintext->size());
        }
        plaintext->clear();
    }
    return ok;
}

std::string CiphertextHashHex(const std::uint8_t *ct_and_tag, std::size_t len)
{
    if (ct_and_tag == nullptr || len == 0) {
        return {};
    }
    std::uint8_t digest[SHA256_DIGEST_LENGTH];
    SHA256(ct_and_tag, len, digest);
    std::string hex = HexLower(digest, SHA256_DIGEST_LENGTH);
    SecureClear(digest, sizeof(digest));
    return hex;
}

std::string EncodeRecoveryKey(const std::uint8_t raw[kKeyBytes])
{
    if (raw == nullptr) {
        return {};
    }
    const std::string b64 = Base64UrlEncode(raw, kKeyBytes);
    std::array<std::uint8_t, 4 + kKeyBytes> material{};
    static_assert(4 == sizeof("SPM1") - 1, "recovery prefix length");
    std::memcpy(material.data(), kRecoveryKeyPrefix.data(), kRecoveryKeyPrefix.size());
    std::memcpy(material.data() + kRecoveryKeyPrefix.size(), raw, kKeyBytes);
    std::array<std::uint8_t, SHA256_DIGEST_LENGTH> digest{};
    SHA256(material.data(), material.size(), digest.data());
    const std::string check = HexUpper(digest.data(), kRecoveryKeyCheckHexLen / 2);
    SecureClear(material.data(), material.size());
    SecureClear(digest.data(), digest.size());
    std::string out;
    out.reserve(kRecoveryKeyPrefix.size() + 1 + b64.size() + 1 + kRecoveryKeyCheckHexLen);
    out.append(kRecoveryKeyPrefix);
    out.push_back('-');
    out.append(b64);
    out.push_back('-');
    out.append(check);
    return out;
}

bool DecodeRecoveryKey(std::string_view encoded, std::uint8_t raw[kKeyBytes])
{
    if (raw == nullptr) {
        return false;
    }
    // ^SPM1-([A-Za-z0-9_-]{43})-([A-Fa-f0-9]{12})$（桌面端 decodeRecoveryKey 正则）。
    const std::size_t first = encoded.find('-');
    const std::size_t last = encoded.rfind('-');
    if (first == std::string_view::npos || last == first || last + 1 >= encoded.size()) {
        return false;
    }
    const std::string_view prefix = encoded.substr(0, first);
    const std::string_view b64 = encoded.substr(first + 1, last - first - 1);
    const std::string_view check = encoded.substr(last + 1);
    if (prefix != kRecoveryKeyPrefix || b64.size() != kRecoveryKeyRawB64urlLen ||
        check.size() != kRecoveryKeyCheckHexLen) {
        return false;
    }
    for (char c : b64) {
        if (!IsB64UrlChar(c)) {
            return false;
        }
    }
    for (char c : check) {
        if (!IsHexChar(c)) {
            return false;
        }
    }
    std::uint8_t decoded[kKeyBytes];
    if (!Base64UrlDecode(b64, decoded, kKeyBytes)) {
        return false;
    }
    // 校验段大小写不敏感：双方转大写后常量时间比较（§3.3）。
    const std::string expect = EncodeRecoveryKey(decoded);
    const std::size_t expect_check_pos = expect.rfind('-') + 1;
    std::string expect_check = expect.substr(expect_check_pos);
    std::string actual_check(check);
    for (char &c : actual_check) {
        c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
    }
    const bool match = ConstantTimeEqual(expect_check, actual_check);
    if (match) {
        std::memcpy(raw, decoded, kKeyBytes);
    }
    SecureClear(decoded, sizeof(decoded));
    SecureClear(expect_check.data(), expect_check.size());
    SecureClear(actual_check.data(), actual_check.size());
    return match;
}

bool DeriveRecoveryKek(const std::uint8_t raw[kKeyBytes], std::uint8_t kek[kHkdfOutLen])
{
    if (raw == nullptr || kek == nullptr) {
        return false;
    }
    return HkdfSha256(raw, kKeyBytes, nullptr, 0, kHkdfInfoRecoveryKek, kek, kHkdfOutLen);
}

std::string Base64StdEncode(const std::uint8_t *data, std::size_t len)
{
    if (data == nullptr && len > 0) {
        return {};
    }
    if (len == 0) {
        return {};
    }
    std::string out;
    out.resize(((len + 2) / 3) * 4);
    const int n = EVP_EncodeBlock(reinterpret_cast<unsigned char *>(out.data()), data,
                                  static_cast<int>(len));
    if (n < 0) {
        return {};
    }
    out.resize(static_cast<std::size_t>(n));
    return out;
}

bool Base64StdDecode(std::string_view in, std::vector<std::uint8_t> *out)
{
    if (out == nullptr) {
        return false;
    }
    if (in.empty()) {
        out->clear();
        return true;
    }
    // 桌面端 decodeBase64：^[A-Za-z0-9+/]*={0,2}$ 且长度 % 4 == 0。
    if (in.size() % 4 != 0) {
        return false;
    }
    std::size_t body_len = in.size();
    while (body_len > 0 && in[body_len - 1] == '=') {
        body_len--;
    }
    const std::size_t pad = in.size() - body_len;
    if (pad > 2) {
        return false;
    }
    for (std::size_t i = 0; i < body_len; ++i) {
        if (!IsB64StdChar(in[i])) {
            return false;
        }
    }
    std::vector<unsigned char> buf(in.size());
    const int n = EVP_DecodeBlock(buf.data(), reinterpret_cast<const unsigned char *>(in.data()),
                                  static_cast<int>(in.size()));
    if (n < 0) {
        out->clear();
        return false;
    }
    const std::size_t real = static_cast<std::size_t>(n) - pad;
    out->assign(buf.begin(), buf.begin() + static_cast<std::ptrdiff_t>(real));
    // 规范形式：解码再编码必须等于原文（§3.3/§3.4）。
    const std::string reencoded = Base64StdEncode(out->data(), out->size());
    if (reencoded.size() != in.size() ||
        ::CRYPTO_memcmp(reencoded.data(), in.data(), in.size()) != 0) {
        SecureClear(out->data(), out->size());
        out->clear();
        return false;
    }
    return true;
}

bool RandomBytes(std::uint8_t *out, std::size_t len)
{
    if (out == nullptr || len == 0) {
        return false;
    }
    return RAND_bytes(out, static_cast<int>(len)) == 1;
}

namespace {

bool WrapKey(const std::uint8_t vault_key[kKeyBytes], const std::uint8_t kek[kKeyBytes],
             std::string_view aad, std::string *wrapped_b64, std::string *nonce_b64)
{
    std::uint8_t nonce[kNonceBytes];
    if (!RandomBytes(nonce, kNonceBytes)) {
        return false;
    }
    std::vector<std::uint8_t> ct;
    if (!Aes256GcmEncrypt(kek, nonce, aad, vault_key, kKeyBytes, &ct)) {
        SecureClear(nonce, sizeof(nonce));
        return false;
    }
    *wrapped_b64 = Base64StdEncode(ct.data(), ct.size());
    *nonce_b64 = Base64StdEncode(nonce, kNonceBytes);
    SecureClear(nonce, sizeof(nonce));
    SecureClear(ct.data(), ct.size());
    return !wrapped_b64->empty() && !nonce_b64->empty();
}

bool UnwrapKey(const std::uint8_t kek[kKeyBytes], std::string_view aad,
               std::string_view wrapped_b64, std::string_view nonce_b64,
               std::uint8_t vault_key[kKeyBytes])
{
    std::vector<std::uint8_t> ct;
    std::vector<std::uint8_t> nonce;
    if (!Base64StdDecode(wrapped_b64, &ct) || !Base64StdDecode(nonce_b64, &nonce)) {
        return false;
    }
    if (nonce.size() != kNonceBytes) {
        return false;
    }
    std::vector<std::uint8_t> pt;
    if (!Aes256GcmDecrypt(kek, nonce.data(), aad, ct.data(), ct.size(), &pt)) {
        return false;
    }
    if (pt.size() != kKeyBytes) {
        SecureClear(pt.data(), pt.size());
        return false;
    }
    std::memcpy(vault_key, pt.data(), kKeyBytes);
    SecureClear(pt.data(), pt.size());
    return true;
}

std::string KeyVerStr(int key_version)
{
    return std::to_string(key_version);
}

bool WrapBoth(const std::uint8_t vault_key[kKeyBytes], std::string_view password, int key_version,
              const KdfParameters &kdf, VaultWrap *wrap, std::string *recovery_key)
{
    std::uint8_t salt[kKdfSaltBytes];
    std::uint8_t recovery_raw[kKeyBytes];
    std::uint8_t pw_kek[kKeyBytes];
    std::uint8_t rec_kek[kHkdfOutLen];
    bool ok = false;
    do {
        if (!RandomBytes(salt, kKdfSaltBytes) || !RandomBytes(recovery_raw, kKeyBytes)) {
            break;
        }
        if (!Argon2idHash(password, salt, kKdfSaltBytes, kdf, pw_kek)) {
            break;
        }
        if (!DeriveRecoveryKek(recovery_raw, rec_kek)) {
            break;
        }
        const std::string kv = KeyVerStr(key_version);
        const std::string pw_aad = VaultKeyPasswordAad(kv);
        const std::string rec_aad = VaultKeyRecoveryAad(kv);
        if (!WrapKey(vault_key, pw_kek, pw_aad, &wrap->passwordWrappedKey,
                     &wrap->passwordWrapNonce)) {
            break;
        }
        if (!WrapKey(vault_key, rec_kek, rec_aad, &wrap->recoveryWrappedKey,
                     &wrap->recoveryWrapNonce)) {
            break;
        }
        wrap->kdfSalt = Base64StdEncode(salt, kKdfSaltBytes);
        wrap->keyVersion = key_version;
        wrap->kdfParameters = kdf;
        *recovery_key = EncodeRecoveryKey(recovery_raw);
        ok = !wrap->kdfSalt.empty() && !recovery_key->empty();
    } while (false);
    SecureClear(salt, sizeof(salt));
    SecureClear(recovery_raw, sizeof(recovery_raw));
    SecureClear(pw_kek, sizeof(pw_kek));
    SecureClear(rec_kek, sizeof(rec_kek));
    return ok;
}

} // namespace

bool CreateVault(std::string_view password, int keyVersion, const KdfParameters &kdf,
                 VaultWrap *wrap, std::string *recovery_key, std::uint8_t vaultKey[kKeyBytes])
{
    if (wrap == nullptr || recovery_key == nullptr || vaultKey == nullptr ||
        password.empty() || keyVersion < 1 || !ValidateKdfParameters(kdf)) {
        return false;
    }
    if (!RandomBytes(vaultKey, kKeyBytes)) {
        return false;
    }
    if (!WrapBoth(vaultKey, password, keyVersion, kdf, wrap, recovery_key)) {
        SecureClear(vaultKey, kKeyBytes);
        return false;
    }
    return true;
}

bool CreateVault(std::string_view password, int keyVersion, VaultWrap *wrap,
                 std::string *recovery_key, std::uint8_t vaultKey[kKeyBytes])
{
    return CreateVault(password, keyVersion, DefaultKdfParameters(), wrap, recovery_key,
                       vaultKey);
}

bool UnlockWithPassword(std::string_view password, const VaultWrap &wrap,
                        std::uint8_t vaultKey[kKeyBytes])
{
    if (vaultKey == nullptr || password.empty() || wrap.keyVersion < 1) {
        return false;
    }
    if (!ValidateKdfParameters(wrap.kdfParameters)) {
        return false;
    }
    std::vector<std::uint8_t> salt;
    if (!Base64StdDecode(wrap.kdfSalt, &salt) || salt.size() != kKdfSaltBytes) {
        return false;
    }
    std::uint8_t pw_kek[kKeyBytes];
    if (!Argon2idHash(password, salt.data(), salt.size(), wrap.kdfParameters, pw_kek)) {
        SecureClear(salt.data(), salt.size());
        return false;
    }
    SecureClear(salt.data(), salt.size());
    const std::string aad = VaultKeyPasswordAad(KeyVerStr(wrap.keyVersion));
    const bool ok =
        UnwrapKey(pw_kek, aad, wrap.passwordWrappedKey, wrap.passwordWrapNonce, vaultKey);
    SecureClear(pw_kek, sizeof(pw_kek));
    return ok;
}

bool UnlockWithRecovery(std::string_view recovery_key, const VaultWrap &wrap,
                        std::uint8_t vaultKey[kKeyBytes])
{
    if (vaultKey == nullptr || wrap.keyVersion < 1) {
        return false;
    }
    std::uint8_t raw[kKeyBytes];
    if (!DecodeRecoveryKey(recovery_key, raw)) {
        return false;
    }
    std::uint8_t rec_kek[kHkdfOutLen];
    if (!DeriveRecoveryKek(raw, rec_kek)) {
        SecureClear(raw, sizeof(raw));
        return false;
    }
    SecureClear(raw, sizeof(raw));
    const std::string aad = VaultKeyRecoveryAad(KeyVerStr(wrap.keyVersion));
    const bool ok =
        UnwrapKey(rec_kek, aad, wrap.recoveryWrappedKey, wrap.recoveryWrapNonce, vaultKey);
    SecureClear(rec_kek, sizeof(rec_kek));
    return ok;
}

bool RewrapVault(const std::uint8_t vaultKey[kKeyBytes], std::string_view newPassword,
                 int keyVersion, const KdfParameters &kdf, VaultWrap *wrap,
                 std::string *recoveryKey)
{
    if (vaultKey == nullptr || wrap == nullptr || recoveryKey == nullptr ||
        newPassword.empty() || keyVersion < 1 || !ValidateKdfParameters(kdf)) {
        return false;
    }
    return WrapBoth(vaultKey, newPassword, keyVersion, kdf, wrap, recoveryKey);
}

bool RewrapVault(const std::uint8_t vaultKey[kKeyBytes], std::string_view newPassword,
                 int keyVersion, VaultWrap *wrap, std::string *recoveryKey)
{
    return RewrapVault(vaultKey, newPassword, keyVersion, DefaultKdfParameters(), wrap,
                       recoveryKey);
}

bool EncryptDocument(const std::uint8_t vaultKey[kKeyBytes], std::string_view vaultId,
                     int schemaVersion, int keyVersion, std::string_view plaintext,
                     DocumentSeal *out)
{
    if (vaultKey == nullptr || out == nullptr || vaultId.empty() || schemaVersion < 1 ||
        keyVersion < 1) {
        return false;
    }
    std::uint8_t nonce[kNonceBytes];
    if (!RandomBytes(nonce, kNonceBytes)) {
        return false;
    }
    const std::string aad =
        SyncDocumentAad(vaultId, std::to_string(schemaVersion), KeyVerStr(keyVersion));
    std::vector<std::uint8_t> ct;
    if (!Aes256GcmEncrypt(vaultKey, nonce, aad,
                          reinterpret_cast<const std::uint8_t *>(plaintext.data()),
                          plaintext.size(), &ct)) {
        SecureClear(nonce, sizeof(nonce));
        return false;
    }
    out->schemaVersion = schemaVersion;
    out->keyVersion = keyVersion;
    out->algorithm = std::string(kAesAlgorithm);
    out->nonce = Base64StdEncode(nonce, kNonceBytes);
    out->ciphertext = Base64StdEncode(ct.data(), ct.size());
    out->ciphertextHash = CiphertextHashHex(ct.data(), ct.size());
    SecureClear(nonce, sizeof(nonce));
    SecureClear(ct.data(), ct.size());
    return !out->nonce.empty() && !out->ciphertext.empty() &&
           out->ciphertextHash.size() == kCiphertextHashHexLen;
}

bool DecryptDocument(const std::uint8_t vaultKey[kKeyBytes], std::string_view vaultId,
                     const DocumentSeal &seal, std::string *plaintext)
{
    if (vaultKey == nullptr || plaintext == nullptr || vaultId.empty()) {
        return false;
    }
    // §3.4：algorithm 必须 == "AES-256-GCM"。
    if (seal.algorithm != kAesAlgorithm || seal.keyVersion < 1 || seal.schemaVersion < 1) {
        return false;
    }
    // 规范解码 + 解密前先常量时间比对 ciphertextHash（踩坑 #2）。
    std::vector<std::uint8_t> ct;
    if (!Base64StdDecode(seal.ciphertext, &ct) || ct.size() < kTagBytes) {
        return false;
    }
    const std::string hash = CiphertextHashHex(ct.data(), ct.size());
    if (!ConstantTimeEqual(hash, seal.ciphertextHash)) {
        return false;
    }
    std::vector<std::uint8_t> nonce;
    if (!Base64StdDecode(seal.nonce, &nonce) || nonce.size() != kNonceBytes) {
        return false;
    }
    const std::string aad = SyncDocumentAad(vaultId, std::to_string(seal.schemaVersion),
                                            KeyVerStr(seal.keyVersion));
    std::vector<std::uint8_t> pt;
    if (!Aes256GcmDecrypt(vaultKey, nonce.data(), aad, ct.data(), ct.size(), &pt)) {
        return false;
    }
    plaintext->assign(reinterpret_cast<const char *>(pt.data()), pt.size());
    SecureClear(pt.data(), pt.size());
    return true;
}

} // namespace crypto
} // namespace sshclient
