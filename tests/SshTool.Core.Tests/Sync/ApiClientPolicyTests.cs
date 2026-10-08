using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SshTool.Core.Sync.Api;
using SshTool.Core.Sync.Api.Dtos;
using SshTool.Core.Tests.Fakes;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // 03-SYNC-PROTOCOL.md §10.1 ApiClient 用例 3–8（§2.4 发送策略）：
    // 401 单飞刷新与重放、HEAD 404 回退、刷新失败 uncertain、Retry-After 与重试判定。
    public class ApiClientPolicyTests
    {
        private const string MeJson = @"{""user"":{""id"":""u1"",""email"":""a@b.c""},""deviceId"":""d1""}";
        private const string RotatedTokensJson =
            @"{""accessToken"":""a-2"",""refreshToken"":""r-2"",""expiresIn"":3600," +
            @"""user"":{""id"":""u1"",""email"":""a@b.c""},""device"":{""id"":""d1"",""name"":""Lumia""}}";

        private static HttpResponseData Json(
            int status, string body, IDictionary<string, string> headers = null)
        {
            var map = headers != null
                ? new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return new HttpResponseData(status, map, body);
        }

        private static HttpResponseData HeadOk(
            string revision = "12", string keyVersion = "2", string etag = "\"revision-12\"")
        {
            var headers = new Dictionary<string, string>();
            if (revision != null) headers["x-sync-revision"] = revision;
            if (keyVersion != null) headers["x-key-version"] = keyVersion;
            if (etag != null) headers["etag"] = etag;
            headers["last-modified"] = "Thu, 17 Sep 2026 08:00:00 GMT";
            return new HttpResponseData(200, headers, null);
        }

        private static string HeaderOf(HttpRequestData request, string name)
        {
            foreach (var header in request.Headers)
            {
                if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return header.Value;
                }
            }
            return null;
        }

        // ---------- 用例 3：刷新一次、保存轮换 token、重放请求；并发 401 单飞 ----------

        [Fact]
        public async Task Refresh_401TokenExpired_RefreshesSavesAndReplays()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(401,
                @"{""statusCode"":401,""data"":{""code"":""AUTH_TOKEN_EXPIRED"",""message"":""过期""}}"));
            transport.Enqueue(Json(200, RotatedTokensJson));
            transport.Enqueue(Json(200, MeJson));
            var tokens = FakeTokenStore.SignedIn();
            var client = new ApiClient("https://sync.example.com", tokens, transport);

            var me = await client.GetMeAsync();

            Assert.Equal("u1", me.User.Id);
            Assert.Equal(3, transport.Requests.Count);

            var refresh = transport.Requests[1];
            Assert.Equal("POST", refresh.Method);
            Assert.Equal("https://sync.example.com/api/v1/auth/refresh", refresh.Url);
            Assert.Contains(@"""refreshToken"":""refresh-1""", refresh.Body);
            Assert.Null(HeaderOf(refresh, "Authorization")); // 刷新不带 Bearer

            // 保存了轮换后的 token，重放用新 accessToken
            Assert.Equal("a-2", tokens.Saved.AccessToken);
            Assert.Equal("Bearer a-2", HeaderOf(transport.Requests[2], "Authorization"));
        }

        [Fact]
        public async Task Refresh_Concurrent401_SingleFlight()
        {
            // 确定性时序：后到的初始请求必须等「刷新已发出」才拿到 401，
            // 刷新完成由测试显式放行 —— 保证第二个请求到达刷新点时单飞任务仍在进行。
            var refreshStarted = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var proceedRefresh = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            var transport = new FakeHttpTransport();
            int refreshCount = 0;
            int initials = 0;
            transport.AsyncHandler = async request =>
            {
                if (request.Url.Contains("auth/refresh"))
                {
                    refreshCount++;
                    refreshStarted.TrySetResult(null);
                    await proceedRefresh.Task.ConfigureAwait(false);
                    return Json(200, RotatedTokensJson);
                }
                var authorization = HeaderOf(request, "Authorization");
                if (authorization == "Bearer access-1")
                {
                    if (System.Threading.Interlocked.Increment(ref initials) == 2)
                    {
                        await refreshStarted.Task.ConfigureAwait(false);
                    }
                    return Json(401, null); // 无响应体 → CodeUnknown
                }
                return request.Url.Contains("devices")
                    ? Json(200, @"{""items"":[]}")
                    : Json(200, MeJson);
            };
            var client = new ApiClient("https://sync.example.com", FakeTokenStore.SignedIn(), transport);

            var first = client.GetMeAsync();
            var second = client.ListDevicesAsync();
            await refreshStarted.Task; // 发起方的刷新已进 handler（单飞槽已占）
            await Task.Delay(200);     // 让另一方的 401 延续链跑完并 join 同一个刷新任务
            proceedRefresh.SetResult(null);
            await Task.WhenAll(first, second);

            Assert.Equal(1, refreshCount); // 两个 401 只刷新一次
            // 初次用旧 token，重放用轮换后的新 token
            Assert.Equal(2, transport.Requests.Count(r => HeaderOf(r, "Authorization") == "Bearer access-1"));
            Assert.Equal(2, transport.Requests.Count(r => HeaderOf(r, "Authorization") == "Bearer a-2"));
        }

        [Fact]
        public async Task TerminalAuthError_DeviceRevoked_ClearsTokensWithoutRefresh()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(401,
                @"{""statusCode"":401,""data"":{""code"":""AUTH_DEVICE_REVOKED"",""message"":""设备已撤销""}}"));
            var tokens = FakeTokenStore.SignedIn();
            var client = new ApiClient("https://sync.example.com", tokens, transport);

            var error = await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());

            Assert.Equal("AUTH_DEVICE_REVOKED", error.Code);
            Assert.True(tokens.Cleared);
            Assert.Single(transport.Requests); // 非 EXPIRED/CodeUnknown → 不触发刷新
        }

        [Fact]
        public async Task Refresh_TokenReused_ClearsTokens()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(401,
                @"{""statusCode"":401,""data"":{""code"":""AUTH_TOKEN_EXPIRED"",""message"":""过期""}}"));
            transport.Enqueue(Json(401,
                @"{""statusCode"":401,""data"":{""code"":""AUTH_TOKEN_REUSED"",""message"":""refreshToken 重用""}}"));
            var tokens = FakeTokenStore.SignedIn();
            var client = new ApiClient("https://sync.example.com", tokens, transport);

            var error = await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());

            Assert.Equal("AUTH_TOKEN_REUSED", error.Code);
            Assert.True(tokens.Cleared);
            Assert.Equal(2, transport.Requests.Count); // 原请求 + 刷新，无重放
        }

        [Fact]
        public async Task Replay_Still401Expired_ClearsTokens()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(401,
                @"{""statusCode"":401,""data"":{""code"":""AUTH_TOKEN_EXPIRED"",""message"":""过期""}}"));
            transport.Enqueue(Json(200, RotatedTokensJson));
            transport.Enqueue(Json(401,
                @"{""statusCode"":401,""data"":{""code"":""AUTH_TOKEN_EXPIRED"",""message"":""仍过期""}}"));
            var tokens = FakeTokenStore.SignedIn();
            var client = new ApiClient("https://sync.example.com", tokens, transport);

            var error = await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());

            Assert.Equal("AUTH_TOKEN_EXPIRED", error.Code);
            Assert.True(tokens.Cleared);
            Assert.Equal(3, transport.Requests.Count); // 只重放一次
        }

        // ---------- 用例 4/5：HEAD 404 CodeUnknown → GET 回退 ----------

        [Fact]
        public async Task Head_404CodeUnknown_FallsBackToGet_DocumentNotFound()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(404, null)); // HEAD 无响应体
            transport.Enqueue(Json(404,
                @"{""statusCode"":404,""data"":{""code"":""SYNC_DOCUMENT_NOT_FOUND"",""message"":""文档不存在""}}"));
            var client = new ApiClient("https://sync.example.com", FakeTokenStore.SignedIn(), transport);

            var error = await Assert.ThrowsAsync<ApiError>(() => client.HeadSyncDocumentAsync());

            Assert.Equal("SYNC_DOCUMENT_NOT_FOUND", error.Code);
            Assert.False(error.CodeUnknown);
            Assert.Equal("GET", transport.Requests[1].Method);
        }

        [Fact]
        public async Task Head_404CodeUnknown_FallsBackToGet_VaultNotFound()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(404, null));
            transport.Enqueue(Json(404,
                @"{""statusCode"":404,""data"":{""code"":""VAULT_NOT_FOUND"",""message"":""保险库不存在""}}"));
            var client = new ApiClient("https://sync.example.com", FakeTokenStore.SignedIn(), transport);

            var error = await Assert.ThrowsAsync<ApiError>(() => client.HeadSyncDocumentAsync());

            Assert.Equal("VAULT_NOT_FOUND", error.Code);
        }

        [Fact]
        public async Task Head_Success_ParsesHeaders()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(HeadOk());
            var client = new ApiClient("https://sync.example.com", FakeTokenStore.SignedIn(), transport);

            var head = await client.HeadSyncDocumentAsync();

            Assert.Equal("12", head.Revision);
            Assert.Equal(2, head.KeyVersion);
            Assert.Equal("\"revision-12\"", head.Etag);
            Assert.Equal("Thu, 17 Sep 2026 08:00:00 GMT", head.LastModified);
        }

        [Fact]
        public async Task Head_GetFallbackSuccess_UsesGetMetadata()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(404, null)); // HEAD
            transport.Enqueue(Json(200,
                @"{""revision"":""7"",""schemaVersion"":1,""keyVersion"":3,""algorithm"":""AES-256-GCM""," +
                @"""nonce"":""bg=="",""ciphertext"":""Yw=="",""ciphertextHash"":""aA==""," +
                @"""updatedByDeviceId"":""d1"",""updatedAt"":""2026-09-17T08:00:00.000Z""}"));
            var client = new ApiClient("https://sync.example.com", FakeTokenStore.SignedIn(), transport);

            var head = await client.HeadSyncDocumentAsync();

            Assert.Equal("7", head.Revision);
            Assert.Equal(3, head.KeyVersion);
            Assert.Equal("\"revision-7\"", head.Etag); // ETag 由 revision 重构
            Assert.Null(head.LastModified);
        }

        [Fact]
        public async Task Head_InvalidMetadata_Throws()
        {
            // 缺 ETag → SYNC_METADATA_INVALID
            var transport = new FakeHttpTransport();
            transport.Enqueue(HeadOk(etag: null));
            var client = new ApiClient("https://sync.example.com", FakeTokenStore.SignedIn(), transport);
            var error = await Assert.ThrowsAsync<ApiError>(() => client.HeadSyncDocumentAsync());
            Assert.Equal(ApiError.CodeSyncMetadataInvalid, error.Code);
            Assert.Equal(ApiErrorKind.Protocol, error.Kind);

            // revision 形状非法 → SYNC_REVISION_INVALID
            var transport2 = new FakeHttpTransport();
            transport2.Enqueue(HeadOk(revision: "01"));
            var client2 = new ApiClient("https://sync.example.com", FakeTokenStore.SignedIn(), transport2);
            var error2 = await Assert.ThrowsAsync<ApiError>(() => client2.HeadSyncDocumentAsync());
            Assert.Equal(ApiError.CodeSyncRevisionInvalid, error2.Code);

            // keyVersion 非 ≥1 整数 → SYNC_METADATA_INVALID
            var transport3 = new FakeHttpTransport();
            transport3.Enqueue(HeadOk(keyVersion: "0"));
            var client3 = new ApiClient("https://sync.example.com", FakeTokenStore.SignedIn(), transport3);
            var error3 = await Assert.ThrowsAsync<ApiError>(() => client3.HeadSyncDocumentAsync());
            Assert.Equal(ApiError.CodeSyncMetadataInvalid, error3.Code);
        }

        // ---------- 用例 6：HEAD 无响应体 401 → 刷新而不是丢会话 ----------

        [Fact]
        public async Task Head_401CodeUnknown_RefreshesInsteadOfLosingSession()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(401, null)); // HEAD 无响应体，CodeUnknown
            transport.Enqueue(Json(200, RotatedTokensJson));
            transport.Enqueue(HeadOk());
            var tokens = FakeTokenStore.SignedIn();
            var client = new ApiClient("https://sync.example.com", tokens, transport);

            var head = await client.HeadSyncDocumentAsync();

            Assert.Equal("12", head.Revision);
            Assert.Equal("a-2", tokens.Saved.AccessToken);
            Assert.False(tokens.Cleared); // 没有丢会话
            Assert.Equal(3, transport.Requests.Count);
            Assert.Equal("Bearer a-2", HeaderOf(transport.Requests[2], "Authorization"));
        }

        // ---------- 用例 7：刷新网络失败 → 当次不重试、标记 uncertain、不清会话 ----------
        // fix/persist-login：下次 401 仍用同一个 refreshToken 试一次，由服务端裁决（见 PersistLoginTests）。

        [Fact]
        public async Task Refresh_NetworkFailure_NoRetryAndMarksUncertain()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(401,
                @"{""statusCode"":401,""data"":{""code"":""AUTH_TOKEN_EXPIRED"",""message"":""过期""}}"));
            transport.Enqueue(new TimeoutException("刷新超时"));
            var tokens = FakeTokenStore.SignedIn();
            var client = new ApiClient("https://sync.example.com", tokens, transport);

            var error = await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());

            Assert.Equal(ApiErrorKind.Timeout, error.Kind);
            Assert.True(error.Ambiguous); // refreshToken 可能已被消耗
            Assert.True(tokens.Uncertain);
            Assert.False(tokens.Cleared);
            Assert.Equal(2, transport.Requests.Count); // 刷新不重试

            // 下次：uncertain 不再直接清会话——用原 refreshToken 再试一次；上次其实没送达 → 轮换成功
            transport.Enqueue(Json(401,
                @"{""statusCode"":401,""data"":{""code"":""AUTH_TOKEN_EXPIRED"",""message"":""过期""}}"));
            transport.Enqueue(Json(200, RotatedTokensJson));
            transport.Enqueue(Json(200, MeJson));
            var me = await client.GetMeAsync();
            Assert.Equal("u1", me.User.Id);
            Assert.False(tokens.Cleared);
            Assert.False(tokens.Uncertain);
            Assert.Equal("a-2", tokens.Saved.AccessToken);
            Assert.Equal(5, transport.Requests.Count);
            Assert.Contains(@"""refreshToken"":""refresh-1""", transport.Requests[3].Body);
        }

        // ---------- 用例 8：遵守 Retry-After；可重试判定 ----------

        [Fact]
        public async Task Retry_429HonorsRetryAfter()
        {
            var delays = new List<long>();
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(429, null, new Dictionary<string, string> { ["retry-after"] = "2" }));
            transport.Enqueue(Json(200, MeJson));
            var client = new ApiClient("https://sync.example.com", FakeTokenStore.SignedIn(), transport,
                sleep: ms => { delays.Add(ms); return Task.CompletedTask; });

            await client.GetMeAsync();

            Assert.Equal(2, transport.Requests.Count);
            Assert.Equal(new[] { 2000L }, delays);
        }

        [Fact]
        public async Task Retry_500ExponentialBackoff()
        {
            var delays = new List<long>();
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(500, null));
            transport.Enqueue(Json(500, null));
            transport.Enqueue(Json(200, MeJson));
            var client = new ApiClient("https://sync.example.com", FakeTokenStore.SignedIn(), transport,
                sleep: ms => { delays.Add(ms); return Task.CompletedTask; });

            await client.GetMeAsync();

            Assert.Equal(3, transport.Requests.Count);
            Assert.Equal(new[] { 250L, 500L }, delays); // 250×2^0、250×2^1
        }

        [Fact]
        public async Task Retry_GivesUpAfterRetryLimit()
        {
            var delays = new List<long>();
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(500, null));
            transport.Enqueue(Json(500, null));
            transport.Enqueue(Json(500, null));
            var client = new ApiClient("https://sync.example.com", FakeTokenStore.SignedIn(), transport,
                sleep: ms => { delays.Add(ms); return Task.CompletedTask; });

            var error = await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());

            Assert.Equal("HTTP_500", error.Code);
            Assert.Equal(3, transport.Requests.Count); // retryLimit(2)+1
            Assert.Equal(2, delays.Count);
        }

        [Fact]
        public async Task Retry_NonIdempotentPost_NotRetried()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(500, null));
            var client = new ApiClient("https://sync.example.com", FakeTokenStore.SignedIn(), transport,
                sleep: _ => Task.CompletedTask);

            await Assert.ThrowsAsync<ApiError>(
                () => client.ChangePasswordAsync("old", "new")); // 无幂等键的 POST

            Assert.Single(transport.Requests);
        }

        [Fact]
        public async Task Retry_IdempotentPost_Retried()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(500, null));
            transport.Enqueue(Json(200, @"{""revision"":""13"",""updatedAt"":""2026-09-17T08:00:00.000Z""}"));
            var client = new ApiClient("https://sync.example.com", FakeTokenStore.SignedIn(), transport,
                sleep: _ => Task.CompletedTask);

            var result = await client.PutSyncDocumentAsync(new EncryptedDocumentData
            {
                SchemaVersion = 1,
                KeyVersion = 1,
                Algorithm = "AES-256-GCM",
                Nonce = "bg==",
                Ciphertext = "Yw==",
                CiphertextHash = "aA=="
            }, "12", "idem-1");

            Assert.Equal("13", result.Revision);
            Assert.Equal(2, transport.Requests.Count); // 带幂等键的写请求可重试
        }

        [Fact]
        public async Task Retry_NetworkError_RetriedForGet()
        {
            var delays = new List<long>();
            var transport = new FakeHttpTransport();
            transport.Enqueue(new TimeoutException("到时"));
            transport.Enqueue(Json(200, MeJson));
            var client = new ApiClient("https://sync.example.com", FakeTokenStore.SignedIn(), transport,
                sleep: ms => { delays.Add(ms); return Task.CompletedTask; });

            await client.GetMeAsync();

            Assert.Equal(2, transport.Requests.Count);
            Assert.Equal(new[] { 250L }, delays);
        }
    }
}
