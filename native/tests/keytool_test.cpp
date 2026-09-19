// K01: 原生密钥工具（01-DESIGN.md §6.1 keytool；03-SYNC-PROTOCOL.md §9）。
//
// 离线单测：fixtures/keys 下 11 组私钥（ed25519/RSA/ECDSA × openssh-key-v1/
// 传统 PEM/PKCS#8 × 加密/未加密）的类型/位数/格式/加密判定，指纹与
// `ssh-keygen -lf` 期望值逐字比对（期望值在夹具生成时采集，见本文件 kCases）。
// 生成侧：ed25519（openssh-key-v1 结构校验：checkint 相等、padding 1..k、
// 8 对齐）与 RSA-3072/4096 往返解析；生成的 ed25519 经 libssh2
// publickey_frommemory 加载校验（无服务器离线判定：私钥解析通过即视为可
// 加载——解析失败报 FILE/KEYFILE_AUTH_FAILED，解析成功则死在无连接的传输
// 层，见 TryLoadFromMemory）。
//
// 加密夹具短语均为 "n05-test-passphrase"（与 N05 auth_test 一致）；夹具为
// 测试专用密钥，CRLF 会破坏解析，.gitattributes 已固定 eol=lf。
#include <winsock2.h> // 必须先于 windows.h（经 gtest 间接包含），同 auth_test 规则。

#include <libssh2.h>

#include "crypto/keytool.h"

#include "private_key_vectors.h" // S15：桌面端 ssh2 派生的期望指纹（private-keys.mjs 生成）。

#include <gtest/gtest.h>

#include <openssl/bio.h>
#include <openssl/evp.h>
#include <openssl/pem.h>

#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

namespace keytool = sshclient::crypto::keytool;

