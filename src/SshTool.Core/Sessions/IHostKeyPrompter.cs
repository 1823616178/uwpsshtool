using System.Threading.Tasks;

namespace SshTool.Core.Sessions
{
    public interface IHostKeyPrompter
    {
        Task<bool> PromptUnknownAsync(HostKeyInfo info, string hostDisplay);
        // 返回 true = 用户确认「移除旧记录并重试」（调用方清 known_hosts + 钉住指纹后重连）；
        // false = 取消（连接保持失败）。
        Task<bool> PromptMismatchAsync(HostKeyInfo info, string hostDisplay, string previousFingerprint);
    }
}
