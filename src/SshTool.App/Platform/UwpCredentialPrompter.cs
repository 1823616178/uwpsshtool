using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.App.Dialogs;
using SshTool.Core.Sessions;

namespace SshTool.App.Platform
{
    public sealed class UwpCredentialPrompter : ICredentialPrompter
    {
        public async Task<PasswordPromptResult> PromptPasswordAsync(string hostDisplay, string errorMessage)
        {
            CredentialDialogResult r = await CredentialDialog.ShowAsync(hostDisplay, errorMessage).ConfigureAwait(true);
            return new PasswordPromptResult
            {
                Password = r.Password,
                Remember = r.Remember,
                Cancelled = r.Cancelled
            };
        }

        public async Task<string> PromptPassphraseAsync(string keyName)
        {
            PassphraseDialogResult r = await PassphraseDialog.ShowAsync(keyName).ConfigureAwait(true);
            return r.Cancelled ? null : r.Passphrase;
        }

        public async Task<IReadOnlyList<string>> PromptKeyboardInteractiveAsync(AuthPromptEventArgs args)
        {
            var prompts = new List<KbdInteractivePrompt>();
            for (int i = 0; i < args.Prompts.Count; i++)
            {
                bool echo = i < args.Echo.Count && args.Echo[i];
                prompts.Add(new KbdInteractivePrompt { Prompt = args.Prompts[i], Echo = echo });
            }
            KbdInteractiveDialogResult r = await KbdInteractiveDialog.ShowAsync(
                args.Name, args.Instruction, prompts).ConfigureAwait(true);
            if (r.Cancelled)
            {
                return null;
            }
            return r.Answers;
        }

        // K03：agent 无可用密钥时提示选择密钥解锁（只回传 KeyId，不接触私钥）。
        public async Task<AgentUnlockResult> PromptAgentUnlockAsync(
            IReadOnlyList<AgentKeyChoice> choices, string hostDisplay)
        {
            return await AgentUnlockDialog.ShowAsync(choices, hostDisplay).ConfigureAwait(true);
        }
    }
}
