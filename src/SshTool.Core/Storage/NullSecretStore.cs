using System;
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

        // 空实现永不写盘，也永不触发（订阅方不会因此标脏）。
        // 定制 add/remove 空实现：无后备字段，避免 CS0067（TreatWarningsAsErrors 下会失败）。
        public event EventHandler<SecretChangedEventArgs> Changed
        {
            add { }
            remove { }
        }

        public Task<string> GetAsync(string key)
        {
            return Task.FromResult<string>(null);
        }

        public Task SetAsync(string key, string value, ChangeOrigin origin = ChangeOrigin.User)
        {
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, ChangeOrigin origin = ChangeOrigin.User)
        {
            return Task.CompletedTask;
        }

        public Task RemoveByPrefixAsync(string prefix, ChangeOrigin origin = ChangeOrigin.User)
        {
            return Task.CompletedTask;
        }
    }
}
