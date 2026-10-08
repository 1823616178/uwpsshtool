using System.Collections.Generic;
using System.Threading.Tasks;

namespace SshTool.Core.Sessions
{
    public sealed class PasswordPromptResult
    {
        public string Password { get; set; }
        public bool Remember { get; set; }
        public bool Cancelled { get; set; }
    }

    // fix/functional-pass：私钥短语对话框结果。Remember = 认证成功后存入 DPAPI 凭据表。
    public sealed class PassphrasePromptResult
    {
        public string Passphrase { get; set; }
        public bool Remember { get; set; }
        public bool Cancelled { get; set; }
    }

    public interface ICredentialPrompter
    {
        // fix/functional-pass：retriesLeft < 0 = 首次询问；>= 0 = 上次密码错误、还可再试几次。
        // 文案由 App 侧本地化（Core 不产出界面中文）。
        Task<PasswordPromptResult> PromptPasswordAsync(string hostDisplay, int retriesLeft);
        // previousWrong = 上次短语错误（对话框显示错误提示）。
        Task<PassphrasePromptResult> PromptPassphraseAsync(string keyName, bool previousWrong);
        Task<IReadOnlyList<string>> PromptKeyboardInteractiveAsync(AuthPromptEventArgs args);
        // K03：agent 无可用密钥时提示选择密钥解锁。choices 为全部本机密钥
        // （Unlocked 标出已解锁的可直接试）；返回 null 或 Cancelled = 无选择。
        Task<AgentUnlockResult> PromptAgentUnlockAsync(
            IReadOnlyList<AgentKeyChoice> choices, string hostDisplay);
    }

    // K03：agent 解锁对话框的一行（仅元数据，无私钥材料）。
    public sealed class AgentKeyChoice
    {
        public string KeyId { get; set; }
        public string Name { get; set; }
        public string KeyType { get; set; }
        public string FingerprintSha256 { get; set; }
        public bool Unlocked { get; set; }
    }

    public sealed class AgentUnlockResult
    {
        public string KeyId { get; set; }
        public bool Cancelled { get; set; }
    }
}
