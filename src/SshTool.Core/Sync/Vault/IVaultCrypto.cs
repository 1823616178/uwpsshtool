using System.Threading.Tasks;

namespace SshTool.Core.Sync.Vault
{
    // S05：保险库密码学抽象（01-DESIGN.md §4.1：Core 不引用 Native，
    // 由 App 层注入 NativeVaultCrypto 实现；单测注入 FakeVaultCrypto）。
    //
    // 契约（对齐 native Bridge.VaultCrypto，见 §6.2）：
    //   - 所有方法必须在后台线程执行，禁止占用 UI 线程（Argon2id 默认参数
    //     在 Lumia 上约数秒，见 D13）。
    //   - 任何失败（密码错误、恢复密钥无效、密文篡改、AAD 不匹配、参数非法）
    //     一律返回 null，不抛异常、不区分失败原因（与 S03 native 语义一致）。
    //   - vaultKey 以标准 Base64（32 字节）字符串传递；明文以 UTF-8 字节传递。
    //   - 实现与调用方均不得记录密码、恢复密钥、vaultKey、明文与密文
    //     （日志脱敏，doc/README.md 硬性约束）。
    public interface IVaultCrypto
    {
        // 创建保险库：随机 vaultKey + 密码/恢复密钥双包裹。失败返回 null。
        Task<VaultSetupResult> CreateAsync(string syncPassword, int keyVersion);

        // 密码解包：返回 vaultKeyBase64；密码错误等失败返回 null。
        Task<string> UnwrapWithPasswordAsync(VaultKeyEnvelope envelope, string syncPassword);

        // 恢复密钥解包：返回 vaultKeyBase64；无效恢复密钥等失败返回 null。
        Task<string> UnwrapWithRecoveryKeyAsync(VaultKeyEnvelope envelope, string recoveryKey);

        // 文档加密：失败返回 null。
        Task<EncryptedDocumentEnvelope> EncryptDocumentAsync(
            string vaultKeyBase64, string vaultId, int schemaVersion, int keyVersion, byte[] plaintextUtf8);

        // 文档解密：失败返回 null。
        Task<byte[]> DecryptDocumentAsync(
            string vaultKeyBase64, string vaultId, EncryptedDocumentEnvelope envelope);
    }

    // CreateAsync 的结果（镜像 native Bridge.VaultSetupResult）。
    public sealed class VaultSetupResult
    {
        public VaultSetupResult(VaultKeyEnvelope envelope, string recoveryKey, string vaultKeyBase64)
        {
            Envelope = envelope;
            RecoveryKey = recoveryKey;
            VaultKeyBase64 = vaultKeyBase64;
        }

        public VaultKeyEnvelope Envelope { get; private set; }
        public string RecoveryKey { get; private set; }
        public string VaultKeyBase64 { get; private set; }
    }
}
