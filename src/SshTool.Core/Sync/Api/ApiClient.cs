using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SshTool.Core.Storage;
using SshTool.Core.Sync.Api.Dtos;

namespace SshTool.Core.Sync.Api
{
    // 桌面端 api-client.ts 的移植（S06 范围：单次发送，无重试/401 刷新/HEAD 回退 —— 那些是 S07）。
    // 端点与错误模型见 03-SYNC-PROTOCOL.md §2.2/§2.3。
    public sealed class ApiClient
    {
        public const string ApiPathPrefix = "/api/v1";

        private static readonly Regex SchemePattern =
            new Regex(@"^[A-Za-z][A-Za-z0-9+.-]*:", RegexOptions.Compiled);

        // _root 在 HTTPS → HTTP 回退后会切到 _fallbackRoot（粘滞，客户端生命周期内不再回切）
        private volatile string _root;
        private readonly string _primaryRoot;
        private readonly string _fallbackRoot;
        private readonly object _fallbackLock = new object();
        private readonly ITokenStore _tokens;
        private readonly IHttpTransport _transport;
        private readonly int _timeoutMs;
        private readonly int _retryLimit;
        private readonly Func<long, Task> _sleep;
        private readonly Func<DateTimeOffset> _clock;

        // 401 刷新单飞：并发请求共用同一个刷新任务
        private readonly object _refreshLock = new object();
        private Task _refreshTask;

        public ApiClient(
            string baseUrl,
            ITokenStore tokenStore,
            IHttpTransport transport,
            bool allowHttp = false,
            int timeoutMs = 15000,
            int retryLimit = 2,
            Func<long, Task> sleep = null,
            Func<DateTimeOffset> clock = null,
            bool httpFallback = false)
        {
            if (tokenStore == null) throw new ArgumentNullException(nameof(tokenStore));
            if (transport == null) throw new ArgumentNullException(nameof(transport));
            _root = NormalizeRoot(baseUrl, allowHttp);
            _primaryRoot = _root;
            // opt/full-pass：配置默认 https，但现网同步服务器尚未上 TLS。允许 HTTP 且开启
            // httpFallback 时，https 在传输层失败（握手/连接异常，不含超时）即一次性降级到
            // 同主机同端口的 http 并粘滞。注意这是可被主动攻击者诱导的降级——只是不比
            // 「一直用 http」更差；服务器上 TLS 后应把 httpFallback 关掉（见 01-DESIGN §12.3）。
            if (httpFallback && allowHttp && _root.StartsWith("https://", StringComparison.Ordinal))
            {
                _fallbackRoot = "http://" + _root.Substring("https://".Length);
            }
            _tokens = tokenStore;
            _transport = transport;
            _timeoutMs = timeoutMs;
            _retryLimit = Math.Max(0, retryLimit);
            _sleep = sleep ?? (ms => Task.Delay((int)Math.Min(ms, (long)int.MaxValue)));
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        // 供测试与 S07 诊断使用：规范化后的 API 根地址（以 / 结尾）；回退后为 http 根
        public string Root { get { return _root; } }

        // 当前是否经由明文 HTTP 通信（配置即 http，或 https 已回退到 http）
        public bool IsInsecureTransport
        {
            get { return _root.StartsWith("http://", StringComparison.Ordinal); }
        }

        // 是否已发生过 https → http 回退
        public bool UsingHttpFallback
        {
            get { return _fallbackRoot != null && ReferenceEquals(_root, _fallbackRoot); }
        }

        // 回退发生时触发一次（任意线程），供 App 记日志 / 刷新明文风险提示
        public event EventHandler HttpFallbackActivated;

        // If-Match 用的 ETag 值："revision-<n>"（带引号）；revision 必须是非负十进制整数字符串
        public static string FormatRevisionEtag(string revision)
        {
            if (revision == null || !Regex.IsMatch(revision, @"^(0|[1-9]\d*)$"))
            {
                throw new ArgumentException("revision 必须是非负十进制整数", nameof(revision));
            }
            return "\"revision-" + revision + "\"";
        }

        // ---------- 认证 ----------

        public async Task<AuthTokenResponse> RegisterAsync(
            string email, string password, string inviteCode, DeviceDescriptorData device,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var body = new JObject { ["email"] = email, ["password"] = password };
            if (inviteCode != null)
            {
                body["inviteCode"] = inviteCode;
            }
            body["device"] = device.ToJson();
            var result = await RequestAsync("auth/register", "POST", body, false, null, null,
                AuthTokenResponse.Parse, cancellationToken).ConfigureAwait(false);
            _tokens.Save(result);
            return result;
        }

        public async Task<AuthTokenResponse> LoginAsync(
            string email, string password, DeviceDescriptorData device,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var body = new JObject
            {
                ["email"] = email,
                ["password"] = password,
                ["device"] = device.ToJson()
            };
            var result = await RequestAsync("auth/login", "POST", body, false, null, null,
                AuthTokenResponse.Parse, cancellationToken).ConfigureAwait(false);
            _tokens.Save(result);
            return result;
        }

        public Task LogoutAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            return RequestAsync<object>("auth/logout", "POST", null, true, null, null, null, cancellationToken);
        }

