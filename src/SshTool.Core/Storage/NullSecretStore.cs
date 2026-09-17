using System.Threading.Tasks;

namespace SshTool.Core.Storage
{
    // D03 前的空实现：级联调用照常走接口，全部 no-op。
    public sealed class NullSecretStore : ISecretStore
    {
        public static readonly NullSecretStore Instance = new NullSecretStore();

        private NullSecretStore()
        {
        }

        public Task<string> GetAsync(string key)
        {
            return Task.FromResult<string>(null);
        }

        public Task SetAsync(string key, string value)
        {
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key)
        {
            return Task.CompletedTask;
        }

        public Task RemoveByPrefixAsync(string prefix)
        {
            return Task.CompletedTask;
        }
    }
}
