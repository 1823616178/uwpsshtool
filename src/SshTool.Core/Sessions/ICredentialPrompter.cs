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
    }
}
