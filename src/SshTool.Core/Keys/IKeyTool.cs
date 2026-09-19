using System.Threading.Tasks;

namespace SshTool.Core.Keys
{
    // K02：原生 KeyTool 的 Core 侧抽象（01-DESIGN.md §4.1：Core 不引用 Native，
    // 由 App 层注入 NativeKeyTool 实现；单测注入 FakeKeyTool）。
    //
    // 契约（对齐 native Bridge.KeyTool，见 §6.2 与 native/core/crypto/keytool.h）：
    //   - 任何失败（非法输入、短语错误、OpenSSL 失败）一律返回 null，
    //     不抛异常、不区分失败原因。
    //   - 私钥以 UTF-8 文本传递；短语无则传空串（不传 null）。
    //   - 实现与调用方均不得记录私钥、短语、公钥内容（日志脱敏）。
    public sealed class InspectedKeyInfo
    {
        public string KeyType { get; set; }           // ssh-ed25519 / ssh-rsa / ecdsa-sha2-nistp256 …；加密 PEM 无短语且头判定不出时为空
        public int Bits { get; set; }                 // ed25519 256；RSA 模数位数；未知时 0
        public string Format { get; set; }            // openssh / pem
        public bool Encrypted { get; set; }           // 文件属性：解密后仍报告 true
        public string PublicKeyOpenSsh { get; set; }  // "ssh-xxx AAAA… comment"；无公钥信息时为空
        public string FingerprintSha256 { get; set; } // "SHA256:…"；无时为空
        public string Comment { get; set; }           // openssh-key-v1 私钥区注释；PEM/加密时为空

        public InspectedKeyInfo Clone()
        {
            return new InspectedKeyInfo
            {
                KeyType = KeyType,
                Bits = Bits,
                Format = Format,
                Encrypted = Encrypted,
                PublicKeyOpenSsh = PublicKeyOpenSsh,
                FingerprintSha256 = FingerprintSha256,
                Comment = Comment
            };
        }
    }

    public interface IKeyTool
    {
        // 解析私钥全文；失败返回 null；加密 PEM 无短语时可返回 Encrypted=true 的
        // 部分信息（KeyType 可能为空、无公钥/指纹）。
        Task<InspectedKeyInfo> InspectAsync(string privateKeyText, string passphrase);

        // 生成 ed25519（openssh-key-v1 未加密）；失败返回 null。
        Task<string> GenerateEd25519Async(string comment);

        // 生成 RSA（PKCS#8 PEM）；bits 仅接受 3072/4096；失败返回 null。
        Task<string> GenerateRsaAsync(int bits, string comment);
    }
}
