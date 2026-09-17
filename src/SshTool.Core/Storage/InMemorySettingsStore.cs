using System.Collections.Generic;

namespace SshTool.Core.Storage
{
    // ISettingsStore 的内存实现：测试与临时通路（T13 之前）使用。
    public sealed class InMemorySettingsStore : ISettingsStore
    {
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>();

        public bool TryGet(string key, out object value)
        {
            return _values.TryGetValue(key, out value);
        }

        public void Set(string key, object value)
        {
            _values[key] = value;
        }
    }
}
