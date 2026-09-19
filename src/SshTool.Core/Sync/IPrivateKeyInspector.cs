using System.Threading.Tasks;
using SshTool.Core.Sync.Protocol;

namespace SshTool.Core.Sync
{
    // S10 定义、S15 异步化并移至本文件（原在 SyncLocalAdapter.cs 内）。
    // 保持兼容：命名空间与类型名（IPrivateKeyInspector、InspectedPrivateKey、
    // NullPrivateKeyInspector.Instance）不变，SyncLocalAdapter 的构造注入
    //（IPrivateKeyInspector keyInspector = null，缺省 Null）原样可用；
    // 仅两个方法由同步改为 Task 异步，以便经由 IKeyTool（原生 WinRT 异步，
    // 见 Keys.IKeyTool）解析私钥与计算指纹。仓库内无其他实现者（grep 可证）。
    public sealed class InspectedPrivateKey
    {
        public string Base64 { get; set; }
        public string Format { get; set; }      // openssh / pem（PrivateKeyFormat 常量）
        public string Fingerprint { get; set; } // SHA256:<43 位标准 base64 无填充>
    }

    public interface IPrivateKeyInspector
    {
        // 出站：私钥原文（UTF-8 文本）+ 短语（可为 null）→ 可同步元数据；
        // 返回 null 表示无法解析或不宜同步，调用方跳过该主机私钥段（§9）。
        Task<InspectedPrivateKey> InspectAsync(string privateKeyText, string passphrase);

        // 入站：校验远端 secrets 私钥段（含 §4.1 结构之外的指纹复算）；
        // 失败抛 SyncApplyException（整份不应用）；返回私钥原文；无私钥段返回 null。
        Task<string> DecodeAndVerifyAsync(ServerSecrets secrets, string serverId);
    }

    public sealed class NullPrivateKeyInspector : IPrivateKeyInspector
    {
        public static readonly NullPrivateKeyInspector Instance = new NullPrivateKeyInspector();

        private NullPrivateKeyInspector()
        {
        }

        public Task<InspectedPrivateKey> InspectAsync(string privateKeyText, string passphrase)
        {
            return Task.FromResult<InspectedPrivateKey>(null);
        }

        public Task<string> DecodeAndVerifyAsync(ServerSecrets secrets, string serverId)
        {
            return Task.FromResult<string>(null);
        }
    }
}
