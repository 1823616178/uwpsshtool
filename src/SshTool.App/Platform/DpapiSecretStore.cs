using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
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
                throw new ArgumentException("credential key must not be empty", nameof(key));
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
            // opt/full-pass：内容损坏时隔离并从空表开始（SecretMapCodec）；解密失败由
            // DpapiSecureFile.ReadAsync 隔离后返回空。旧实现两种情况都每次抛异常，
            // 所有依赖凭据的功能永久不可用。
            _map = await SecretMapCodec.LoadAsync(_file, Warn).ConfigureAwait(false);
            _loaded = true;
        }

        private static void Warn(string message)
        {
            ILogger log = AppLog.Logger;
            if (log != null)
            {
                log.Log(LogLevel.Warning, "Secrets", message);
            }
        }

        private async Task PersistAsync()
        {
            byte[] utf8 = SecretMapCodec.Serialize(_map);
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
