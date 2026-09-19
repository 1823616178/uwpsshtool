using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SshTool.Core.Common;
using SshTool.Core.Storage;
using SshTool.Core.Sync.Api;
using SshTool.Core.Sync.Api.Dtos;

namespace SshTool.Core.Sync.Auth
{
    // 03-SYNC-PROTOCOL.md §6.1 AuthState（secure/auth.bin），桌面端 auth-store.ts 的移植。
    // 持久格式：
    //   { version:1, user:{id,email}, device:{id,name},
    //     tokens:{accessToken,refreshToken,expiresAt}, refreshUncertain:false }
    // 同时实现 ITokenStore，供 ApiClient 直接使用：
    //   - 刷新成功 → Save（expiresAt = now + expiresIn×1000，refreshUncertain=false）
    //   - 刷新网络/超时失败 → MarkRefreshUncertain（§2.4.5）
    //   - 终端鉴权错误 → Clear（§2.4.6，与 S07 一致）
    //
    // 并发：内存状态用 lock 保护；文件读写用 SemaphoreSlim 串行化，
    // 公开方法一律先占 gate 再读内存，保证读-改-写原子。
    // ITokenStore 的同步方法会阻塞等待落盘（InMemorySecureFile 瞬时完成；
    // DPAPI 实现在调用线程上等待文件 IO，全程 ConfigureAwait(false) 不回 UI 线程）。
    // 因此 ApiClient（含刷新）必须在线程池调用，不得在 UI 线程同步等待。
    public sealed class AuthStore : ITokenStore
    {
        // 01-DESIGN.md §8.2：认证状态落 LocalFolder/secure/auth.bin（DPAPI 加密）。
        public const string RelativePath = "secure/auth.bin";

        public const int CurrentVersion = 1;

        private static readonly DateTimeOffset UnixEpoch =
            new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private static readonly Encoding FileEncoding = new UTF8Encoding(false);

        private readonly ISecureFile _file;
        private readonly Func<DateTimeOffset> _clock;
        private readonly ILogger _logger;
        private readonly object _mutex = new object();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        // null = 未登录
        private StoredSession _state;

        private sealed class StoredSession
        {
            public string UserId;
            public string UserEmail;
            public string DeviceId;
            public string DeviceName;
            public string AccessToken;
            public string RefreshToken;
            public long ExpiresAt;
            public bool RefreshUncertain;
        }

