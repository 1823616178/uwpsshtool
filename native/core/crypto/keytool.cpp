// K01: SSH 私钥生成与解析实现（见 keytool.h）。
//
// openssh-key-v1 私钥布局依据 OpenSSH PROTOCOL.key：
//   MAGIC("openssh-key-v1\0") + string cipher + string kdf + string kdfoptions
//   + u32 nkeys + string[nkeys] pubblob + string private
// 未加密 private 内容：u32 check1 + u32 check2 + string keytype + 类型相关字段
//   + string comment + padding(1,2,3... 至 8 字节对齐，"none" 块为 8)。
#include "keytool.h"

#include <openssl/bn.h>
#include <openssl/core_names.h>
#include <openssl/crypto.h>
#include <openssl/evp.h>
#include <openssl/pem.h>
#include <openssl/rand.h>
#include <openssl/rsa.h> // EVP_RSA_gen（非弃用 RSA 快查生成，默认 e=65537）

#include <cstring>

namespace sshclient {
namespace crypto {
namespace keytool {
namespace {

constexpr char kMagic[] = "openssh-key-v1";
constexpr size_t kMagicLen = 15; // 含结尾 NUL
constexpr size_t kOpensshBlockSize = 8; // "none" cipher 块大小
constexpr size_t kEd25519RawLen = 32;
constexpr size_t kEd25519PrivLen = 64; // seed(32) || pub(32)
constexpr size_t kBase64WrapCols = 70; // 与 ssh-keygen 一致

constexpr const char* kFmtOpenssh = "openssh";
constexpr const char* kFmtPem = "pem";

constexpr const char* kTypeEd25519 = "ssh-ed25519";
constexpr const char* kTypeRsa = "ssh-rsa";
constexpr const char* kTypeDss = "ssh-dss";
constexpr const char* kTypeEcdsa256 = "ecdsa-sha2-nistp256";
constexpr const char* kTypeEcdsa384 = "ecdsa-sha2-nistp384";
constexpr const char* kTypeEcdsa521 = "ecdsa-sha2-nistp521";

// ---------------------------------------------------------------- base64

int B64Index(char c)
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
    if (c == '+') {
        return 62;
    }
    if (c == '/') {
        return 63;
    }
    return -1;
}

std::string Base64Encode(const uint8_t* data, size_t len, bool pad)
{
    static const char kTable[] =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out;
    out.reserve(((len + 2) / 3) * 4);
    size_t i = 0;
    for (; i + 3 <= len; i += 3) {
        const uint32_t v = (static_cast<uint32_t>(data[i]) << 16) |
                           (static_cast<uint32_t>(data[i + 1]) << 8) |
                           static_cast<uint32_t>(data[i + 2]);
        out.push_back(kTable[(v >> 18) & 63]);
        out.push_back(kTable[(v >> 12) & 63]);
        out.push_back(kTable[(v >> 6) & 63]);
        out.push_back(kTable[v & 63]);
    }
    const size_t rem = len - i;
    if (rem == 1) {
        const uint32_t v = static_cast<uint32_t>(data[i]) << 16;
        out.push_back(kTable[(v >> 18) & 63]);
        out.push_back(kTable[(v >> 12) & 63]);
        if (pad) {
            out.push_back('=');
            out.push_back('=');
        }
    } else if (rem == 2) {
        const uint32_t v = (static_cast<uint32_t>(data[i]) << 16) |
                           (static_cast<uint32_t>(data[i + 1]) << 8);
        out.push_back(kTable[(v >> 18) & 63]);
        out.push_back(kTable[(v >> 12) & 63]);
        out.push_back(kTable[(v >> 6) & 63]);
        if (pad) {
            out.push_back('=');
        }
    }
    return out;
}

// 严格解码：输入已去空白，只含 base64 字符与至多两个尾部 '='；失败返回 false。
bool Base64DecodeStrict(const std::string& in, std::vector<uint8_t>* out)
{
    out->clear();
    if (in.empty()) {
        return true;
    }
    if (in.size() % 4 != 0) {
        return false;
    }
    size_t pad = 0;
    if (in.size() >= 1 && in.back() == '=') {
        pad = 1;
        if (in.size() >= 2 && in[in.size() - 2] == '=') {
            pad = 2;
        }
    }
    for (size_t i = 0; i < in.size() - (pad > 0 ? 4 : 0); ++i) {
        if (in[i] == '=' || B64Index(in[i]) < 0) {
            return false;
        }
    }
    for (size_t i = in.size() - (pad > 0 ? 4 : 0); i < in.size(); ++i) {
        const char c = in[i];
        if (c != '=' && B64Index(c) < 0) {
            return false;
        }
    }
    // '=' 只允许出现在最后 pad 位。
    for (size_t i = 0; i + pad < in.size(); ++i) {
        if (in[i] == '=') {
            return false;
        }
    }
    out->reserve((in.size() / 4) * 3);
    for (size_t i = 0; i < in.size(); i += 4) {
        const int a = B64Index(in[i]);
        const int b = B64Index(in[i + 1]);
        const int c = in[i + 2] == '=' ? 0 : B64Index(in[i + 2]);
        const int d = in[i + 3] == '=' ? 0 : B64Index(in[i + 3]);
        if (a < 0 || b < 0 || c < 0 || d < 0) {
            out->clear();
            return false;
        }
        const uint32_t v = (static_cast<uint32_t>(a) << 18) |
                           (static_cast<uint32_t>(b) << 12) |
                           (static_cast<uint32_t>(c) << 6) | static_cast<uint32_t>(d);
        out->push_back(static_cast<uint8_t>((v >> 16) & 0xFF));
        if (in[i + 2] != '=') {
            out->push_back(static_cast<uint8_t>((v >> 8) & 0xFF));
        }
        if (in[i + 3] != '=') {
            out->push_back(static_cast<uint8_t>(v & 0xFF));
        }
    }
    return true;
}

// ---------------------------------------------------------------- wire 编解码

struct Reader {
    const uint8_t* p;
    size_t left;
    bool ok = true;