namespace {

constexpr const char* kFixturePassphrase = "n05-test-passphrase";

std::string FixturePath(const char* name)
{
    return std::string(SSH_TEST_SOURCE_DIR) + "/fixtures/keys/" + name;
}

bool ReadTextFile(const std::string& path, std::string& out)
{
    FILE* f = std::fopen(path.c_str(), "rb");
    if (f == nullptr) {
        return false;
    }
    char buf[8192];
    out.clear();
    size_t n = 0;
    while ((n = std::fread(buf, 1, sizeof(buf), f)) > 0) {
        out.append(buf, n);
    }
    std::fclose(f);
    return !out.empty();
}

std::string TrimRight(const std::string& s)
{
    size_t e = s.size();
    while (e > 0 && (s[e - 1] == ' ' || s[e - 1] == '\t' || s[e - 1] == '\r' || s[e - 1] == '\n')) {
        --e;
    }
    return s.substr(0, e);
}

// 空白容忍的 base64 解码（测试内结构校验用；非法返回 false）。
bool B64Decode(const std::string& in, std::vector<uint8_t>* out)
{
    static const signed char kVal[256] = {
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1, // 0x00
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1, // 0x10
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,62,-1,-1,-1,63, // 0x20 ("+"=62, "/"=63)
        52,53,54,55,56,57,58,59,60,61,-1,-1,-1,-1,-1,-1, // 0x30
        -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9,10,11,12,13,14, // 0x40
        15,16,17,18,19,20,21,22,23,24,25,-1,-1,-1,-1,-1, // 0x50
        -1,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40, // 0x60
        41,42,43,44,45,46,47,48,49,50,51,-1,-1,-1,-1,-1, // 0x70
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1, // 0x80
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1, // 0x90
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1, // 0xA0
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1, // 0xB0
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1, // 0xC0
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1, // 0xD0
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1, // 0xE0
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1, // 0xF0
    };
    std::string clean;
    for (const char c : in) {
        if (c == ' ' || c == '\t' || c == '\r' || c == '\n') {
            continue;
        }
        clean.push_back(c);
    }
    out->clear();
    if (clean.empty()) {
        return true;
    }
    if (clean.size() % 4 != 0) {
        return false;
    }
    out->reserve((clean.size() / 4) * 3);
    for (size_t i = 0; i < clean.size(); i += 4) {
        int v[4];
        int pad = 0;
        for (int k = 0; k < 4; ++k) {
            const char c = clean[i + k];
            if (c == '=') {
                v[k] = 0;
                ++pad;
            } else {
                v[k] = kVal[static_cast<unsigned char>(c)];
                if (v[k] < 0) {
                    out->clear();
                    return false;
                }
            }
        }
        const uint32_t n = (static_cast<uint32_t>(v[0]) << 18) |
                           (static_cast<uint32_t>(v[1]) << 12) |
                           (static_cast<uint32_t>(v[2]) << 6) | static_cast<uint32_t>(v[3]);
        out->push_back(static_cast<uint8_t>((n >> 16) & 0xFF));
        if (pad < 2) {
            out->push_back(static_cast<uint8_t>((n >> 8) & 0xFF));
        }
        if (pad < 1) {
            out->push_back(static_cast<uint8_t>(n & 0xFF));
        }
    }
    return true;
}

void EnsureLibssh2Init()
{
    static std::once_flag once;
    static int rc = -1;
    std::call_once(once, [] { rc = ::libssh2_init(0); });
    ASSERT_EQ(rc, 0);
}

struct LoadProbe {
    int rc = 0;
    std::string message;
};

// 离线加载探测：publickey 传空强制走“私钥解析 → 派生公钥”分支（见
// userauth.c userauth_publickey_frommemory）。私钥可解析时停在无连接的传输
// 层（BAD_SOCKET/SOCKET_SEND 等）；解析失败时报 FILE/KEYFILE_AUTH_FAILED。
LoadProbe TryLoadFromMemory(const std::string& privateKey, const std::string& passphrase)
{
    LoadProbe probe;
    LIBSSH2_SESSION* session = ::libssh2_session_init_ex(nullptr, nullptr, nullptr, nullptr);
    if (session == nullptr) {
        probe.rc = -999;
        probe.message = "session init failed";
        return probe;
    }
    ::libssh2_session_set_blocking(session, 1);
    const char* phrase = passphrase.empty() ? nullptr : passphrase.c_str();
    probe.rc = ::libssh2_userauth_publickey_frommemory(
        session, "k01-test", 8, nullptr, 0, privateKey.data(), privateKey.size(), phrase);
    char* msg = nullptr;
    int msgLen = 0;
    ::libssh2_session_last_error(session, &msg, &msgLen, 0);
    if (msg != nullptr && msgLen > 0) {
        probe.message.assign(msg, msgLen);
    }
    ::libssh2_session_free(session);
    return probe;
}

// 可加载 = 死在传输层，而非私钥解析层。
bool KeyLoadable(const LoadProbe& probe)
{
    return probe.rc != LIBSSH2_ERROR_FILE &&
           probe.rc != LIBSSH2_ERROR_KEYFILE_AUTH_FAILED;
}

// 生成的 openssh-key-v1 未加密私钥结构校验（PROTOCOL.key）：magic、
// cipher/kdf 为 none、单钥、checkint 相等、keytype 一致、comment 一致、
// padding 为 1..k 且整体 8 对齐、消费完毕。
bool VerifyOpensshUnencrypted(const std::string& pem, const std::string& wantComment,
                              const std::string& wantAlg, std::string* err)
{
    const auto fail = [&](const std::string& m) {
        if (err != nullptr) {
            *err = m;
        }
        return false;
    };
    const size_t begin = pem.find("-----BEGIN OPENSSH PRIVATE KEY-----");
    const size_t end = pem.find("-----END OPENSSH PRIVATE KEY-----");
    if (begin == std::string::npos || end == std::string::npos || end <= begin) {
        return fail("armor missing");
    }
    std::string body = pem.substr(begin + 35, end - begin - 35);
    std::vector<uint8_t> raw;
    if (!B64Decode(body, &raw)) {
        return fail("base64 decode");
    }
    static const char kMagic[] = "openssh-key-v1";
    if (raw.size() < 16 || std::memcmp(raw.data(), kMagic, 15) != 0 || raw[14] != 0) {
        return fail("magic");
    }
    size_t off = 15;
    const auto u32 = [&](uint32_t* v) {
        if (off + 4 > raw.size()) {
            return false;
        }
        *v = (static_cast<uint32_t>(raw[off]) << 24) |
             (static_cast<uint32_t>(raw[off + 1]) << 16) |
             (static_cast<uint32_t>(raw[off + 2]) << 8) | raw[off + 3];
        off += 4;
        return true;
    };
    const auto str = [&](std::string* s) {
        uint32_t l = 0;
        if (!u32(&l) || off + l > raw.size()) {
            return false;
        }
        s->assign(reinterpret_cast<const char*>(raw.data() + off), l);
        off += l;
        return true;
    };
    std::string cipher;
    std::string kdf;
    std::string kdfopts;
    uint32_t nkeys = 0;
    std::string pub;
    std::string priv;
    if (!str(&cipher) || !str(&kdf) || !str(&kdfopts) || !u32(&nkeys) || !str(&pub) ||
        !str(&priv)) {
        return fail("top-level parse");
    }
    if (cipher != "none" || kdf != "none" || nkeys != 1) {
        return fail("cipher/kdf/nkeys");
    }
    if (off != raw.size()) {
        return fail("trailing bytes");
    }
    // 私钥区解析。
    size_t poff = 0;
    const auto pu32 = [&](uint32_t* v) {
        if (poff + 4 > priv.size()) {
            return false;
        }
        *v = (static_cast<uint32_t>(priv[poff]) << 24) |
             (static_cast<uint32_t>(priv[poff + 1]) << 16) |
             (static_cast<uint32_t>(priv[poff + 2]) << 8) | priv[poff + 3];
        poff += 4;
        return true;
    };
    const auto pstr = [&](std::string* s) {
        uint32_t l = 0;
        if (!pu32(&l) || poff + l > priv.size()) {
            return false;
        }
        s->assign(priv.data() + poff, l);
        poff += l;
        return true;
    };
    uint32_t c1 = 0;
    uint32_t c2 = 0;
    std::string ktype;
    if (!pu32(&c1) || !pu32(&c2) || c1 != c2) {
        return fail("checkint mismatch");
    }
    if (!pstr(&ktype) || ktype != wantAlg) {
        return fail("keytype");
    }
    // ed25519 私钥区：pub(1) + priv(1) 后跟 comment。
    std::string f1;
    std::string f2;
    std::string comment;
    if (!pstr(&f1) || !pstr(&f2) || !pstr(&comment)) {
        return fail("private fields");
    }
    if (comment != wantComment) {
        return fail("comment");
    }
    if (priv.size() % 8 != 0) {
        return fail("alignment");
    }
    const size_t padLen = priv.size() - poff;
    if (padLen >= 8) {
        return fail("pad too long");
    }
    for (size_t i = 0; i < padLen; ++i) {
        if (static_cast<uint8_t>(priv[poff + i]) != i + 1) {
            return fail("pad bytes");
        }
    }
    return true;
}

} // namespace

