using System.Threading.Tasks;

namespace SshTool.Core.Storage
{
    // 01-DESIGN.md §8.2：凭据键值表（DPAPI 加密实现由 D03 提供）。
    public interface ISecretStore
    {
        Task<string> GetAsync(string key);
        Task SetAsync(string key, string value);
        Task RemoveAsync(string key);
        Task RemoveByPrefixAsync(string prefix);
    }
}