        public Task<LogoutAllResponse> LogoutAllAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RequestAsync("auth/logout-all", "POST", null, true, null, null,
                LogoutAllResponse.Parse, cancellationToken);
        }

        public Task ChangePasswordAsync(
            string currentPassword, string newPassword,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var body = new JObject
            {
                ["currentPassword"] = currentPassword,
                ["newPassword"] = newPassword
            };
            return RequestAsync<object>("auth/change-password", "POST", body, true, null, null, null, cancellationToken);
        }

        // ---------- 账号与设备 ----------

        public Task<MeResponse> GetMeAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            return RequestAsync("me", "GET", null, true, null, null, MeResponse.Parse, cancellationToken);
        }

        public Task<DeleteAccountResponse> DeleteAccountAsync(
            string currentPassword, CancellationToken cancellationToken = default(CancellationToken))
        {
            var body = new JObject
            {
                ["currentPassword"] = currentPassword,
                ["confirmation"] = "DELETE"
            };
            return RequestAsync("me", "DELETE", body, true, null, null,
                DeleteAccountResponse.Parse, cancellationToken);
        }

        public Task<DeviceListResponse> ListDevicesAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RequestAsync("devices", "GET", null, true, null, null,
                DeviceListResponse.Parse, cancellationToken);
        }

        public Task RenameDeviceAsync(
            string deviceId, string name, CancellationToken cancellationToken = default(CancellationToken))
        {
            var body = new JObject { ["name"] = name };
            return RequestAsync<object>("devices/" + Uri.EscapeDataString(deviceId), "PATCH", body,
                true, null, null, null, cancellationToken);
        }

        public Task DeleteDeviceAsync(
            string deviceId, CancellationToken cancellationToken = default(CancellationToken))
        {
            return RequestAsync<object>("devices/" + Uri.EscapeDataString(deviceId), "DELETE", null,
                true, null, null, null, cancellationToken);
        }

        // ---------- 保险库 ----------

        public Task<VaultWriteResponse> CreateVaultAsync(
            VaultKeyEnvelopeData envelope, string idempotencyKey,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RequestAsync("vault", "POST", envelope.ToJson(), true, idempotencyKey, null,
                VaultWriteResponse.Parse, cancellationToken);
        }

        public Task<VaultEnvelopeResponse> GetVaultKeyEnvelopeAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RequestAsync("vault/key-envelope", "GET", null, true, null, null,
                VaultEnvelopeResponse.Parse, cancellationToken);
        }

        public Task<VaultWriteResponse> UpdateVaultKeyEnvelopeAsync(
            string currentPassword, VaultKeyEnvelopeData envelope,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var body = new JObject { ["currentPassword"] = currentPassword };
            foreach (var property in envelope.ToJson().Properties())
            {
                body[property.Name] = property.Value;
            }
            return RequestAsync("vault/key-envelope", "PUT", body, true, null, null,
                VaultWriteResponse.Parse, cancellationToken);
        }

        public Task<RotateVaultResponse> RotateVaultAsync(
            string currentPassword, VaultKeyEnvelopeData envelope, EncryptedDocumentData document,
            string baseRevision, string idempotencyKey,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var body = new JObject
            {
                ["currentPassword"] = currentPassword,
                ["keyEnvelope"] = envelope.ToJson(),
                ["document"] = document.ToJson()
            };
            return RequestAsync("vault/rotate", "POST", body, true, idempotencyKey, baseRevision,
                RotateVaultResponse.Parse, cancellationToken);
        }

        public Task DeleteVaultAsync(
            string currentPassword, CancellationToken cancellationToken = default(CancellationToken))
        {
            var body = new JObject { ["currentPassword"] = currentPassword };
            return RequestAsync<object>("vault", "DELETE", body, true, null, null, null, cancellationToken);
        }

        // ---------- 同步文档与历史 ----------

        public Task<SyncDocumentResponse> GetSyncDocumentAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RequestAsync("sync/document", "GET", null, true, null, null,
                SyncDocumentResponse.Parse, cancellationToken);
        }

        // HEAD sync/document：只关心响应头。404 且 CodeUnknown（HEAD 无响应体）时
        // 按 §2.4.7 回退 GET 拿权威 data.code；GET 成功则以 GET 内容作为 head。
        public async Task<SyncDocumentHead> HeadSyncDocumentAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            HttpResponseData response;
            try
            {
                response = await RequestWithPolicyAsync("sync/document", "HEAD", null, true,
                    null, null, cancellationToken).ConfigureAwait(false);
            }
            catch (ApiError error) when (error.Status == 404 && error.CodeUnknown)
            {
                return await SyncDocumentHeadViaGetAsync(cancellationToken).ConfigureAwait(false);
            }
            var revision = ParseRevisionHeader(response.Header("x-sync-revision"));
            int keyVersion;
            if (!int.TryParse(response.Header("x-key-version"), NumberStyles.None,
                    CultureInfo.InvariantCulture, out keyVersion)
                || keyVersion < 1
                || string.IsNullOrEmpty(response.Header("etag")))
            {
                throw new ApiError(ApiErrorKind.Protocol, ApiError.CodeSyncMetadataInvalid,
                    "服务器返回了无效的同步元数据",
                    status: response.StatusCode, requestId: response.Header("x-request-id"));
            }
            return new SyncDocumentHead
            {
                Revision = revision,
                KeyVersion = keyVersion,
                Etag = response.Header("etag"),
                LastModified = response.Header("last-modified")
            };
        }

        // 用 GET 代替 HEAD 得到同步元数据；文档/保险库不存在时抛出带准确 data.code 的错误
        private async Task<SyncDocumentHead> SyncDocumentHeadViaGetAsync(
            CancellationToken cancellationToken)
        {
            var document = await GetSyncDocumentAsync(cancellationToken).ConfigureAwait(false);
            var revision = ParseRevisionHeader(document.Revision);
            if (document.KeyVersion < 1)
            {
                throw new ApiError(ApiErrorKind.Protocol, ApiError.CodeSyncMetadataInvalid,
                    "服务器返回了无效的同步元数据");
            }
            return new SyncDocumentHead
            {
                Revision = revision,
                KeyVersion = document.KeyVersion,
                Etag = FormatRevisionEtag(revision),
                LastModified = null
            };
        }

        private static string ParseRevisionHeader(string value)
        {
            if (value == null || !Regex.IsMatch(value, @"^(0|[1-9]\d*)$"))
            {
                throw new ApiError(ApiErrorKind.Protocol, ApiError.CodeSyncRevisionInvalid,
                    "服务器返回了无效的同步 revision");
            }
            return value;
        }

        public Task<SyncWriteResponse> PutSyncDocumentAsync(
            EncryptedDocumentData document, string baseRevision, string idempotencyKey,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RequestAsync("sync/document", "PUT", document.ToJson(), true, idempotencyKey,
                baseRevision, SyncWriteResponse.Parse, cancellationToken);
        }

        public Task<RevisionListResponse> ListRevisionsAsync(
            int limit, string beforeRevision = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var path = "sync/revisions?limit=" + limit.ToString(CultureInfo.InvariantCulture);
            if (beforeRevision != null)
            {
                path += "&beforeRevision=" + Uri.EscapeDataString(beforeRevision);
            }
            return RequestAsync(path, "GET", null, true, null, null,
                RevisionListResponse.Parse, cancellationToken);
        }

        public Task<DeleteRevisionsResponse> DeleteRevisionsAsync(
            string baseRevision, CancellationToken cancellationToken = default(CancellationToken))
        {
            return RequestAsync("sync/revisions", "DELETE", null, true, null, baseRevision,
                DeleteRevisionsResponse.Parse, cancellationToken);
        }

        public Task<RestoreRevisionResponse> RestoreRevisionAsync(
            string revision, string baseRevision, string idempotencyKey,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return RequestAsync("sync/revisions/" + revision + "/restore", "POST", null, true,
                idempotencyKey, baseRevision, RestoreRevisionResponse.Parse, cancellationToken);
        }

        // ---------- 核心请求管线（§2.4 发送策略） ----------

        private async Task<T> RequestAsync<T>(
            string path, string method, JObject body, bool auth,
            string idempotencyKey, string ifMatchRevision,
            Func<JObject, T> parse, CancellationToken cancellationToken)
        {
            var response = await RequestWithPolicyAsync(path, method, body, auth,
                idempotencyKey, ifMatchRevision, cancellationToken).ConfigureAwait(false);
            return ReadSuccess(response, parse);
        }

        // 完整的鉴权重试策略：401 单飞刷新后重放；终端鉴权错误清 token
        private async Task<HttpResponseData> RequestWithPolicyAsync(
            string path, string method, JObject body, bool auth,
            string idempotencyKey, string ifMatchRevision, CancellationToken cancellationToken)
        {
            var first = await SendWithPolicyAsync(path, method, body, auth,
                idempotencyKey, ifMatchRevision, cancellationToken).ConfigureAwait(false);
            if (auth && first.Error != null && ShouldRefresh(first.Error))
            {
                await RefreshTokensAsync().ConfigureAwait(false);
                var retried = await SendWithPolicyAsync(path, method, body, auth,
                    idempotencyKey, ifMatchRevision, cancellationToken).ConfigureAwait(false);
                if (retried.Error != null)
                {
                    ClearForTerminalAuthError(retried.Error);
                    throw retried.Error;
                }
                return retried.Response;
            }
            if (first.Error != null)
            {
                ClearForTerminalAuthError(first.Error);
                throw first.Error;
            }
            return first.Response;
        }

        // §2.4.1/2：可重试请求（GET/HEAD/带幂等键）最多 retryLimit+1 次；
        // 可重试错误（network/timeout/429/≥500）按 RetryAfterMs 或 250×2^attempt（封顶 4000）退避
        private async Task<SendResult> SendWithPolicyAsync(
            string path, string method, JObject body, bool auth,
            string idempotencyKey, string ifMatchRevision, CancellationToken cancellationToken)
        {
            bool retryable = method == "GET" || method == "HEAD" || idempotencyKey != null;
            int attempts = retryable ? _retryLimit + 1 : 1;
            ApiError lastError = null;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                var result = await SendOnceAsync(path, method, body, auth,
                    idempotencyKey, ifMatchRevision, cancellationToken).ConfigureAwait(false);
                if (result.Error == null
                    && result.Response.StatusCode >= 200 && result.Response.StatusCode <= 299)
                {
                    return result;
                }
                var error = result.Error ?? ErrorFromResponse(result.Response);
                if (!IsRetriable(error) || attempt + 1 >= attempts)
                {
                    return SendResult.Failed(error);
                }
                lastError = error;
                long delay = error.RetryAfterMs ?? Math.Min(4000L, 250L * (1L << attempt));
                await _sleep(delay).ConfigureAwait(false);
            }
            return SendResult.Failed(lastError);
        }

        // 单次发送：只映射传输层异常与「未登录」；HTTP 非 2xx 由调用方按策略处理
        private async Task<SendResult> SendOnceAsync(
            string path, string method, JObject body, bool auth,
            string idempotencyKey, string ifMatchRevision, CancellationToken cancellationToken)
        {
            var url = Endpoint(path);
            var headers = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Accept", "application/json"),
                new KeyValuePair<string, string>("X-Request-Id", Guid.NewGuid().ToString())
            };
            string serialized = null;
            if (body != null)
            {
                serialized = body.ToString(Newtonsoft.Json.Formatting.None);
                headers.Add(new KeyValuePair<string, string>("Content-Type", "application/json"));
            }
            if (idempotencyKey != null)
            {
                headers.Add(new KeyValuePair<string, string>("Idempotency-Key", idempotencyKey));
            }
            if (ifMatchRevision != null)
            {
                headers.Add(new KeyValuePair<string, string>("If-Match", FormatRevisionEtag(ifMatchRevision)));
            }
            if (auth)
            {
                var tokens = _tokens.GetTokens();
                var accessToken = tokens != null ? tokens.AccessToken : null;
                if (string.IsNullOrEmpty(accessToken))
                {
                    return SendResult.Failed(new ApiError(
                        ApiErrorKind.Authentication, ApiError.CodeAuthRequired, "尚未登录"));
                }
                headers.Add(new KeyValuePair<string, string>("Authorization", "Bearer " + accessToken));
            }

            var request = new HttpRequestData(method, url, headers, serialized, _timeoutMs);
            try
            {
                return SendResult.Ok(
                    await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw; // 调用方取消，原样传播
            }
            catch (TimeoutException ex)
            {
                return SendResult.Failed(new ApiError(
                    ApiErrorKind.Timeout, ApiError.CodeRequestTimeout, "服务器请求超时",
                    ambiguous: method != "GET" && method != "HEAD", inner: ex));
            }
            catch (Exception ex)
            {
                Exception failure = ex;
                bool fallbackPending = false;
                // 评审（PR #1）：回退本身照旧粘滞切换（后续请求走 http），但**当次请求**只在
                // 重发安全时才立即经 http 重放：GET/HEAD、带 Idempotency-Key（服务端去重，
                // 03 §2.4）、或传输层确认请求根本没发出去（HttpConnectionFailedException）。
                // 其余写请求（如注册、登录、无幂等键的 POST）在 https 上结果不明——可能已被
                // 服务器执行、只是响应丢了——重放会执行两次，故直接按 ambiguous network 错误返回。
                bool fellBack = TryActivateHttpFallback(url);
                if (fellBack && !IsSafeToReplay(method, idempotencyKey, ex))
                {
                    // fix/login-feedback：已切到 http 但本次不重放——标记出来，App 提示用户再点一次。
                    fallbackPending = true;
                }
                else if (fellBack)
                {
                    var retry = new HttpRequestData(method, Endpoint(path), headers, serialized, _timeoutMs);
                    try
                    {
                        return SendResult.Ok(
                            await _transport.SendAsync(retry, cancellationToken).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (TimeoutException tex)
                    {
                        return SendResult.Failed(new ApiError(
                            ApiErrorKind.Timeout, ApiError.CodeRequestTimeout, "服务器请求超时",
                            ambiguous: method != "GET" && method != "HEAD", inner: tex));
                    }
                    catch (Exception ex2)
                    {
                        failure = ex2;
                    }
                }
                return SendResult.Failed(new ApiError(
                    ApiErrorKind.Network, ApiError.CodeNetworkError, "无法连接同步服务器",
                    ambiguous: method != "GET" && method != "HEAD", inner: failure,
                    httpFallbackActivated: fallbackPending));
            }
        }

        // fix/persist-login：传输失败是否发生在「请求字节一定还没发出」的连接阶段
        // （异常链上有 HttpConnectionFailedException，含 https→http 回退后重放仍连不上）。
        internal static bool IsRequestNotSent(Exception error)
        {
            for (Exception current = error; current != null; current = current.InnerException)
            {
                if (current is HttpConnectionFailedException)
                {
                    return true;
                }
            }
            return false;
        }

        // https 失败后能否在 http 上立即重发当次请求（见 SendOnceAsync 注释）。
        internal static bool IsSafeToReplay(string method, string idempotencyKey, Exception failure)
        {
            if (method == "GET" || method == "HEAD" || idempotencyKey != null)
            {
                return true;
            }
            return failure is HttpConnectionFailedException;
        }

        // https 请求在传输层失败时切到 http 根（只切一次）。返回 true 表示调用方应
        // 立即用新根重发：本次请求是发往 https 主根的，且回退已就位（可能是并发请求刚切的）。
        private bool TryActivateHttpFallback(string failedUrl)
        {
            if (_fallbackRoot == null || failedUrl == null
                || !failedUrl.StartsWith(_primaryRoot, StringComparison.Ordinal))
            {
                return false;
            }
            bool raised = false;
            lock (_fallbackLock)
            {
                if (!ReferenceEquals(_root, _fallbackRoot))
                {
                    _root = _fallbackRoot;
                    raised = true;
                }
            }
            if (raised)
            {
                var handler = HttpFallbackActivated;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
            return true;
        }

        private static bool IsRetriable(ApiError error)
        {
            return error.Kind == ApiErrorKind.Network
                || error.Kind == ApiErrorKind.Timeout
                || error.Status == 429
                || (error.Status != null && error.Status.Value >= 500);
        }

        // HEAD 请求拿不到响应体，401 时读不出 AUTH_TOKEN_EXPIRED；带着有效 token 收到 401
        // 本身就说明 accessToken 已失效，此时按 §2.4.4 直接刷新重试
        private static bool ShouldRefresh(ApiError error)
        {
            return error.Status == 401
                && (error.Code == "AUTH_TOKEN_EXPIRED" || error.CodeUnknown);
        }

        // §2.4.6 终端鉴权错误 → 清空本地会话
        private void ClearForTerminalAuthError(ApiError error)
        {
            if (error.Code == "AUTH_DEVICE_REVOKED"
                || error.Code == "AUTH_TOKEN_REUSED"
                || (error.Code == "AUTH_TOKEN_EXPIRED" && error.Status == 401))
            {
                _tokens.Clear();
            }
        }

        // ---------- 401 刷新（§2.4.5，单飞） ----------

        private Task RefreshTokensAsync()
        {
            lock (_refreshLock)
            {
                if (_refreshTask != null)
                {
                    return _refreshTask;
                }
                var task = PerformRefreshAsync();
                _refreshTask = task;
                // 完成后清槽。注意不能用「执行体 finally 清槽」：同步完成时 finally 先于
                // 上面的赋值执行，会把故障任务留在槽里（S07 测试抓获）。
                var _ = ClearRefreshSlotWhenDoneAsync(task);
                return task;
            }
        }

        private async Task ClearRefreshSlotWhenDoneAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 刷新失败由等待方处理，这里只负责清槽
            }
            lock (_refreshLock)
            {
                if (ReferenceEquals(_refreshTask, task))
                {
                    _refreshTask = null;
                }
            }
        }

        // 刷新：每次刷新只发一次，绝不当场重试（§2.4.5）。
        // fix/persist-login：「登录状态需要保存」——此前刷新只要遇到一次网络/超时失败就标
        // refreshUncertain，之后下一次 401 不再发刷新、直接清空会话。accessToken 只有 15 分钟
        // （docs api-v1 §1.2），前台 60 s 轮询下每 15 分钟就要刷新一次，Lumia 上一次信号抖动 /
        // 挂起打断就等于被强制登出（保险库也随之上锁）。现在：
        //   - 无 token → 清空并抛 AUTH_REFRESH_UNAVAILABLE（未变）；
        //   - refreshUncertain 不再门控：仍用手里的 refreshToken 试一次，由服务端裁决——
        //     上次请求其实没送达（最常见）→ 正常轮换、保持登录；确已被消耗 → 服务端回
        //     401 AUTH_TOKEN_REUSED / AUTH_TOKEN_EXPIRED，按终端鉴权清空（与旧行为同一结局）。
        //     重用检测撤销的 token family 只是本设备这次登录的那一串（logout-all 才动其他设备），
        //     本地本来就要丢弃它，不会波及其他设备；
        //   - 网络/超时失败一律不清会话；连接阶段失败（HttpConnectionFailedException：
        //     DNS/建连/TLS，请求字节一定没发出）连 uncertain 都不标。
        private async Task PerformRefreshAsync()
        {
            var tokens = _tokens.GetTokens();
            if (tokens == null || string.IsNullOrEmpty(tokens.RefreshToken))
            {
                _tokens.Clear();
                throw new ApiError(ApiErrorKind.Authentication, ApiError.CodeAuthRefreshUnavailable,
                    "登录已失效，请重新登录");
            }
            var result = await SendOnceAsync("auth/refresh", "POST",
                new JObject { ["refreshToken"] = tokens.RefreshToken }, false, null, null,
                CancellationToken.None).ConfigureAwait(false);
            if (result.Error != null)
            {
                var error = result.Error;
                bool notSent = IsRequestNotSent(error);
                if (!notSent)
                {
                    // 结果不明：refreshToken 可能已被服务端消耗（仅记录，下次照样由服务端裁决）
                    _tokens.MarkRefreshUncertain();
                }
                throw new ApiError(error.Kind, error.Code, error.Message,
                    status: error.Status, requestId: error.RequestId,
                    retryAfterMs: error.RetryAfterMs, ambiguous: !notSent, inner: error);
            }
            if (result.Response.StatusCode < 200 || result.Response.StatusCode > 299)
            {
                var error = ErrorFromResponse(result.Response);
                ClearForTerminalAuthError(error);
                throw error;
            }
            var refreshed = ReadSuccess(result.Response,
                new Func<JObject, AuthTokenResponse>(AuthTokenResponse.Parse));
            _tokens.Save(refreshed);
        }

        private sealed class SendResult
        {
            public HttpResponseData Response;
            public ApiError Error;

            public static SendResult Ok(HttpResponseData response)
            {
                return new SendResult { Response = response };
            }

            public static SendResult Failed(ApiError error)
            {
                return new SendResult { Error = error };
            }
        }

        private ApiError ErrorFromResponse(HttpResponseData response)
        {
            JObject body = null;
            if (!string.IsNullOrEmpty(response.Body))
            {
                try
                {
                    body = JsonText.ParseObject(response.Body);
                }
                catch (Exception)
                {
                    // 代理可能返回 HTML；状态码本身仍有意义
                }
            }
            var data = body != null ? body["data"] as JObject : null;
            var codeToken = data != null ? data["code"] : null;
            bool hasCode = codeToken != null && codeToken.Type == JTokenType.String;
            string code = hasCode
                ? (string)codeToken
                : response.StatusCode == 429 ? "RATE_LIMITED" : "HTTP_" + response.StatusCode;

            string message = null;
            bool messageFromServer = true;
            var dataMessage = data != null ? data["message"] : null;
            if (dataMessage != null && dataMessage.Type == JTokenType.String)
            {
                message = (string)dataMessage;
            }
            else if (body != null && body["statusMessage"] != null && body["statusMessage"].Type == JTokenType.String)
            {
                message = (string)body["statusMessage"];
            }
            else if (body != null && body["message"] != null && body["message"].Type == JTokenType.String)
            {
                message = (string)body["message"];
            }
            if (message == null)
            {
                // fix/functional-pass（P2-2）：无服务端文案时只给英文诊断；App 对
                // MessageFromServer=false 且无 Api_<CODE> 键的错误显示本地化「服务器请求失败（status）」。
                messageFromServer = false;
                message = "HTTP " + response.StatusCode.ToString(CultureInfo.InvariantCulture);
            }

            return new ApiError(
                response.StatusCode == 401 ? ApiErrorKind.Authentication : ApiErrorKind.Http,
                code,
                message,
                status: response.StatusCode,
                requestId: response.Header("x-request-id"),
                retryAfterMs: ParseRetryAfter(response.Header("retry-after"), _clock()),
                codeUnknown: !hasCode,
                messageFromServer: messageFromServer);
        }

        private T ReadSuccess<T>(HttpResponseData response, Func<JObject, T> parse)
        {
            if (response.StatusCode == 204)
            {
                return default(T);
            }
            JObject json;
            try
            {
                json = JsonText.ParseObject(response.Body ?? "");
            }
            catch (Exception ex)
            {
                throw ResponseInvalid(response, "服务器返回了无效 JSON", ex);
            }
            if (parse == null)
            {
                return default(T);
            }
            try
            {
                return parse(json);
            }
            catch (ProtocolParseException ex)
            {
                throw ResponseInvalid(response, "服务器响应不符合协议", ex);
            }
        }

        private static ApiError ResponseInvalid(HttpResponseData response, string message, Exception inner)
        {
            return new ApiError(ApiErrorKind.Protocol, ApiError.CodeResponseInvalid, message,
                status: response.StatusCode, requestId: response.Header("x-request-id"), inner: inner);
        }

        internal static long? ParseRetryAfter(string value, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }
            var trimmed = value.Trim();
            long seconds;
            if (long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out seconds))
            {
                return seconds * 1000;
            }
            DateTimeOffset date;
            if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                return Math.Max(0L, (long)(date - now).TotalMilliseconds);
            }
            return null;
        }

        private static string NormalizeRoot(string baseUrl, bool allowHttp)
        {
            if (string.IsNullOrEmpty(baseUrl))
            {
                throw new ArgumentException("API 地址不能为空", nameof(baseUrl));
            }
            Uri uri;
            try
            {
                uri = new Uri(baseUrl, UriKind.Absolute);
            }
            catch (UriFormatException ex)
            {
                throw new ArgumentException("API 地址不是合法 URL", nameof(baseUrl), ex);
            }
            if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new ArgumentException("API 地址不能包含凭据、查询参数或片段", nameof(baseUrl));
            }
            // Uri.Scheme 已规范化为小写
            bool https = uri.Scheme == "https";
            bool http = uri.Scheme == "http";
            if (!https && !(allowHttp && http))
            {
                throw new ArgumentException("API 必须使用 HTTPS，或显式允许 HTTP", nameof(baseUrl));
            }
            var path = uri.AbsolutePath.TrimEnd('/');
            if (!path.EndsWith(ApiPathPrefix, StringComparison.Ordinal))
            {
                path += ApiPathPrefix;
            }
            var port = uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
            return uri.Scheme + "://" + uri.Host + port + path + "/";
        }

        private string Endpoint(string path)
        {
            var queryIndex = path == null ? -1 : path.IndexOf('?');
            var pure = queryIndex >= 0 ? path.Substring(0, queryIndex) : path;
            if (string.IsNullOrEmpty(pure)
                || pure.StartsWith("/", StringComparison.Ordinal)
                || SchemePattern.IsMatch(pure)
                || Array.IndexOf(pure.Split('/'), "..") >= 0)
            {
                throw new ArgumentException("API 路径必须是安全的相对路径", nameof(path));
            }
            return _root + path;
        }
    }
}
