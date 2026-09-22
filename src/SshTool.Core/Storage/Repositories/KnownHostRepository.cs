using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Storage.Codecs;

namespace SshTool.Core.Storage.Repositories
{
    // §8.2：LocalFolder/data/knownhosts.json
    public sealed class KnownHostRepository : Repository<KnownHost>
    {
        public const string DataFilePath = "data/knownhosts.json";

        // O08：(host, port) → 条目。known_hosts 的查找键不是 id 而是主机+端口，
        // 调用方原先自己线性扫（SessionManager.FindKnownAsync），跳板链每跳扫一次、
        // 落盘时再扫一次。索引随 Changed 失效，下次查询时重建（懒重建：连接路径
        // 只读，不值得在每次写入时全量重算）。
        private readonly object _lookupGate = new object();
        private Dictionary<string, KnownHost> _byHostPort;

        public KnownHostRepository(IFileSystem fs, ILogger logger = null)
            : base(new JsonStore<KnownHost>(fs, DataFilePath, new KnownHostCodec(), StorageMigrations.All, logger))
        {
            Changed += OnChangedInvalidate;
        }

        // 主机名按 OrdinalIgnoreCase 比较（与原线性扫一致）；端口拼进键里。
        public async Task<KnownHost> FindAsync(string host, int port)
        {
            if (string.IsNullOrEmpty(host))
            {
                return null;
            }
            IReadOnlyList<KnownHost> all = await GetAllAsync().ConfigureAwait(false);
            Dictionary<string, KnownHost> index;
            lock (_lookupGate)
            {
                if (_byHostPort == null)
                {
                    var built = new Dictionary<string, KnownHost>(all.Count, StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < all.Count; i++)
                    {
                        string key = KeyOf(all[i].Host, all[i].Port);
                        // 重复条目沿用旧的「第一个命中」语义
                        if (key != null && !built.ContainsKey(key))
                        {
                            built[key] = all[i];
                        }
                    }
                    _byHostPort = built;
                }
                index = _byHostPort;
            }
            KnownHost found;
            return index.TryGetValue(KeyOf(host, port), out found) ? found : null;
        }

        // 键形如 "22/example.com"：端口在前，'/' 不可能出现在端口数字里，
        // 因此不存在 "1/2:3" 这类拼接歧义。
        private static string KeyOf(string host, int port)
        {
            if (string.IsNullOrEmpty(host))
            {
                return null;
            }
            return port.ToString(CultureInfo.InvariantCulture) + "/" + host;
        }

        private void OnChangedInvalidate(object sender, RepositoryChangedEventArgs e)
        {
            lock (_lookupGate)
            {
                _byHostPort = null;
            }
        }
    }
}
