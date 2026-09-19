using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SshTool.Core.Storage;

namespace SshTool.App.Platform
{
    // D03：整个键值表 JSON 经 ISecureFile 加密落盘；串行化访问。
    // S14：写操作落盘后触发 Changed（只带键名与来源，不带值；凭据 Changed 接线据此标脏）。
    public sealed class DpapiSecretStore : ISecretStore
    {
        private readonly ISecureFile _file;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private Dictionary<string, string> _map;
        private bool _loaded;

        public event EventHandler<SecretChangedEventArgs> Changed;

        public DpapiSecretStore()
            : this(new DpapiSecureFile())
        {
        }

        public DpapiSecretStore(ISecureFile file)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }
            _file = file;
        }

        public async Task<string> GetAsync(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureLoadedAsync().ConfigureAwait(false);
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
                await EnsureLoadedAsync().ConfigureAwait(false);
                if (value == null)
                {
                    _map.Remove(key);
                }
                else
                {
                    _map[key] = value;
                }
                await PersistAsync().ConfigureAwait(false);
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
                await EnsureLoadedAsync().ConfigureAwait(false);
                removed = _map.Remove(key);
                if (removed)
                {
                    await PersistAsync().ConfigureAwait(false);
                }
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
                await EnsureLoadedAsync().ConfigureAwait(false);
                doomed = new List<string>();
                foreach (KeyValuePair<string, string> pair in _map)
                {
                    if (pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        doomed.Add(pair.Key);
                    }
                }
                if (doomed.Count == 0)
                {
                    return;
                }
                for (int i = 0; i < doomed.Count; i++)
                {
                    _map.Remove(doomed[i]);
                }
                await PersistAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
            RaiseChanged(origin, doomed);
        }

        private async Task EnsureLoadedAsync()
        {
            if (_loaded)
            {
                return;
            }
            _map = new Dictionary<string, string>(StringComparer.Ordinal);
            byte[] raw = await _file.ReadAsync().ConfigureAwait(false);
            if (raw != null && raw.Length > 0)
            {
                string json = Encoding.UTF8.GetString(raw, 0, raw.Length);
                JObject obj = JsonText.ParseObject(json);
                foreach (JProperty property in obj.Properties())
                {
                    if (property.Value != null && property.Value.Type == JTokenType.String)
                    {
                        _map[property.Name] = (string)property.Value;
                    }
                }
            }
            _loaded = true;
        }

        private async Task PersistAsync()
        {
            var obj = new JObject();
            foreach (KeyValuePair<string, string> pair in _map)
            {
                obj[pair.Key] = pair.Value;
            }
            byte[] utf8 = Encoding.UTF8.GetBytes(obj.ToString(Newtonsoft.Json.Formatting.None));
            await _file.WriteAsync(utf8).ConfigureAwait(false);
        }

        private void RaiseChanged(ChangeOrigin origin, IList<string> changedKeys)
        {
            var handler = Changed;
            if (handler != null && changedKeys != null && changedKeys.Count != 0)
            {
                handler(this, new SecretChangedEventArgs(origin, new List<string>(changedKeys)));
            }
        }
    }
}