// ================================================================== 指纹/格式

TEST(KeytoolHelperTest, FingerprintAndPublicKeyLine)
{
    // 与 hostkey_test 相同的黄金向量：SHA 实现跨模块一致。
    EXPECT_EQ(keytool::FingerprintSha256OfBlob(reinterpret_cast<const uint8_t*>(""), 0),
              "SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU");
    EXPECT_EQ(keytool::FingerprintSha256OfBlob(reinterpret_cast<const uint8_t*>("abc"), 3),
              "SHA256:ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0");

    const uint8_t blob[] = {'a', 'b', 'c'}; // base64 "YWJj"
    EXPECT_EQ(keytool::BuildPublicKeyLine("ssh-ed25519", blob, 3, "c"), "ssh-ed25519 YWJj c");
    EXPECT_EQ(keytool::BuildPublicKeyLine("ssh-ed25519", blob, 3, ""), "ssh-ed25519 YWJj");
    EXPECT_EQ(keytool::BuildPublicKeyLine("", blob, 3, "c"), "");
}

// ============================================================ 夹具判定与指纹

struct FixtureExpect {
    const char* file;
    const char* keyType;
    int bits;
    const char* format;
    bool encrypted;
    const char* fingerprint; // `ssh-keygen -lf <file>.pub` 输出
    const char* comment;     // Inspect 应返回的 comment（加密/PEM 为空）
};