    bool u32(uint32_t* v)
    {
        if (left < 4) {
            ok = false;
            return false;
        }
        *v = (static_cast<uint32_t>(p[0]) << 24) | (static_cast<uint32_t>(p[1]) << 16) |
             (static_cast<uint32_t>(p[2]) << 8) | static_cast<uint32_t>(p[3]);
        p += 4;
        left -= 4;
        return true;
    }

    // 返回 [data, len) 视图（调用方拷贝），失败 data=nullptr。
    bool str(const uint8_t** data, size_t* len)
    {
        uint32_t l = 0;
        if (!u32(&l) || l > left) {
            ok = false;
            return false;
        }
        *data = p;
        *len = l;
        p += l;
        left -= l;
        return true;
    }

    bool skipStr()
    {
        const uint8_t* d = nullptr;
        size_t l = 0;
        return str(&d, &l);
    }
};

struct Writer {
    std::vector<uint8_t> b;

    void u32(uint32_t v)
    {
        b.push_back(static_cast<uint8_t>((v >> 24) & 0xFF));
        b.push_back(static_cast<uint8_t>((v >> 16) & 0xFF));
        b.push_back(static_cast<uint8_t>((v >> 8) & 0xFF));
        b.push_back(static_cast<uint8_t>(v & 0xFF));
    }

    void bytes(const uint8_t* data, size_t len)
    {
        b.insert(b.end(), data, data + len);
    }

    void str(const uint8_t* data, size_t len)
    {
        u32(static_cast<uint32_t>(len));
        bytes(data, len);
    }

    void str(const std::string& s)
    {
        str(reinterpret_cast<const uint8_t*>(s.data()), s.size());
    }

