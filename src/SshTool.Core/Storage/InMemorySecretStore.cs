using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SshTool.Core.Storage
{
    public sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _map = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public async Task<string> GetAsync(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                string value;
                return _map.TryGetValue(key, out value) ? value : null;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task SetAsync(string key, string value)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("凭据键不能为空", nameof(key));
            }
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (value == null)
                {
                    _map.Remove(key);
                }
                else
                {
                    _map[key] = value;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task RemoveAsync(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _map.Remove(key);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task RemoveByPrefixAsync(string prefix)
        {
            prefix = prefix ?? string.Empty;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var doomed = new List<string>();
                foreach (KeyValuePair<string, string> pair in _map)
                {
                    if (pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        doomed.Add(pair.Key);
                    }
                }
                for (int i = 0; i < doomed.Count; i++)
                {
                    _map.Remove(doomed[i]);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