// K01 验收：所有夹具的类型/位数/是否加密判定正确，指纹等于 ssh-keygen 期望值。
TEST(KeytoolInspectTest, FixturesMatchSshKeygen)
{
    static const FixtureExpect kCases[] = {
        {"ed25519_openssh", "ssh-ed25519", 256, "openssh", false,
         "SHA256:yUjvGHYmmaSO8ZPFpMC28TReVGeR3FkkcVj3rSpFG5o", "n05-test-only"},
        {"ed25519_openssh_enc", "ssh-ed25519", 256, "openssh", true,
         "SHA256:Kt7nAiMYqX3aCxwlk8I6mkpb3+C4NSDAxdGV4xbRuIM", ""},
        {"rsa3072_pem", "ssh-rsa", 3072, "pem", false,
         "SHA256:sbq1ZDO6nyRdE5tVw+RYev/FnGeRurZpfbCIosdvmfU", ""},
        {"rsa3072_pem_enc", "ssh-rsa", 3072, "pem", true,
         "SHA256:lVKB4oa4VrXb6IwrkA2UV7sku1BuL3ILvo1wG6k130s", ""},
        {"ecdsa256_openssh", "ecdsa-sha2-nistp256", 256, "openssh", false,
         "SHA256:UJrZXMGpcZinQmImA1XLqwoMjrDYamoLaGGSwZ96ct4", "n05-test-only"},
        {"ecdsa256_openssh_enc", "ecdsa-sha2-nistp256", 256, "openssh", true,
         "SHA256:gnqPGEyDP3LPm3x+AZttdBMV+LHReZEYAx0wZY2k7Rw", ""},
        {"ecdsa256_pem", "ecdsa-sha2-nistp256", 256, "pem", false,
         "SHA256:g8kFBlsvX4TvmTuj95FpX63QNRP5mTA+wS8RDImJYHM", ""},
        {"rsa3072_openssh", "ssh-rsa", 3072, "openssh", false,
         "SHA256:YMyjFtTO5gbh3EkFKgccRHf1C4fpCfuK/Ef6HU+1EX8", "n05-test-only"},
        {"rsa3072_openssh_enc", "ssh-rsa", 3072, "openssh", true,
         "SHA256:mTHmshYeYDmV3kbctMKgh8c47tj8hEKE5XBh9Exshks", ""},
        {"rsa3072_pkcs8", "ssh-rsa", 3072, "pem", false,
         "SHA256:7B2vS6QZ/8vVkyv4b9VSzgMqrCXmZzMCxEwK0jiubVo", ""},
        {"rsa3072_pkcs8_enc", "ssh-rsa", 3072, "pem", true,
         "SHA256:oq30y+YjN2/MZuWs6yqQpWQz3B7sFd8XMBjRGfgxD1g", ""},
    };
    for (const FixtureExpect& c : kCases) {
        SCOPED_TRACE(c.file);
        std::string priv;
        ASSERT_TRUE(ReadTextFile(FixturePath(c.file), priv)) << "missing fixture";
        keytool::KeyInspectInfo info;
        ASSERT_TRUE(keytool::InspectPrivateKey(priv, c.encrypted ? kFixturePassphrase : "",
                                               &info));
        EXPECT_EQ(info.keyType, c.keyType);
        EXPECT_EQ(info.bits, c.bits);
        EXPECT_EQ(info.format, c.format);
        EXPECT_EQ(info.encrypted, c.encrypted);
        EXPECT_EQ(info.fingerprintSha256, c.fingerprint);
        EXPECT_EQ(info.comment, c.comment);
        ASSERT_FALSE(info.publicKeyBlob.empty());

        // 指纹自洽：由返回的 blob 重算必须一致（§9 wire blob 规则）。
        EXPECT_EQ(keytool::FingerprintSha256OfBlob(info.publicKeyBlob.data(),
                                                   info.publicKeyBlob.size()),
                  c.fingerprint);

        // .pub 比对：未加密 openssh 要求整行一致（含 comment 往返）；
        // 其余只比 type + base64 两段（PEM/加密的 comment 不存于私钥侧）。
        std::string pubLine;
        ASSERT_TRUE(ReadTextFile(std::string(FixturePath(c.file)) + ".pub", pubLine));
        pubLine = TrimRight(pubLine);
        const bool fullLine = !c.encrypted && std::string(c.format) == "openssh";
        if (fullLine) {
            EXPECT_EQ(info.publicKeyOpenSsh, pubLine);
        } else {
            const size_t sp1 = pubLine.find(' ');
            ASSERT_NE(sp1, std::string::npos) << pubLine;
            size_t sp2 = pubLine.find(' ', sp1 + 1);
            const std::string wantPrefix =
                pubLine.substr(0, sp2 == std::string::npos ? sp2 : sp2);
            EXPECT_EQ(info.publicKeyOpenSsh, wantPrefix) << pubLine;
        }
    }
}

