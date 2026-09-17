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

        private readonly string _root;
        private readonly ITokenStore _tokens;
        private readonly IHttpTransport _transport;
        private readonly int _timeoutMs;
        private readonly Func<DateTimeOffset> _clock;

        public ApiClient(
            string baseUrl,
            ITokenStore tokenStore,
            IHttpTransport transport,
            bool allowHttp = false,
            int timeoutMs = 15000,
            Func<DateTimeOffset> clock = null)
        {
            if (tokenStore == null) throw new ArgumentNullException(nameof(tokenStore));
            if (transport == null) throw new ArgumentNullException(nameof(transport));
            _root = NormalizeRoot(baseUrl, allowHttp);
            _tokens = tokenStore;
            _transport = transport;
            _timeoutMs = timeoutMs;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        // 供测试与 S07 诊断使用：规范化后的 API 根地址（以 / 结尾）
        public string Root { get { return _root; } }

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

        // ---------- 核心请求管线 ----------

        private async Task<T> RequestAsync<T>(
            string path, string method, JObject body, bool auth,
            string idempotencyKey, string ifMatchRevision,
            Func<JObject, T> parse, CancellationToken cancellationToken)
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
                    throw new ApiError(ApiErrorKind.Authentication, ApiError.CodeAuthRequired, "尚未登录");
                }
                headers.Add(new KeyValuePair<string, string>("Authorization", "Bearer " + accessToken));
            }

            var request = new HttpRequestData(method, url, headers, serialized, _timeoutMs);
            HttpResponseData response;
            try
            {
                response = await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // 调用方取消，原样传播
            }
            catch (TimeoutException ex)
            {
                throw new ApiError(ApiErrorKind.Timeout, ApiError.CodeRequestTimeout, "服务器请求超时",
                    ambiguous: method != "GET" && method != "HEAD", inner: ex);
            }
            catch (Exception ex)
            {
                throw new ApiError(ApiErrorKind.Network, ApiError.CodeNetworkError, "无法连接同步服务器",
                    ambiguous: method != "GET" && method != "HEAD", inner: ex);
            }

            if (response.StatusCode < 200 || response.StatusCode > 299)
            {
                throw ErrorFromResponse(response);
            }
            return ReadSuccess(response, parse);
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
                message = "服务器请求失败（" + response.StatusCode + "）";
            }

            return new ApiError(
                response.StatusCode == 401 ? ApiErrorKind.Authentication : ApiErrorKind.Http,
                code,
                message,
                status: response.StatusCode,
                requestId: response.Header("x-request-id"),
                retryAfterMs: ParseRetryAfter(response.Header("retry-after"), _clock()),
                codeUnknown: !hasCode);
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