    void str(const char* s)
    {
        str(reinterpret_cast<const uint8_t*>(s), std::strlen(s));
    }
};

void EncodeMpint(Writer* w, const uint8_t* mag, size_t magLen)
{
    // 去前导零；高位置位则补 0x00（SSH mpint 有符号语义）。
    size_t i = 0;
    while (i + 1 < magLen && mag[i] == 0) {
        ++i;
    }
    mag += i;
    magLen -= i;
    if (magLen == 0) {
        w->u32(0);
        return;
    }
    const bool pad = (mag[0] & 0x80) != 0;
    w->u32(static_cast<uint32_t>(magLen + (pad ? 1 : 0)));
    if (pad) {
        w->b.push_back(0x00);
    }
    w->bytes(mag, magLen);
}

void EncodeBnMpint(Writer* w, const BIGNUM* bn)
{
    if (bn == nullptr) {
        return;
    }
    std::vector<uint8_t> mag(static_cast<size_t>(BN_num_bytes(bn)));
    if (!mag.empty()) {
        BN_bn2bin(bn, mag.data());
    }
    EncodeMpint(w, mag.data(), mag.size());
}

unsigned MpintBits(const uint8_t* mag, size_t magLen)
{
    size_t i = 0;
    while (i + 1 < magLen && mag[i] == 0) {
        ++i;
    }
    mag += i;
    magLen -= i;
    if (magLen == 0) {
        return 0;
    }
    unsigned first = 0;
    for (uint8_t v = mag[0]; v != 0; v >>= 1) {
        ++first;
    }
    return static_cast<unsigned>((magLen - 1) * 8) + first;
}

// ---------------------------------------------------------------- 文本工具

std::string Trim(const std::string& s)
{
    size_t b = 0;
    while (b < s.size() && (s[b] == ' ' || s[b] == '\t' || s[b] == '\r' || s[b] == '\n')) {
        ++b;
    }
    size_t e = s.size();
    while (e > b && (s[e - 1] == ' ' || s[e - 1] == '\t' || s[e - 1] == '\r' ||
                     s[e - 1] == '\n')) {
        --e;
    }
    return s.substr(b, e - b);
}

std::vector<std::string> SplitLines(const std::string& s)
{
    std::vector<std::string> lines;
    size_t b = 0;
    for (size_t i = 0; i <= s.size(); ++i) {
        if (i == s.size() || s[i] == '\n') {
            std::string line = s.substr(b, i - b);
            if (!line.empty() && line.back() == '\r') {
                line.pop_back();
            }
            lines.push_back(line);
            b = i + 1;
        }
    }
    return lines;
}

struct PemBlock {
    std::string label;
    std::vector<std::string> bodyLines; // BEGIN/END 之间的原始行
};

// 找到第一个 "-----BEGIN <label>-----" 与其后第一个匹配的 END；前后纯空白可容忍。
bool SplitPem(const std::string& text, PemBlock* out)
{
    std::string t = text;
    // UTF-8 BOM 容忍（ssh 私钥本不应带 BOM，但编辑器可能加上）。
    if (t.size() >= 3 && static_cast<uint8_t>(t[0]) == 0xEF &&
        static_cast<uint8_t>(t[1]) == 0xBB && static_cast<uint8_t>(t[2]) == 0xBF) {
        t = t.substr(3);
    }
    const std::vector<std::string> lines = SplitLines(t);
    size_t begin = lines.size();
    std::string label;
    for (size_t i = 0; i < lines.size(); ++i) {
        const std::string line = Trim(lines[i]);
        if (line.compare(0, 11, "-----BEGIN ") == 0 &&
            line.size() > 16 && line.compare(line.size() - 5, 5, "-----") == 0) {
            label = line.substr(11, line.size() - 11 - 5);
            begin = i;
            break;
        }
    }
    if (begin == lines.size() || label.empty()) {
        return false;
    }
    const std::string endMark = "-----END " + label + "-----";
    size_t end = lines.size();
    for (size_t i = begin + 1; i < lines.size(); ++i) {
        if (Trim(lines[i]) == endMark) {
            end = i;
            break;
        }
    }
    if (end == lines.size()) {
        return false;
    }
    // BEGIN 之前与 END 之后只允许空白。
    for (size_t i = 0; i < begin; ++i) {
        if (!Trim(lines[i]).empty()) {
            return false;
        }
    }
    for (size_t i = end + 1; i < lines.size(); ++i) {
        if (!Trim(lines[i]).empty()) {
            return false;
        }
    }
    out->label = label;
    out->bodyLines.assign(lines.begin() + begin + 1, lines.begin() + end);
    return true;
}

// PEM body（含传统加密头的 Proc-Type/DEK-Info 行）→ 去空白后 base64 解码。
bool DecodePemBody(const std::vector<std::string>& bodyLines, std::vector<uint8_t>* out,
                   bool* hasEncryptedHeaders)
{
    *hasEncryptedHeaders = false;
    std::string b64;
    for (const std::string& raw : bodyLines) {
        const std::string line = Trim(raw);
        if (line.empty()) {
            continue;
        }
        if (line.find(':') != std::string::npos) {
            // 传统加密 PEM 头（"Proc-Type: 4,ENCRYPTED" / "DEK-Info: ..."）。
            if (line.compare(0, 10, "Proc-Type:") == 0 && line.find("ENCRYPTED") != std::string::npos) {
                *hasEncryptedHeaders = true;
            }
            continue;
        }
        b64 += line;
    }
    return Base64DecodeStrict(b64, out);
}

// ---------------------------------------------------------------- 公钥 blob 解析

// pubBlob → keyType/bits；未知类型返回 false。
bool ParsePublicBlob(const uint8_t* blob, size_t blobLen, std::string* keyType, int* bits)
{
    Reader r{blob, blobLen};
    const uint8_t* alg = nullptr;
    size_t algLen = 0;
    if (!r.str(&alg, &algLen) || algLen == 0 || algLen > 64) {
        return false;
    }
    const std::string algStr(reinterpret_cast<const char*>(alg), algLen);
    if (algStr == kTypeEd25519) {
        const uint8_t* pk = nullptr;
        size_t pkLen = 0;
        if (!r.str(&pk, &pkLen) || pkLen != kEd25519RawLen || r.left != 0) {
            return false;
        }
        *keyType = algStr;
        *bits = kEd25519Bits;
        return true;
    }
    if (algStr == kTypeRsa) {
        const uint8_t* e = nullptr;
        size_t eLen = 0;
        const uint8_t* n = nullptr;
        size_t nLen = 0;
        if (!r.str(&e, &eLen) || !r.str(&n, &nLen) || nLen == 0 || r.left != 0) {
            return false;
        }
        const unsigned nBits = MpintBits(n, nLen);
        if (nBits == 0) {
            return false;
        }
        *keyType = algStr;
        *bits = static_cast<int>(nBits);
        return true;
    }
    if (algStr.compare(0, 11, "ecdsa-sha2-") == 0) {
        const uint8_t* curve = nullptr;
        size_t curveLen = 0;
        const uint8_t* q = nullptr;
        size_t qLen = 0;
        if (!r.str(&curve, &curveLen) || !r.str(&q, &qLen) || r.left != 0) {
            return false;
        }
        const std::string curveStr(reinterpret_cast<const char*>(curve), curveLen);
        if (curveStr != algStr.substr(11)) { // "ecdsa-sha2-nistpXXX" → "nistpXXX"
            return false;
        }
        int ecdsaBits = 0;
        size_t coord = 0;
        if (curveStr == "nistp256") {
            ecdsaBits = 256;
            coord = 32;
        } else if (curveStr == "nistp384") {
            ecdsaBits = 384;
            coord = 48;
        } else if (curveStr == "nistp521") {
            ecdsaBits = 521;
            coord = 66;
        } else {
            return false;
        }
        if (qLen != 1 + 2 * coord || q[0] != 0x04) {
            return false;
        }
        *keyType = algStr;
        *bits = ecdsaBits;
        return true;
    }
    if (algStr == kTypeDss) {
        const uint8_t* f = nullptr;
        size_t fl = 0;
        size_t count = 0;
        const uint8_t* y = nullptr;
        size_t yLen = 0;
        for (int i = 0; i < 4; ++i) { // p, q, g, y
            if (!r.str(&f, &fl) || fl == 0) {
                return false;
            }
            ++count;
            if (i == 3) {
                y = f;
                yLen = fl;
            }
        }
        if (count != 4 || r.left != 0) {
            return false;
        }
        *keyType = algStr;
        *bits = static_cast<int>(MpintBits(y, yLen));
        return true;
    }
    return false;
}

// ---------------------------------------------------------------- openssh-key-v1

int OpensshPrivFieldCount(const std::string& keyType)
{
    if (keyType == kTypeEd25519) {
        return 2; // pk, sk
    }
    if (keyType == kTypeRsa) {
        return 6; // n, e, d, iqmp, p, q
    }
    if (keyType == kTypeEcdsa256 || keyType == kTypeEcdsa384 || keyType == kTypeEcdsa521) {
        return 3; // curve, Q, priv
    }
    if (keyType == kTypeDss) {
        return 5; // p, q, g, y, x
    }
    return -1;
}

bool InspectOpenSsh(const PemBlock& blk, KeyInspectInfo* out)
{
    std::string b64;
    for (const std::string& raw : blk.bodyLines) {
        const std::string line = Trim(raw);
        if (line.empty()) {
            continue;
        }
        if (line.find(':') != std::string::npos) {
            return false; // openssh-key-v1 不应有头行
        }
        b64 += line;
    }
    std::vector<uint8_t> raw;
    if (!Base64DecodeStrict(b64, &raw) || raw.size() < kMagicLen) {
        return false;
    }
    if (std::memcmp(raw.data(), kMagic, kMagicLen) != 0) {
        return false;
    }
    Reader r{raw.data() + kMagicLen, raw.size() - kMagicLen};
    const uint8_t* cipher = nullptr;
    size_t cipherLen = 0;
    const uint8_t* kdf = nullptr;
    size_t kdfLen = 0;
    uint32_t nkeys = 0;
    const uint8_t* pub = nullptr;
    size_t pubLen = 0;
    const uint8_t* priv = nullptr;
    size_t privLen = 0;
    if (!r.str(&cipher, &cipherLen) || !r.str(&kdf, &kdfLen) || !r.skipStr()) {
        return false;
    }
    if (!r.u32(&nkeys) || nkeys != 1) {
        return false;
    }
    if (!r.str(&pub, &pubLen) || pubLen == 0 || !r.str(&priv, &privLen) || privLen == 0) {
        return false;
    }
    if (r.left != 0) {
        return false;
    }
    const std::string cipherStr(reinterpret_cast<const char*>(cipher), cipherLen);
    const std::string kdfStr(reinterpret_cast<const char*>(kdf), kdfLen);
    const bool encrypted = (cipherStr != "none" || kdfStr != "none");

    std::string keyType;
    int bits = 0;
    if (!ParsePublicBlob(pub, pubLen, &keyType, &bits)) {
        return false;
    }

    std::string comment;
    if (!encrypted) {
        Reader pr{priv, privLen};
        uint32_t c1 = 0;
        uint32_t c2 = 0;
        const uint8_t* kt = nullptr;
        size_t ktLen = 0;
        if (!pr.u32(&c1) || !pr.u32(&c2) || c1 != c2) {
            return false; // checkint 不匹配：文件损坏
        }
        if (!pr.str(&kt, &ktLen)) {
            return false;
        }
        const std::string ktStr(reinterpret_cast<const char*>(kt), ktLen);
        if (ktStr != keyType) {
            return false;
        }
        const int fields = OpensshPrivFieldCount(keyType);
        if (fields < 0) {
            return false;
        }
        for (int i = 0; i < fields; ++i) {
            if (!pr.skipStr()) {
                return false;
            }
        }
        const uint8_t* cm = nullptr;
        size_t cmLen = 0;
        if (!pr.str(&cm, &cmLen)) {
            return false;
        }
        comment.assign(reinterpret_cast<const char*>(cm), cmLen);
        // padding：余下字节必须为 1,2,3... 且整体 8 对齐。
        if (privLen % kOpensshBlockSize != 0) {
            return false;
        }
        for (size_t i = 0; i < pr.left; ++i) {
            if (pr.p[i] != static_cast<uint8_t>(i + 1)) {
                return false;
            }
        }
        if (pr.left >= kOpensshBlockSize) {
            return false;
        }
    }

    out->keyType = keyType;
    out->bits = bits;
    out->format = kFmtOpenssh;
    out->encrypted = encrypted;
    out->comment = comment;
    out->publicKeyBlob.assign(pub, pub + pubLen);
    out->fingerprintSha256 = FingerprintSha256OfBlob(pub, pubLen);
    if (out->fingerprintSha256.empty()) {
        return false;
    }
    out->publicKeyOpenSsh = BuildPublicKeyLine(keyType, pub, pubLen, comment);
    return true;
}

// ---------------------------------------------------------------- OpenSSL PEM 解析

int PassphraseCallback(char* buf, int size, int rwflag, void* userdata)
{
    (void)rwflag;
    const std::string* phrase = static_cast<const std::string*>(userdata);
    if (phrase == nullptr || phrase->empty()) {
        return 0;
    }
    if (phrase->size() > static_cast<size_t>(size)) {
        return 0; // 装不下：失败闭合
    }
    std::memcpy(buf, phrase->data(), phrase->size());
    return static_cast<int>(phrase->size());
}

bool BnToBytes(const BIGNUM* bn, std::vector<uint8_t>* out)
{
    out->clear();
    if (bn == nullptr) {
        return false;
    }
    const int n = BN_num_bytes(bn);
    if (n <= 0) {
        return false;
    }
    out->resize(static_cast<size_t>(n));
    if (BN_bn2bin(bn, out->data()) != n) {
        out->clear();
        return false;
    }
    return true;
}

// EVP_PKEY → keyType/bits/pubBlob；失败 false。
bool EvpToInfo(EVP_PKEY* pkey, KeyInspectInfo* out)
{
    if (pkey == nullptr) {
        return false;
    }
    const int id = EVP_PKEY_id(pkey);
    Writer w;
    if (id == EVP_PKEY_ED25519) {
        uint8_t pub[kEd25519RawLen];
        size_t pubLen = sizeof(pub);
        if (EVP_PKEY_get_raw_public_key(pkey, pub, &pubLen) != 1 || pubLen != kEd25519RawLen) {
            return false;
        }
        w.str(kTypeEd25519);
        w.str(pub, sizeof(pub));
        OPENSSL_cleanse(pub, sizeof(pub));
        out->keyType = kTypeEd25519;
        out->bits = kEd25519Bits;
    } else if (id == EVP_PKEY_RSA) {
        const int bits = EVP_PKEY_get_bits(pkey);
        if (bits <= 0) {
            return false;
        }
        BIGNUM* e = nullptr;
        BIGNUM* n = nullptr;
        bool ok = EVP_PKEY_get_bn_param(pkey, OSSL_PKEY_PARAM_RSA_E, &e) == 1 &&
                  EVP_PKEY_get_bn_param(pkey, OSSL_PKEY_PARAM_RSA_N, &n) == 1;
        if (ok) {
            w.str(kTypeRsa);
            EncodeBnMpint(&w, e);
            EncodeBnMpint(&w, n);
        }
        BN_free(e);
        BN_free(n);
        if (!ok || w.b.empty()) {
            return false;
        }
        out->keyType = kTypeRsa;
        out->bits = bits;
    } else if (id == EVP_PKEY_EC) {
        char group[64] = {0};
        size_t groupLen = 0;
        if (EVP_PKEY_get_utf8_string_param(pkey, OSSL_PKEY_PARAM_GROUP_NAME, group,
                                           sizeof(group), &groupLen) != 1) {
            return false;
        }
        const std::string g(group, groupLen);
        std::string alg;
        std::string nistp;
        int bits = 0;
        size_t coord = 0;
        if (g == "prime256v1" || g == "P-256" || g == "nistp256") {
            alg = kTypeEcdsa256;
            nistp = "nistp256";
            bits = 256;
            coord = 32;
        } else if (g == "secp384r1" || g == "P-384" || g == "nistp384") {
            alg = kTypeEcdsa384;
            nistp = "nistp384";
            bits = 384;
            coord = 48;
        } else if (g == "secp521r1" || g == "P-521" || g == "nistp521") {
            alg = kTypeEcdsa521;
            nistp = "nistp521";
            bits = 521;
            coord = 66;
        } else {
            return false; // 非主流曲线：拒绝（服务器通常也不接受）
        }
        uint8_t q[133] = {0};
        size_t qLen = 0;
        if (EVP_PKEY_get_octet_string_param(pkey, OSSL_PKEY_PARAM_PUB_KEY, q, sizeof(q),
                                            &qLen) != 1 ||
            qLen != 1 + 2 * coord || q[0] != 0x04) {
            OPENSSL_cleanse(q, sizeof(q));
            return false;
        }
        w.str(alg);
        w.str(nistp);
        w.str(q, qLen);
        OPENSSL_cleanse(q, sizeof(q));
        out->keyType = alg;
        out->bits = bits;
    } else if (id == EVP_PKEY_DSA) {
        const int bits = EVP_PKEY_get_bits(pkey);
        if (bits <= 0) {
            return false;
        }
        BIGNUM* p = nullptr;
        BIGNUM* q = nullptr;
        BIGNUM* g = nullptr;
        BIGNUM* y = nullptr;
        bool ok = EVP_PKEY_get_bn_param(pkey, OSSL_PKEY_PARAM_FFC_P, &p) == 1 &&
                  EVP_PKEY_get_bn_param(pkey, OSSL_PKEY_PARAM_FFC_Q, &q) == 1 &&
                  EVP_PKEY_get_bn_param(pkey, OSSL_PKEY_PARAM_FFC_G, &g) == 1 &&
                  EVP_PKEY_get_bn_param(pkey, OSSL_PKEY_PARAM_PUB_KEY, &y) == 1;
        if (ok) {
            w.str(kTypeDss);
            EncodeBnMpint(&w, p);
            EncodeBnMpint(&w, q);
            EncodeBnMpint(&w, g);
            EncodeBnMpint(&w, y);
        }
        BN_free(p);
        BN_free(q);
        BN_free(g);
        BN_free(y);
        if (!ok || w.b.empty()) {
            return false;
        }
        out->keyType = kTypeDss;
        out->bits = bits;
    } else {
        return false;
    }
    out->publicKeyBlob = w.b;
    out->fingerprintSha256 = FingerprintSha256OfBlob(w.b.data(), w.b.size());
    if (out->fingerprintSha256.empty()) {
        return false;
    }
    out->publicKeyOpenSsh = BuildPublicKeyLine(out->keyType, w.b.data(), w.b.size(), "");
    return true;
}

// PEM 头直接可判的类型（加密且无短语时的部分成功用）。
std::string KeyTypeFromPemLabel(const std::string& label)
{
    if (label == "RSA PRIVATE KEY") {
        return kTypeRsa;
    }
    if (label == "DSA PRIVATE KEY") {
        return kTypeDss;
    }
    return "";
}

bool InspectPem(const PemBlock& blk, const std::string& passphrase, KeyInspectInfo* out)
{
    const std::string& label = blk.label;
    if (label != "RSA PRIVATE KEY" && label != "EC PRIVATE KEY" && label != "DSA PRIVATE KEY" &&
        label != "PRIVATE KEY" && label != "ENCRYPTED PRIVATE KEY") {
        return false;
    }
    std::vector<uint8_t> der;
    bool hasEncHeaders = false;
    if (!DecodePemBody(blk.bodyLines, &der, &hasEncHeaders) || der.empty()) {
        return false;
    }
    const bool encrypted = hasEncHeaders || label == "ENCRYPTED PRIVATE KEY";

    // 全文重组为 OpenSSL 可读的 PEM（含头行，传统加密头一并保留供 PEM_do_header）。
    // 空行必须原样保留：传统加密 PEM 的头与体之间要有空行分隔，否则 OpenSSL
    // 不认 Proc-Type/DEK-Info 头（当成未加密 DER 解析而失败）。
    std::string pem;
    pem += "-----BEGIN " + label + "-----\n";
    for (const std::string& raw : blk.bodyLines) {
        pem += Trim(raw) + "\n";
    }
    pem += "-----END " + label + "-----\n";
    OPENSSL_cleanse(der.data(), der.size());

    BIO* bio = BIO_new_mem_buf(pem.data(), static_cast<int>(pem.size()));
    if (bio == nullptr) {
        return false;
    }
    EVP_PKEY* pkey = PEM_read_bio_PrivateKey(bio, nullptr, PassphraseCallback,
                                             const_cast<std::string*>(&passphrase));
    BIO_free(bio);
    if (pkey == nullptr) {
        if (!encrypted) {
            return false; // 未加密却解不开：损坏或垃圾
        }
        if (!passphrase.empty()) {
            return false; // 短语错误（或文件损坏）：失败闭合
        }
        // 加密且无短语：部分成功，上层据此提示输入短语。
        out->format = kFmtPem;
        out->encrypted = true;
        out->keyType = KeyTypeFromPemLabel(label);
        out->bits = 0;
        return true;
    }
    out->format = kFmtPem;
    out->encrypted = encrypted;
    const bool ok = EvpToInfo(pkey, out);
    EVP_PKEY_free(pkey);
    if (!ok) {
        *out = KeyInspectInfo();
        return false;
    }
    return true;
}

} // namespace

// ---------------------------------------------------------------- 公开 API

std::string FingerprintSha256OfBlob(const uint8_t* data, size_t len)
{
    uint8_t digest[32] = {0};
    unsigned int mdLen = 0;
    if (data == nullptr && len != 0) {
        return "";
    }
    if (EVP_Digest(data, len, digest, &mdLen, EVP_sha256(), nullptr) != 1 || mdLen != 32) {
        OPENSSL_cleanse(digest, sizeof(digest));
        return "";
    }
    std::string out = "SHA256:" + Base64Encode(digest, sizeof(digest), false);
    OPENSSL_cleanse(digest, sizeof(digest));
    return out;
}

std::string BuildPublicKeyLine(const std::string& keyType, const uint8_t* blob, size_t len,
                               const std::string& comment)
{
    if (keyType.empty() || (blob == nullptr && len != 0)) {
        return "";
    }
    std::string out = keyType + " " + Base64Encode(blob, len, true);
    if (!comment.empty()) {
        out += " " + comment;
    }
    return out;
}

bool InspectPrivateKey(const std::string& privateKeyText, const std::string& passphrase,
                       KeyInspectInfo* out)
{
    if (out == nullptr) {
        return false;
    }
    *out = KeyInspectInfo();
    if (privateKeyText.empty() || privateKeyText.size() > 262144) {
        return false;
    }
    PemBlock blk;
    if (!SplitPem(privateKeyText, &blk)) {
        return false;
    }
    if (blk.label == "OPENSSH PRIVATE KEY") {
        return InspectOpenSsh(blk, out);
    }
    return InspectPem(blk, passphrase, out);
}

bool GenerateEd25519(const std::string& comment, std::string* outPrivateKey)
{
    if (outPrivateKey == nullptr) {
        return false;
    }
    outPrivateKey->clear();
    if (comment.size() > 1024) {
        return false;
    }
    EVP_PKEY_CTX* ctx = EVP_PKEY_CTX_new_id(EVP_PKEY_ED25519, nullptr);
    if (ctx == nullptr) {
        return false;
    }
    EVP_PKEY* pkey = nullptr;
    bool ok = EVP_PKEY_keygen_init(ctx) == 1 && EVP_PKEY_keygen(ctx, &pkey) == 1;
    EVP_PKEY_CTX_free(ctx);
    if (!ok || pkey == nullptr) {
        EVP_PKEY_free(pkey);
        return false;
    }
    uint8_t pub[kEd25519RawLen] = {0};
    uint8_t seed[kEd25519RawLen] = {0};
    size_t pubLen = sizeof(pub);
    size_t seedLen = sizeof(seed);
    ok = EVP_PKEY_get_raw_public_key(pkey, pub, &pubLen) == 1 && pubLen == sizeof(pub) &&
         EVP_PKEY_get_raw_private_key(pkey, seed, &seedLen) == 1 && seedLen == sizeof(seed);
    EVP_PKEY_free(pkey);
    if (!ok) {
        OPENSSL_cleanse(pub, sizeof(pub));
        OPENSSL_cleanse(seed, sizeof(seed));
        return false;
    }
    uint8_t check[4] = {0};
    if (RAND_bytes(check, sizeof(check)) != 1) {
        OPENSSL_cleanse(pub, sizeof(pub));
        OPENSSL_cleanse(seed, sizeof(seed));
        return false;
    }

    // 公钥 blob：string "ssh-ed25519" + string pub。
    Writer pubW;
    pubW.str(kTypeEd25519);
    pubW.str(pub, sizeof(pub));

    // 私钥区：check/check/keytype/pub/priv64/comment/padding。
    Writer privW;
    privW.bytes(check, sizeof(check));
    privW.bytes(check, sizeof(check));
    privW.str(kTypeEd25519);
    privW.str(pub, sizeof(pub));
    uint8_t sk[kEd25519PrivLen] = {0};
    std::memcpy(sk, seed, sizeof(seed));
    std::memcpy(sk + sizeof(seed), pub, sizeof(pub));
    privW.str(sk, sizeof(sk));
    OPENSSL_cleanse(sk, sizeof(sk));
    privW.str(reinterpret_cast<const uint8_t*>(comment.data()), comment.size());
    const size_t pad = (kOpensshBlockSize - (privW.b.size() % kOpensshBlockSize)) % kOpensshBlockSize;
    for (size_t i = 0; i < pad; ++i) {
        privW.b.push_back(static_cast<uint8_t>(i + 1));
    }

    Writer fileW;
    fileW.bytes(reinterpret_cast<const uint8_t*>(kMagic), kMagicLen);
    fileW.str("none");
    fileW.str("none");
    fileW.str(""); // kdfoptions 空
    fileW.u32(1);
    fileW.str(pubW.b.data(), pubW.b.size());
    fileW.str(privW.b.data(), privW.b.size());

    const std::string b64 = Base64Encode(fileW.b.data(), fileW.b.size(), true);
    std::string pem = "-----BEGIN OPENSSH PRIVATE KEY-----\n";
    for (size_t i = 0; i < b64.size(); i += kBase64WrapCols) {
        pem += b64.substr(i, kBase64WrapCols) + "\n";
    }
    pem += "-----END OPENSSH PRIVATE KEY-----\n";

    OPENSSL_cleanse(pub, sizeof(pub));
    OPENSSL_cleanse(seed, sizeof(seed));
    OPENSSL_cleanse(check, sizeof(check));
    *outPrivateKey = pem;
    return true;
}

bool GenerateRsa(int bits, std::string* outPrivateKey)
{
    if (outPrivateKey == nullptr) {
        return false;
    }
    outPrivateKey->clear();
    if (bits != kRsaBits3072 && bits != kRsaBits4096) {
        return false;
    }
    // EVP_RSA_gen：e=65537 默认；失败返回 nullptr（熵不足等），无弃用 API。
    EVP_PKEY* pkey = EVP_RSA_gen(bits);
    if (pkey == nullptr) {
        return false;
    }
    // PKCS#8 非加密 PEM（"-----BEGIN PRIVATE KEY-----"），无弃用 API。
    BIO* bio = BIO_new(BIO_s_mem());
    if (bio == nullptr) {
        EVP_PKEY_free(pkey);
        return false;
    }
    const bool wok =
        PEM_write_bio_PrivateKey(bio, pkey, nullptr, nullptr, 0, nullptr, nullptr) == 1;
    EVP_PKEY_free(pkey);
    if (!wok) {
        BIO_free(bio);
        return false;
    }
    char* data = nullptr;
    const long len = BIO_get_mem_data(bio, &data);
    if (len <= 0 || data == nullptr) {
        BIO_free(bio);
        return false;
    }
    outPrivateKey->assign(data, static_cast<size_t>(len));
    BIO_free(bio);
    return true;
}

} // namespace keytool
} // namespace crypto
} // namespace sshclient