// 加密文件无短语：openssh 仍给出完整公钥信息；PEM 给出 Encrypted 部分成功。
TEST(KeytoolInspectTest, EncryptedWithoutPassphraseReportsEncrypted)
{
    // openssh-key-v1 公钥在文件头，无需短语（§9）。
    for (const char* f : {"ed25519_openssh_enc", "ecdsa256_openssh_enc", "rsa3072_openssh_enc"}) {
        SCOPED_TRACE(f);
        std::string priv;
        ASSERT_TRUE(ReadTextFile(FixturePath(f), priv));
        keytool::KeyInspectInfo info;
        ASSERT_TRUE(keytool::InspectPrivateKey(priv, "", &info));
        EXPECT_TRUE(info.encrypted);
        EXPECT_EQ(info.format, "openssh");
        EXPECT_FALSE(info.keyType.empty());
        EXPECT_GT(info.bits, 0);
        EXPECT_FALSE(info.fingerprintSha256.empty());
    }
    // 传统加密 PEM：头可判定类型，位数/公钥需短语。
    {
        std::string priv;
        ASSERT_TRUE(ReadTextFile(FixturePath("rsa3072_pem_enc"), priv));
        keytool::KeyInspectInfo info;
        ASSERT_TRUE(keytool::InspectPrivateKey(priv, "", &info));
        EXPECT_TRUE(info.encrypted);
        EXPECT_EQ(info.format, "pem");
        EXPECT_EQ(info.keyType, "ssh-rsa");
        EXPECT_EQ(info.bits, 0);
        EXPECT_TRUE(info.publicKeyBlob.empty());
        EXPECT_TRUE(info.fingerprintSha256.empty());
    }
    // 加密 PKCS#8：头判定不出类型。
    {
        std::string priv;
        ASSERT_TRUE(ReadTextFile(FixturePath("rsa3072_pkcs8_enc"), priv));
        keytool::KeyInspectInfo info;
        ASSERT_TRUE(keytool::InspectPrivateKey(priv, "", &info));
        EXPECT_TRUE(info.encrypted);
        EXPECT_EQ(info.format, "pem");
        EXPECT_EQ(info.keyType, "");
        EXPECT_EQ(info.bits, 0);
        EXPECT_TRUE(info.publicKeyBlob.empty());
    }
}

// 短语错误：PEM/PKCS#8 失败闭合；openssh-key-v1 不校验短语（libssh2 在认证时判定）。
TEST(KeytoolInspectTest, WrongPassphraseFailsForPem)
{
    std::string encPem;
    ASSERT_TRUE(ReadTextFile(FixturePath("rsa3072_pem_enc"), encPem));
    std::string encPkcs8;
    ASSERT_TRUE(ReadTextFile(FixturePath("rsa3072_pkcs8_enc"), encPkcs8));
    std::string encOssl;
    ASSERT_TRUE(ReadTextFile(FixturePath("ed25519_openssh_enc"), encOssl));
    keytool::KeyInspectInfo info;
    EXPECT_FALSE(keytool::InspectPrivateKey(encPem, "wrong-passphrase", &info));
    EXPECT_FALSE(keytool::InspectPrivateKey(encPkcs8, "wrong-passphrase", &info));
    EXPECT_TRUE(keytool::InspectPrivateKey(encOssl, "wrong-passphrase", &info));
    EXPECT_TRUE(info.encrypted);
    EXPECT_EQ(info.fingerprintSha256, "SHA256:Kt7nAiMYqX3aCxwlk8I6mkpb3+C4NSDAxdGV4xbRuIM");
}

