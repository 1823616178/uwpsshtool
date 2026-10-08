using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.Core.Sessions;

namespace SshTool.App.Platform
{
    public sealed class UwpCredentialPrompter : ICredentialPrompter
    {
        public async Task<PasswordPromptResult> PromptPasswordAsync(string hostDisplay, int retriesLeft)
        {
            // fix/functional-pass：Core 只给剩余次数，文案在此本地化。
            string errorMessage = retriesLeft < 0
                ? null
                : Localized.Format("Auth_PasswordWrongRetries", "密码错误，还可尝试 {0} 次", retriesLeft);
            CredentialDialogResult r = await CredentialDialog.ShowAsync(hostDisplay, errorMessage).ConfigureAwait(true);
            return new PasswordPromptResult
            {
                Password = r.Password,
                Remember = r.Remember,
                Cancelled = r.Cancelled
            };
        }

        public async Task<PassphrasePromptResult> PromptPassphraseAsync(string keyName, bool previousWrong)
        {
            string error = previousWrong
                ? Localized.Get("Auth_PassphraseWrong", "短语错误，请重新输入")
                : null;
            PassphraseDialogResult r = await PassphraseDialog.ShowAsync(keyName, error).ConfigureAwait(true);
            return new PassphrasePromptResult
            {
                Passphrase = r.Cancelled ? null : r.Passphrase,
                Remember = !r.Cancelled && r.Remember,
                Cancelled = r.Cancelled
            };
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
