using System.Threading.Tasks;

namespace SshTool.Core.Sessions
{
    public interface IHostKeyPrompter
    {
        Task<bool> PromptUnknownAsync(HostKeyInfo info, string hostDisplay);
        Task<bool> PromptMismatchAsync(HostKeyInfo info, string hostDisplay, string previousFingerprint);
    }
}