TEST(KeytoolInspectTest, RejectsMalformed)
{
    keytool::KeyInspectInfo info;
    EXPECT_FALSE(keytool::InspectPrivateKey("", "", &info));
    EXPECT_FALSE(keytool::InspectPrivateKey("hello world", "", &info));
    EXPECT_FALSE(keytool::InspectPrivateKey("-----BEGIN RSA PRIVATE KEY-----\nAAAA\n"
                                            "-----END EC PRIVATE KEY-----\n",
                                            "", &info));
    // 截断。
    std::string priv;
    ASSERT_TRUE(ReadTextFile(FixturePath("ed25519_openssh"), priv));
    EXPECT_FALSE(keytool::InspectPrivateKey(priv.substr(0, 100), "", &info));
    // 首个 body 字符 'b'→'c'：magic 被破坏。
    std::string tampered = priv;
    const size_t nl = tampered.find('\n');
    ASSERT_NE(nl, std::string::npos);
    ASSERT_GT(tampered.size(), nl + 1);
    tampered[nl + 1] = (tampered[nl + 1] == 'b' ? 'c' : 'b');
    EXPECT_FALSE(keytool::InspectPrivateKey(tampered, "", &info));
    // 超限（§3.1 PrivateKeyMaxBytes 同值 256 KiB）。
    EXPECT_FALSE(keytool::InspectPrivateKey(std::string(300000, 'A'), "", &info));
}

TEST(KeytoolInspectTest, AcceptsCrlfAndBom)
{
    std::string priv;
    ASSERT_TRUE(ReadTextFile(FixturePath("ed25519_openssh"), priv));
    std::string crlf;
    for (const char c : priv) {
        if (c == '\n') {
            crlf.push_back('\r');
        }
        crlf.push_back(c);
    }
    keytool::KeyInspectInfo a;
    keytool::KeyInspectInfo b;
    ASSERT_TRUE(keytool::InspectPrivateKey(priv, "", &a));
    ASSERT_TRUE(keytool::InspectPrivateKey(crlf, "", &b));
    EXPECT_EQ(a.fingerprintSha256, b.fingerprintSha256);
    EXPECT_EQ(a.publicKeyOpenSsh, b.publicKeyOpenSsh);

    const std::string bom = std::string("\xEF\xBB\xBF") + priv;
    keytool::KeyInspectInfo c;
    ASSERT_TRUE(keytool::InspectPrivateKey(bom, "", &c));
    EXPECT_EQ(a.fingerprintSha256, c.fingerprintSha256);
}

// ================================================================== 生成

TEST(KeytoolGenerateTest, Ed25519RoundTripAndStructure)
{
    EnsureLibssh2Init();
    std::string pem;
    ASSERT_TRUE(keytool::GenerateEd25519("k01-test-comment", &pem));
    const std::string kBegin = "-----BEGIN OPENSSH PRIVATE KEY-----\n";
    const std::string kEnd = "-----END OPENSSH PRIVATE KEY-----\n";
    ASSERT_GE(pem.size(), kBegin.size() + kEnd.size());
    EXPECT_EQ(pem.compare(0, kBegin.size(), kBegin), 0);
    EXPECT_EQ(pem.compare(pem.size() - kEnd.size(), kEnd.size(), kEnd), 0);

    keytool::KeyInspectInfo info;
    ASSERT_TRUE(keytool::InspectPrivateKey(pem, "", &info));
    EXPECT_EQ(info.keyType, "ssh-ed25519");
    EXPECT_EQ(info.bits, 256);
    EXPECT_EQ(info.format, "openssh");
    EXPECT_FALSE(info.encrypted);
    EXPECT_EQ(info.comment, "k01-test-comment");
    EXPECT_FALSE(info.fingerprintSha256.empty());
    EXPECT_EQ(info.publicKeyOpenSsh.substr(info.publicKeyOpenSsh.size() - 17),
              " k01-test-comment");

    // K01 要点：checkint 与 padding 显式校验。
    std::string err;
    EXPECT_TRUE(VerifyOpensshUnencrypted(pem, "k01-test-comment", "ssh-ed25519", &err))
        << err;

    // K01 验收：libssh2 publickey_frommemory 可加载。
    const LoadProbe probe = TryLoadFromMemory(pem, "");
    EXPECT_TRUE(KeyLoadable(probe)) << "rc=" << probe.rc << " msg=" << probe.message;

    // 空 comment 亦可；公钥行无尾空格。
    std::string pem2;
    ASSERT_TRUE(keytool::GenerateEd25519("", &pem2));
    keytool::KeyInspectInfo info2;
    ASSERT_TRUE(keytool::InspectPrivateKey(pem2, "", &info2));
    EXPECT_EQ(info2.comment, "");
    EXPECT_EQ(info2.publicKeyOpenSsh.find(' '),
              info2.publicKeyOpenSsh.find_last_of(' ')); // 仅 type 与 b64 之间一个空格
    // 两次生成随机性不同（checkint + 密钥均随机）。
    EXPECT_NE(pem, pem2);
}

