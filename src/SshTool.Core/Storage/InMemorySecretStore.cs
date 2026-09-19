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

        public event EventHandler<SecretChangedEventArgs> Changed;

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

        public async Task SetAsync(string key, string value, ChangeOrigin origin = ChangeOrigin.User)
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
            RaiseChanged(origin, new string[] { key });
        }

        public async Task RemoveAsync(string key, ChangeOrigin origin = ChangeOrigin.User)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }
            bool removed;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                removed = _map.Remove(key);
            }
            finally
            {
                _gate.Release();
            }
            if (removed)
            {
                RaiseChanged(origin, new string[] { key });
            }
        }

        public async Task RemoveByPrefixAsync(string prefix, ChangeOrigin origin = ChangeOrigin.User)
        {
            prefix = prefix ?? string.Empty;
            List<string> doomed;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                doomed = new List<string>();
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
            if (doomed.Count != 0)
            {
                RaiseChanged(origin, doomed);
            }
        }

        private void RaiseChanged(ChangeOrigin origin, IReadOnlyList<string> changedKeys)
        {
            var handler = Changed;
            if (handler != null && changedKeys != null && changedKeys.Count != 0)
            {
                handler(this, new SecretChangedEventArgs(origin, changedKeys));
            }
        }
    }
}
