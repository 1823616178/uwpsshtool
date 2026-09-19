using System.Threading.Tasks;
using SshTool.App.Dialogs;
using SshTool.Core.Sessions;

namespace SshTool.App.Platform
{
    public sealed class UwpHostKeyPrompter : IHostKeyPrompter
    {
        public async Task<bool> PromptUnknownAsync(HostKeyInfo info, string hostDisplay)
        {
            HostKeyDialogResult r = await HostKeyDialog.ShowAsync(
                hostDisplay, info.KeyType, info.FingerprintSha256, info.RandomArt).ConfigureAwait(true);
            return r.Trusted;
        }

        public async Task<bool> PromptMismatchAsync(HostKeyInfo info, string hostDisplay, string previousFingerprint)
        {
            await HostKeyMismatchDialog.ShowAsync(
                hostDisplay, previousFingerprint ?? string.Empty, info.FingerprintSha256).ConfigureAwait(true);
            return false;
        }
    }
}