TEST(KeytoolGenerateTest, Rsa3072RoundTrip)
{
    EnsureLibssh2Init();
    std::string pem;
    ASSERT_TRUE(keytool::GenerateRsa(3072, &pem));
    EXPECT_EQ(pem.compare(0, 27, "-----BEGIN PRIVATE KEY-----"), 0);

    keytool::KeyInspectInfo info;
    ASSERT_TRUE(keytool::InspectPrivateKey(pem, "", &info));
    EXPECT_EQ(info.keyType, "ssh-rsa");
    EXPECT_EQ(info.bits, 3072);
    EXPECT_EQ(info.format, "pem");
    EXPECT_FALSE(info.encrypted);
    EXPECT_FALSE(info.fingerprintSha256.empty());

    const LoadProbe probe = TryLoadFromMemory(pem, "");
    EXPECT_TRUE(KeyLoadable(probe)) << "rc=" << probe.rc << " msg=" << probe.message;
}

TEST(KeytoolGenerateTest, Rsa4096RoundTrip)
{
    EnsureLibssh2Init();
    std::string pem;
    ASSERT_TRUE(keytool::GenerateRsa(4096, &pem));
    keytool::KeyInspectInfo info;
    ASSERT_TRUE(keytool::InspectPrivateKey(pem, "", &info));
    EXPECT_EQ(info.keyType, "ssh-rsa");
    EXPECT_EQ(info.bits, 4096);
    EXPECT_EQ(info.format, "pem");
    const LoadProbe probe = TryLoadFromMemory(pem, "");
    EXPECT_TRUE(KeyLoadable(probe)) << "rc=" << probe.rc << " msg=" << probe.message;
}

TEST(KeytoolGenerateTest, RsaRejectsBadBits)
{
    std::string pem;
    EXPECT_FALSE(keytool::GenerateRsa(0, &pem));
    EXPECT_FALSE(keytool::GenerateRsa(1024, &pem));
    EXPECT_FALSE(keytool::GenerateRsa(2048, &pem));
    EXPECT_FALSE(keytool::GenerateRsa(4095, &pem));
    EXPECT_FALSE(keytool::GenerateRsa(-3072, &pem));
    EXPECT_FALSE(keytool::GenerateRsa(3072, nullptr));
    EXPECT_FALSE(keytool::GenerateEd25519("x", nullptr));
    EXPECT_FALSE(keytool::InspectPrivateKey("x", "", nullptr));
}

// ssh-keygen 写不出 ed25519 PKCS#8：运行时用 OpenSSL 造一段，覆盖 PEM 路径的
// ed25519 分支（EvpToInfo）与 libssh2 对该格式的加载。
TEST(KeytoolPemTest, Ed25519Pkcs8RoundTrip)
{
    EnsureLibssh2Init();
    EVP_PKEY_CTX* ctx = EVP_PKEY_CTX_new_id(EVP_PKEY_ED25519, nullptr);
    ASSERT_NE(ctx, nullptr);
    EVP_PKEY* pkey = nullptr;
    ASSERT_EQ(EVP_PKEY_keygen_init(ctx), 1);
    ASSERT_EQ(EVP_PKEY_keygen(ctx, &pkey), 1);
    EVP_PKEY_CTX_free(ctx);
    ASSERT_NE(pkey, nullptr);
    BIO* bio = BIO_new(BIO_s_mem());
    ASSERT_NE(bio, nullptr);
    ASSERT_EQ(PEM_write_bio_PrivateKey(bio, pkey, nullptr, nullptr, 0, nullptr, nullptr), 1);
    EVP_PKEY_free(pkey);
    char* data = nullptr;
    const long len = BIO_get_mem_data(bio, &data);
    ASSERT_GT(len, 0);
    const std::string pem(data, static_cast<size_t>(len));
    BIO_free(bio);
    ASSERT_EQ(pem.compare(0, 27, "-----BEGIN PRIVATE KEY-----"), 0);

    keytool::KeyInspectInfo info;
    ASSERT_TRUE(keytool::InspectPrivateKey(pem, "", &info));
    EXPECT_EQ(info.keyType, "ssh-ed25519");
    EXPECT_EQ(info.bits, 256);
    EXPECT_EQ(info.format, "pem");
    EXPECT_FALSE(info.encrypted);

    const LoadProbe probe = TryLoadFromMemory(pem, "");
    EXPECT_TRUE(KeyLoadable(probe)) << "rc=" << probe.rc << " msg=" << probe.message;
}

