using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SshTool.Core.Storage
{
    // 01-DESIGN.md §8.4：内存缓存 + 写操作串行化（SemaphoreSlim）+ 落盘后触发 Changed。
    // GetAllAsync 返回活引用：调用方修改实体后必须调 Update/UpdateMany 落盘，
    // 未落盘的修改只影响内存（ConfigService 级联即用此模式批量改后一次保存）。
    public class Repository<T>
    {
        private readonly JsonStore<T> _store;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private List<T> _items;
        // O08：id → _items 下标。GetByIdAsync 原本是 O(n) 线性扫，而它就落在
        // 每次连接（SessionManager.StartOpenAsync）、每次重连、每次密钥认证的路上。
        // _items 每次整体替换时同步重建（见 SetItems）。
        private Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.Ordinal);
        private bool _loaded;

        public Repository(JsonStore<T> store)
        {
            if (store == null)
            {
                throw new ArgumentNullException("store");
            }
            _store = store;
        }

        public event EventHandler<RepositoryChangedEventArgs> Changed;

        public IReadOnlyList<string> LoadWarnings
        {
            get { return _store.LoadWarnings; }
        }

        public async Task LoadAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureLoadedCoreAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<IReadOnlyList<T>> GetAllAsync()
        {
            await LoadAsync().ConfigureAwait(false);
            return _items;
        }

        public async Task<T> GetByIdAsync(string id)
        {
            await LoadAsync().ConfigureAwait(false);
            int index = FindIndex(id);
            return index < 0 ? default(T) : _items[index];
        }

        public async Task AddAsync(T item, ChangeOrigin origin = ChangeOrigin.User)
        {
            List<string> changedIds = null;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureLoadedCoreAsync().ConfigureAwait(false);
                string id = IdOf(item);
                if (string.IsNullOrEmpty(id))
                {
                    throw new InvalidOperationException("实体 id 为空");
                }
                if (FindIndex(id) >= 0)
                {
                    throw new InvalidOperationException("实体 id 重复: " + id);
                }
                var next = new List<T>(_items);
                next.Add(item);
                await _store.SaveAsync(next).ConfigureAwait(false);
                SetItems(next);
                changedIds = new List<string> { id };
            }
            finally
            {
                _gate.Release();
            }
            RaiseChanged(origin, changedIds);
        }

        public async Task AddManyAsync(IReadOnlyList<T> items, ChangeOrigin origin = ChangeOrigin.User)
        {
            if (items == null)
            {
                throw new ArgumentNullException("items");
            }
            List<string> changedIds = null;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureLoadedCoreAsync().ConfigureAwait(false);
                var next = new List<T>(_items);
                var batchIds = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < items.Count; i++)
                {
                    string id = IdOf(items[i]);
                    if (string.IsNullOrEmpty(id))
                    {
                        throw new InvalidOperationException("实体 id 为空");
                    }
                    if (FindIndex(id) >= 0 || batchIds.Contains(id))
                    {
                        throw new InvalidOperationException("实体 id 重复: " + id);
                    }
                    next.Add(items[i]);
                    batchIds.Add(id);
                }
                if (batchIds.Count == 0)
                {
                    return;
                }
                await _store.SaveAsync(next).ConfigureAwait(false);
                SetItems(next);
                changedIds = new List<string>(batchIds);
            }
            finally
            {
                _gate.Release();
            }
            RaiseChanged(origin, changedIds);
        }

        public async Task<bool> UpdateAsync(T item, ChangeOrigin origin = ChangeOrigin.User)
        {
            return await UpdateManyAsync(new List<T> { item }, origin).ConfigureAwait(false);
        }

        // 按 id 替换；不存在的 id 忽略。全部未命中时不写盘、不发事件。
        public async Task<bool> UpdateManyAsync(IReadOnlyList<T> items, ChangeOrigin origin = ChangeOrigin.User)
        {
            List<string> changedIds = null;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureLoadedCoreAsync().ConfigureAwait(false);
                var byId = new Dictionary<string, T>(StringComparer.Ordinal);
                foreach (var item in items)
                {
                    string id = IdOf(item);
                    if (!string.IsNullOrEmpty(id))
                    {
                        byId[id] = item;
                    }
                }
                var next = new List<T>(_items.Count);
                var replaced = new List<string>();
                foreach (var existing in _items)
                {
                    T replacement;
                    if (byId.TryGetValue(IdOf(existing), out replacement))
                    {
                        next.Add(replacement);
                        replaced.Add(IdOf(existing));
                    }
                    else
                    {
                        next.Add(existing);
                    }
                }
                if (replaced.Count == 0)
                {
                    return false;
                }
                await _store.SaveAsync(next).ConfigureAwait(false);
                SetItems(next);
                changedIds = replaced;
            }
            finally
            {
                _gate.Release();
            }
            RaiseChanged(origin, changedIds);
            return true;
        }

        public async Task<bool> RemoveAsync(string id, ChangeOrigin origin = ChangeOrigin.User)
        {
            return await RemoveManyAsync(new List<string> { id }, origin).ConfigureAwait(false);
        }

        public async Task<bool> RemoveManyAsync(IReadOnlyList<string> ids, ChangeOrigin origin = ChangeOrigin.User)
        {
            List<string> changedIds = null;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureLoadedCoreAsync().ConfigureAwait(false);
                var doomed = new HashSet<string>(ids, StringComparer.Ordinal);
                var next = new List<T>(_items.Count);
                var removed = new List<string>();
                foreach (var existing in _items)
                {
                    if (doomed.Contains(IdOf(existing)))
                    {
                        removed.Add(IdOf(existing));
                    }
                    else
                    {
                        next.Add(existing);
                    }
                }
                if (removed.Count == 0)
                {
                    return false;
                }
                await _store.SaveAsync(next).ConfigureAwait(false);
                SetItems(next);
                changedIds = removed;
            }
            finally
            {
                _gate.Release();
            }
            RaiseChanged(origin, changedIds);
            return true;
        }

        // 同步下行用：整份替换。origin 应为 Sync（不触发同步标脏）。
        public async Task ReplaceAllAsync(IReadOnlyList<T> items, ChangeOrigin origin)
        {
            List<string> changedIds;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureLoadedCoreAsync().ConfigureAwait(false);
                var next = new List<T>(items);
                await _store.SaveAsync(next).ConfigureAwait(false);
                SetItems(next);
                changedIds = new List<string>(next.Count);
                foreach (var item in next)
                {
                    changedIds.Add(IdOf(item));
                }
            }
            finally
            {
                _gate.Release();
            }
            RaiseChanged(origin, changedIds);
        }

        public async Task FlushAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_items == null)
                {
                    return;
                }
                await _store.SaveAsync(_items).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task EnsureLoadedCoreAsync()
        {
            if (_loaded)
            {
                return;
            }
            SetItems(new List<T>(await _store.LoadAsync().ConfigureAwait(false)));
            _loaded = true;
        }

        // O08：替换 _items 的唯一入口，保证索引不与列表脱节。
        // 重复 id（存储文件被写坏）沿用旧的「第一个命中」语义：字典只记首次出现，
        // 不用 Add 以免在损坏数据上抛异常。
        private void SetItems(List<T> next)
        {
            var index = new Dictionary<string, int>(next.Count, StringComparer.Ordinal);
            for (int i = 0; i < next.Count; i++)
            {
                string id = IdOf(next[i]);
                if (!string.IsNullOrEmpty(id) && !index.ContainsKey(id))
                {
                    index[id] = i;
                }
            }
            _items = next;
            _index = index;
        }

        private int FindIndex(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return -1;
            }
            int found;
            return _index.TryGetValue(id, out found) ? found : -1;
        }

        private string IdOf(T item)
        {
            return _store.Codec.GetId(item);
        }

        private void RaiseChanged(ChangeOrigin origin, List<string> changedIds)
        {
            var handler = Changed;
            if (handler != null && changedIds != null && changedIds.Count > 0)
            {
                handler(this, new RepositoryChangedEventArgs(origin, changedIds));
            }
        }
    }
}
