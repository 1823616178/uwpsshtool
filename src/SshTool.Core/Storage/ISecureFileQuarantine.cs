using System.Threading.Tasks;

namespace SshTool.Core.Storage
{
    // opt/full-pass：能把损坏的受保护文件挪到一边（改名保留，不删除）的 ISecureFile。
    // 用于「解密失败 / 内容无法解析」这类永久性损坏：挪走后从空开始，而不是每次读都抛、
    // 应用永久卡在凭据读取上。返回 false 表示没有可挪的文件或挪动失败。
    public interface ISecureFileQuarantine
    {
        Task<bool> QuarantineAsync(string reason);
    }
}