// ============================================================ libssh2 加载判定

// K01 验收（单测内校验）：探测器本身先用已知好/坏输入自证，再用于生成密钥。
TEST(KeytoolLibssh2Test, LoadDiscriminatorSelfCheck)
{
    EnsureLibssh2Init();
    std::string good;
    ASSERT_TRUE(ReadTextFile(FixturePath("ed25519_openssh"), good));
    const LoadProbe okProbe = TryLoadFromMemory(good, "");
    EXPECT_TRUE(KeyLoadable(okProbe)) << "rc=" << okProbe.rc << " msg=" << okProbe.message;

    const LoadProbe badProbe = TryLoadFromMemory("not a key", "");
    EXPECT_FALSE(KeyLoadable(badProbe)) << "rc=" << badProbe.rc;
    EXPECT_EQ(badProbe.rc, LIBSSH2_ERROR_FILE);

    // 加密夹具：错短语解不开（本地失败），对短语可过解析关（死在传输层）。
    std::string enc;
    ASSERT_TRUE(ReadTextFile(FixturePath("ed25519_openssh_enc"), enc));
    const LoadProbe wrongProbe = TryLoadFromMemory(enc, "wrong-passphrase");
    EXPECT_FALSE(KeyLoadable(wrongProbe)) << "rc=" << wrongProbe.rc << " msg=" << wrongProbe.message;
    const LoadProbe rightProbe = TryLoadFromMemory(enc, kFixturePassphrase);
    EXPECT_TRUE(KeyLoadable(rightProbe)) << "rc=" << rightProbe.rc << " msg=" << rightProbe.message;
}

// ============================================================ S15 桌面端互通

// S15 验收：ed25519/rsa/ecdsa × openssh/pem × 加密/未加密 的指纹与桌面端一致。
// 期望值由 tools/sync-vectors/private-keys.mjs 经桌面端 node_modules/ssh2 的
// utils.parseKey(...).getPublicSSH() + SHA256 生成（PKCS#8 两类 ssh2 不支持，
// 取自 ssh-keygen .pub 的 wire blob，与 getPublicSSH 同物）；此处断言 native
// 解析的格式/类型/加密标记/指纹与向量逐项一致（正确短语解析）。
TEST(KeytoolInteropTest, FingerprintsMatchDesktopSsh2Vectors)
{
    namespace vectors = sshclient::crypto::privkey_vectors;
    ASSERT_GT(vectors::kEntryCount, static_cast<std::size_t>(0));
    for (std::size_t i = 0; i < vectors::kEntryCount; ++i) {
        const vectors::Entry& e = vectors::kEntries[i];
        SCOPED_TRACE(e.file);
        std::string priv;
        ASSERT_TRUE(ReadTextFile(FixturePath(e.file), priv)) << "missing fixture";
        keytool::KeyInspectInfo info;
        ASSERT_TRUE(keytool::InspectPrivateKey(priv, e.encrypted ? kFixturePassphrase : "",
                                               &info))
            << "native 无法解析";
        EXPECT_STREQ(info.format.c_str(), e.format);
        EXPECT_STREQ(info.keyType.c_str(), e.keyType);
        EXPECT_EQ(info.encrypted, e.encrypted);
        EXPECT_STREQ(info.fingerprintSha256.c_str(), e.fingerprint);
    }
}
