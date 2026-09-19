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

    public interface ICredentialPrompter
    {
        Task<PasswordPromptResult> PromptPasswordAsync(string hostDisplay, string errorMessage);
        Task<string> PromptPassphraseAsync(string keyName);
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