        public AuthStore(ISecureFile file, Func<DateTimeOffset> clock = null, ILogger logger = null)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }
            _file = file;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _logger = logger;
        }

        // 从 secure/auth.bin 读回内存并返回会话。文件缺失/为空 → 未登录；
        // 损坏 → 记警告（不含任何敏感内容）并清空文件，按未登录处理。
        public async Task<AuthState> LoadAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                byte[] bytes = await _file.ReadAsync().ConfigureAwait(false);
                StoredSession parsed = null;
                if (bytes != null && bytes.Length > 0)
                {
                    if (!TryParse(bytes, out parsed))
                    {
                        if (_logger != null)
                        {
                            _logger.Log(LogLevel.Warning, "Auth", "auth.bin 已损坏，已按未登录处理");
                        }
                        parsed = null;
                        try
                        {
                            await _file.WriteAsync(new byte[0]).ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // 清空失败不抛：下次启动再试。
                        }
                    }
                }
                lock (_mutex)
                {
                    _state = parsed;
                    return SnapshotLocked();
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public AuthState Session
        {
            get
            {
                lock (_mutex)
                {
                    return SnapshotLocked();
                }
            }
        }

        // 完整 token（含 ExpiresAt）；未登录返回 null。返回的是拷贝。
        public AuthTokens Tokens
        {
            get
            {
                lock (_mutex)
                {
                    if (_state == null)
                    {
                        return null;
                    }
                    return new AuthTokens
                    {
                        AccessToken = _state.AccessToken,
                        RefreshToken = _state.RefreshToken,
                        ExpiresAt = _state.ExpiresAt
                    };
                }
            }
        }

        public bool CanRefresh
        {
            get
            {
                lock (_mutex)
                {
                    return _state != null && !_state.RefreshUncertain;
                }
            }
        }

        // §2.4.5：expiresAt = now + expiresIn×1000，refreshUncertain=false。
        public async Task<AuthState> SaveAsync(AuthTokenResponse response)
        {
            if (response == null)
            {
                throw new ArgumentNullException(nameof(response));
            }
            RequireToken(response.AccessToken, nameof(response));
            RequireToken(response.RefreshToken, nameof(response));
            if (response.User == null || response.Device == null)
            {
                throw new ArgumentException("认证响应缺少用户或设备信息", nameof(response));
            }
            RequireToken(response.User.Id, nameof(response));
            RequireToken(response.User.Email, nameof(response));
            RequireToken(response.Device.Id, nameof(response));
            RequireToken(response.Device.Name, nameof(response));

            var next = new StoredSession
            {
                UserId = response.User.Id,
                UserEmail = response.User.Email,
                DeviceId = response.Device.Id,
                DeviceName = response.Device.Name,
                AccessToken = response.AccessToken,
                RefreshToken = response.RefreshToken,
                ExpiresAt = ToUnixMs(_clock()) + (long)response.ExpiresIn * 1000L,
                RefreshUncertain = false
            };
            byte[] bytes = Serialize(next);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _file.WriteAsync(bytes).ConfigureAwait(false);
                lock (_mutex)
                {
                    _state = next;
                    return SnapshotLocked();
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        // §2.4.5：刷新请求网络/超时失败后标记，下次刷新直接不可用。
        // 无会话时静默返回（与桌面端 markRefreshUncertain 一致）。
        public async Task MarkRefreshUncertainAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                StoredSession next;
                lock (_mutex)
                {
                    if (_state == null)
                    {
                        return;
                    }
                    next = Clone(_state);
                    next.RefreshUncertain = true;
                }
                byte[] bytes = Serialize(next);
                await _file.WriteAsync(bytes).ConfigureAwait(false);
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

        public async Task ClearAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await _file.WriteAsync(new byte[0]).ConfigureAwait(false);
                lock (_mutex)
                {
                    _state = null;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        // ---------- ITokenStore（ApiClient 对接，语义与 S07 一致） ----------
        // 同步方法会阻塞等待落盘；ApiClient（含刷新）必须在线程池调用，
        // 不得在 UI 线程同步等待（见类注释）。

        public TokenPair GetTokens()
        {
            lock (_mutex)
            {
                if (_state == null)
                {
                    return null;
                }
                return new TokenPair(_state.AccessToken, _state.RefreshToken);
            }
        }

        public void Save(AuthTokenResponse tokens)
        {
            SaveAsync(tokens).GetAwaiter().GetResult();
        }

        public void Clear()
        {
            ClearAsync().GetAwaiter().GetResult();
        }

        public void MarkRefreshUncertain()
        {
            MarkRefreshUncertainAsync().GetAwaiter().GetResult();
        }

        // ---------- 持久格式 ----------

        private static byte[] Serialize(StoredSession session)
        {
            var root = new JObject
            {
                ["version"] = CurrentVersion,
                ["user"] = new JObject
                {
                    ["id"] = session.UserId,
                    ["email"] = session.UserEmail
                },
                ["device"] = new JObject
                {
                    ["id"] = session.DeviceId,
                    ["name"] = session.DeviceName
                },
                ["tokens"] = new JObject
                {
                    ["accessToken"] = session.AccessToken,
                    ["refreshToken"] = session.RefreshToken,
                    ["expiresAt"] = session.ExpiresAt
                },
                ["refreshUncertain"] = session.RefreshUncertain
            };
            return FileEncoding.GetBytes(root.ToString(Formatting.None));
        }

        private static bool TryParse(byte[] bytes, out StoredSession session)
        {
            session = null;
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
            if (!IsInt(root["version"], CurrentVersion))
            {
                return false;
            }
            var user = root["user"] as JObject;
            var device = root["device"] as JObject;
            var tokens = root["tokens"] as JObject;
            if (user == null || device == null || tokens == null)
            {
                return false;
            }
            var uncertain = root["refreshUncertain"];
            if (uncertain == null || uncertain.Type != JTokenType.Boolean)
            {
                return false;
            }
            string userId = NonEmpty(user["id"]);
            string userEmail = NonEmpty(user["email"]);
            string deviceId = NonEmpty(device["id"]);
            string deviceName = NonEmpty(device["name"]);
            string accessToken = NonEmpty(tokens["accessToken"]);
            string refreshToken = NonEmpty(tokens["refreshToken"]);
            long expiresAt;
            if (!TryLong(tokens["expiresAt"], out expiresAt) || expiresAt < 0)
            {
                return false;
            }
            if (userId == null || userEmail == null || deviceId == null || deviceName == null
                || accessToken == null || refreshToken == null)
            {
                return false;
            }
            session = new StoredSession
            {
                UserId = userId,
                UserEmail = userEmail,
                DeviceId = deviceId,
                DeviceName = deviceName,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ExpiresAt = expiresAt,
                RefreshUncertain = (bool)uncertain
            };
            return true;
        }

        private static bool IsInt(JToken token, long expected)
        {
            return token != null && token.Type == JTokenType.Integer && (long)token == expected;
        }

        private static bool TryLong(JToken token, out long value)
        {
            value = 0;
            if (token == null || token.Type != JTokenType.Integer)
            {
                return false;
            }
            value = (long)token;
            return true;
        }

        private static string NonEmpty(JToken token)
        {
            if (token == null || token.Type != JTokenType.String)
            {
                return null;
            }
            string value = (string)token;
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private AuthState SnapshotLocked()
        {
            if (_state == null)
            {
                return AuthState.Unauthenticated;
            }
            return new AuthState
            {
                Authenticated = true,
                UserId = _state.UserId,
                UserEmail = _state.UserEmail,
                DeviceId = _state.DeviceId,
                DeviceName = _state.DeviceName
            };
        }

        private static StoredSession Clone(StoredSession source)
        {
            return new StoredSession
            {
                UserId = source.UserId,
                UserEmail = source.UserEmail,
                DeviceId = source.DeviceId,
                DeviceName = source.DeviceName,
                AccessToken = source.AccessToken,
                RefreshToken = source.RefreshToken,
                ExpiresAt = source.ExpiresAt,
                RefreshUncertain = source.RefreshUncertain
            };
        }

        private static long ToUnixMs(DateTimeOffset value)
        {
            return (long)(value.ToUniversalTime() - UnixEpoch).TotalMilliseconds;
        }

        private static void RequireToken(string value, string paramName)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new ArgumentException("认证响应的 token 不能为空", paramName);
            }
        }
    }
}
