using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SshTool.Core.Common;
using SshTool.Core.Storage;

namespace SshTool.Core.Sync.Vault
{
    // 03-SYNC-PROTOCOL.md §6.2 VaultCache 持久化（对应桌面端 vault-cache.ts 的 VaultCache）。
    //
    // 落盘位置 LocalFolder/secure/vault-cache.bin（经 ISecureFile 加密；DPAPI 实现见 D03）。
    // 格式：{ version:1, ...VaultCacheState.ToJson() }，UTF-8 无 BOM。
    // 语义与桌面端逐项对齐：
    //   - version != 1 → 初始状态（前向兼容：旧版本直接丢弃）。
    //   - 文件缺失/为空 → 初始状态；损坏（JSON 非法或嵌套段校验失败）→ 记警告后重置为初始。
    //   - Lock()：仅 vaultKey 置 null（无 key 时直接返回，不写盘）；Clear()：整体重置。
    //   - BindToUser(userId)：用户变化时整体重置（只保留新 userId 与默认偏好），见 §6.2。
    //
    // 并发：内存状态用 lock 保护；文件读写用 SemaphoreSlim 串行化（与 AuthStore 同构）。
    // 日志脱敏：只记“已损坏/已重置/已绑定”等相位，绝不记录 vaultKey、恢复密钥、文档内容与 pending 明文。
    public sealed class VaultCacheStore
    {
        // 01-DESIGN.md §8.2：保险库缓存落 LocalFolder/secure/vault-cache.bin（DPAPI 加密）。
        public const string RelativePath = "secure/vault-cache.bin";

        public const int CurrentVersion = 1;

        private static readonly Encoding FileEncoding = new UTF8Encoding(false);

        private readonly ISecureFile _file;
        private readonly ILogger _logger;
        private readonly object _mutex = new object();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        private VaultCacheState _state = VaultCacheState.Initial();

        public VaultCacheStore(ISecureFile file, ILogger logger = null)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }
            _file = file;
            _logger = logger;
        }

        // 当前快照（拷贝）。尚未 LoadAsync 时返回初始状态。
        public VaultCacheState State
        {
            get
            {
                lock (_mutex)
                {
                    return _state.Clone();
                }
            }
        }

        public async Task<VaultCacheState> LoadAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                byte[] bytes = await _file.ReadAsync().ConfigureAwait(false);
                VaultCacheState parsed = null;
                if (bytes != null && bytes.Length > 0)
                {
                    if (!TryParse(bytes, out parsed))
                    {
                        if (_logger != null)
                        {
                            _logger.Log(LogLevel.Warning, "VaultCache", "vault-cache.bin 已损坏，已重置为初始状态");
                        }
                        parsed = VaultCacheState.Initial();
                        try
                        {
                            await _file.WriteAsync(Serialize(parsed)).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // 回写失败不抛：下次启动再试。
                        }
                    }
                }
                else
                {
                    parsed = VaultCacheState.Initial();
                }
                lock (_mutex)
                {
                    _state = parsed;
                    return _state.Clone();
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        // 全量替换并落盘（调用方先 Clone 再改，避免外部引用被持久化）。
        public async Task<VaultCacheState> SaveAsync(VaultCacheState next)
        {
            if (next == null)
            {
                throw new ArgumentNullException(nameof(next));
            }
            VaultCacheState snapshot = next.Clone();
            byte[] bytes = Serialize(snapshot);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _file.WriteAsync(bytes).ConfigureAwait(false);
                lock (_mutex)
                {
                    _state = snapshot;
                    return _state.Clone();
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        // 读-改-写原子更新（先落盘后生效，与踩坑 #12 一致）。
        public async Task<VaultCacheState> UpdateAsync(Action<VaultCacheState> mutate)
        {
            if (mutate == null)
            {
                throw new ArgumentNullException(nameof(mutate));
            }
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                VaultCacheState next;
                lock (_mutex)
                {
                    next = _state.Clone();
                }
                mutate(next);
                if (next == null)
                {
                    throw new InvalidOperationException("缓存更新不得返回空状态");
                }
                byte[] bytes = Serialize(next);
                await _file.WriteAsync(bytes).ConfigureAwait(false);
                lock (_mutex)
                {
                    _state = next;
                    return _state.Clone();
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        // §6.2 Lock()：vaultKey 置 null（其余保留）。无 key 时不写盘。
        public async Task LockAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                bool hasKey;
                lock (_mutex)
                {
                    hasKey = _state.VaultKeyBase64 != null;
                }
                if (!hasKey)
                {
                    return;
                }
                VaultCacheState next;
                lock (_mutex)
                {
                    next = _state.Clone();
                }
                next.VaultKeyBase64 = null;
                await _file.WriteAsync(Serialize(next)).ConfigureAwait(false);
                lock (_mutex)
                {
                    _state = next;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        // Clear()：重置为初始（userId 一并清空；需保留时由调用方随后 BindToUser）。
        public async Task ClearAsync()
        {
            await SaveAsync(VaultCacheState.Initial()).ConfigureAwait(false);
        }

        // §6.2 绑定用户：userId 为空（未登录）→ 不变；
        // 已有 userId 且与传入不同 → 整体重置（只保留新 userId 与默认偏好）；
        // 尚无 userId → 记下 userId。其余字段原样保留。
        public async Task<VaultCacheState> BindToUserAsync(string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                lock (_mutex)
                {
                    return _state.Clone();
                }
            }
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                VaultCacheState current;
                lock (_mutex)
                {
                    current = _state.Clone();
                }
                if (!string.IsNullOrEmpty(current.UserId)
                    && !string.Equals(current.UserId, userId, StringComparison.Ordinal))
                {
                    if (_logger != null)
                    {
                        _logger.Log(LogLevel.Info, "VaultCache", "登录用户变化，保险库缓存已重置");
                    }
                    var reset = VaultCacheState.Initial();
                    reset.UserId = userId;
                    await _file.WriteAsync(Serialize(reset)).ConfigureAwait(false);
                    lock (_mutex)
                    {
                        _state = reset;
                        return _state.Clone();
                    }
                }
                if (string.IsNullOrEmpty(current.UserId))
                {
                    current.UserId = userId;
                    await _file.WriteAsync(Serialize(current)).ConfigureAwait(false);
                    lock (_mutex)
                    {
                        _state = current;
                        return _state.Clone();
                    }
                }
                lock (_mutex)
                {
                    return _state.Clone();
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private static byte[] Serialize(VaultCacheState state)
        {
            JObject root = state.ToJson();
            return FileEncoding.GetBytes(root.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static bool TryParse(byte[] bytes, out VaultCacheState state)
        {
            state = null;
            string text;
            try
            {
                text = FileEncoding.GetString(bytes, 0, bytes.Length);
            }
            catch (Exception)
            {
                return false;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            JObject root;
            try
            {
                root = JsonText.ParseObject(text);
            }
            catch (Exception)
            {
                return false;
            }
            try
            {
                state = VaultCacheState.Parse(root);
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }
    }
}
