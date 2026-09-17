using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.Core.Sync.Api;
using SshTool.Core.Sync.Api.Dtos;
using SshTool.Core.Tests.Fakes;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // 03-SYNC-PROTOCOL.md §10.1 ApiClient 用例 1（HTTP 许可与 Base URL 规范化）、
    // 用例 2（API 前缀与统一头）、用例 9（错误码稳定映射）。
    public class ApiClientTests
    {
        private const string Base = "https://sync.example.com";

        private sealed class FakeTokenStore : ITokenStore
        {
            public TokenPair Tokens;
            public AuthTokenResponse Saved;
            public bool Cleared;
            public bool Uncertain;

            public TokenPair GetTokens()
            {
                return Tokens;
            }

            public void Save(AuthTokenResponse tokens)
            {
                Saved = tokens;
                Tokens = new TokenPair(tokens.AccessToken, tokens.RefreshToken);
            }

            public void Clear()
            {
                Tokens = null;
                Cleared = true;
            }

            public bool CanRefresh
            {
                get { return !Uncertain; }
            }

            public void MarkRefreshUncertain()
            {
                Uncertain = true;
            }
        }

        private static ApiClient NewClient(
            FakeHttpTransport transport, FakeTokenStore tokens,
            bool allowHttp = false, string baseUrl = Base, Func<DateTimeOffset> clock = null)
        {
            return new ApiClient(baseUrl, tokens, transport, allowHttp: allowHttp, clock: clock);
        }

        private static FakeTokenStore SignedIn()
        {
            return new FakeTokenStore { Tokens = new TokenPair("access-1", "refresh-1") };
        }

        private static HttpResponseData Json(
            int status, string body, IDictionary<string, string> headers = null)
        {
            var map = headers != null
                ? new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return new HttpResponseData(status, map, body);
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

        private static EncryptedDocumentData SampleDocument()
        {
            return new EncryptedDocumentData
            {
                SchemaVersion = 1,
                KeyVersion = 1,
                Algorithm = "AES-256-GCM",
                Nonce = "bm9uY2U=",
                Ciphertext = "Y2lwaGVy",
                CiphertextHash = "aGFzaA=="
            };
        }

        // ---------- 用例 1：HTTP 许可与 Base URL 规范化 ----------

        [Fact]
        public void BaseUrl_HttpWithoutAllowHttp_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => new ApiClient("http://sync.example.com", new FakeTokenStore(), new FakeHttpTransport()));
        }

        [Fact]
        public void BaseUrl_HttpWithAllowHttp_Ok()
        {
            var client = new ApiClient(
                "http://sync.example.com", new FakeTokenStore(), new FakeHttpTransport(), allowHttp: true);
            Assert.Equal("http://sync.example.com/api/v1/", client.Root);
        }

        [Fact]
        public void BaseUrl_OtherScheme_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => new ApiClient("ftp://sync.example.com", new FakeTokenStore(), new FakeHttpTransport(), allowHttp: true));
        }

        [Theory]
        [InlineData("https://user:pass@sync.example.com")]   // 凭据
        [InlineData("https://sync.example.com?a=1")]         // 查询参数
        [InlineData("https://sync.example.com/#frag")]       // 片段
        [InlineData("不是合法URL")]                            // 非法 URL
        public void BaseUrl_Rejected(string baseUrl)
        {
            Assert.Throws<ArgumentException>(
                () => new ApiClient(baseUrl, new FakeTokenStore(), new FakeHttpTransport()));
        }

        [Theory]
        [InlineData("https://sync.example.com", "https://sync.example.com/api/v1/")]
        [InlineData("https://sync.example.com/", "https://sync.example.com/api/v1/")]
        [InlineData("https://sync.example.com/api/v1", "https://sync.example.com/api/v1/")]
        [InlineData("https://sync.example.com/api/v1/", "https://sync.example.com/api/v1/")]
        [InlineData("https://sync.example.com/sub/", "https://sync.example.com/sub/api/v1/")]
        [InlineData("https://sync.example.com:8443", "https://sync.example.com:8443/api/v1/")]
        public void BaseUrl_Normalized(string input, string expected)
        {
            var client = new ApiClient(input, new FakeTokenStore(), new FakeHttpTransport());
            Assert.Equal(expected, client.Root);
        }

        // ---------- 用例 2：API 前缀与统一头 ----------

        [Fact]
        public async Task PutSyncDocument_SendsUrlMethodAndHeaders()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(200, @"{""revision"":""13"",""updatedAt"":""2026-09-17T08:00:00.000Z""}",
                new Dictionary<string, string> { ["x-request-id"] = "srv-1" }));
            var client = NewClient(transport, SignedIn());

            var result = await client.PutSyncDocumentAsync(SampleDocument(), "12", "idem-1");

            Assert.Equal("13", result.Revision);
            var request = Assert.Single(transport.Requests);
            Assert.Equal("PUT", request.Method);
            Assert.Equal("https://sync.example.com/api/v1/sync/document", request.Url);
            Assert.Equal("\"revision-12\"", HeaderOf(request, "If-Match"));
            Assert.Equal("idem-1", HeaderOf(request, "Idempotency-Key"));
            Assert.Equal("application/json", HeaderOf(request, "Accept"));
            Assert.Equal("application/json", HeaderOf(request, "Content-Type"));
            Assert.Equal("Bearer access-1", HeaderOf(request, "Authorization"));
            Assert.False(string.IsNullOrEmpty(HeaderOf(request, "X-Request-Id")));
            Assert.Contains("\"ciphertext\":\"Y2lwaGVy\"", request.Body);
        }

        [Fact]
        public async Task Get_SendsNoBodyNoContentType()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(200, @"{""ok"":true}"));
            var client = NewClient(transport, SignedIn());

            await client.LogoutAsync(); // POST 无 body
            var logout = Assert.Single(transport.Requests);
            Assert.Null(logout.Body);
            Assert.Null(HeaderOf(logout, "Content-Type"));

            transport.Handler = _ => Json(200,
                @"{""user"":{""id"":""u1"",""email"":""a@b.c""},""deviceId"":""d1""}");
            await client.GetMeAsync();
            var get = transport.Requests[1];
            Assert.Equal("GET", get.Method);
            Assert.Null(get.Body);
            Assert.Null(HeaderOf(get, "Content-Type"));
        }

        [Theory]
        [InlineData("0", "\"revision-0\"")]
        [InlineData("12", "\"revision-12\"")]
        [InlineData("18446744073709551615", "\"revision-18446744073709551615\"")] // u64 上限，字符串不转数字
        public void FormatRevisionEtag_Valid(string revision, string expected)
        {
            Assert.Equal(expected, ApiClient.FormatRevisionEtag(revision));
        }

        [Theory]
        [InlineData("01")]   // 前导零
        [InlineData("-1")]   // 负数
        [InlineData("abc")]  // 非数字
        [InlineData("")]     // 空
        public void FormatRevisionEtag_Invalid(string revision)
        {
            Assert.Throws<ArgumentException>(() => ApiClient.FormatRevisionEtag(revision));
        }

        // ---------- 用例 9：错误码稳定映射 ----------

        [Fact]
        public async Task Error_401_MapsAuthenticationWithServerCode()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(401,
                @"{""statusCode"":401,""statusMessage"":""Unauthorized"",""data"":{""code"":""AUTH_TOKEN_EXPIRED"",""message"":""token 已过期""}}",
                new Dictionary<string, string> { ["x-request-id"] = "req-9" }));
            var client = NewClient(transport, SignedIn());

            var error = await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());

            Assert.Equal(ApiErrorKind.Authentication, error.Kind);
            Assert.Equal("AUTH_TOKEN_EXPIRED", error.Code);
            Assert.Equal("token 已过期", error.Message);
            Assert.Equal(401, error.Status);
            Assert.Equal("req-9", error.RequestId);
            Assert.False(error.CodeUnknown);
        }

        [Fact]
        public async Task Error_429WithoutBody_RateLimitedWithRetryAfter()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(429, null,
                new Dictionary<string, string> { ["retry-after"] = "30" }));
            var client = NewClient(transport, SignedIn());

            var error = await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());

            Assert.Equal(ApiErrorKind.Http, error.Kind);
            Assert.Equal("RATE_LIMITED", error.Code);
            Assert.True(error.CodeUnknown);
            Assert.Equal(30000L, error.RetryAfterMs);
        }

        [Fact]
        public async Task Error_500WithoutBody_Http500()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(500, null));
            var client = NewClient(transport, SignedIn());

            var error = await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());

            Assert.Equal(ApiErrorKind.Http, error.Kind);
            Assert.Equal("HTTP_500", error.Code);
            Assert.True(error.CodeUnknown);
            Assert.Equal("服务器请求失败（500）", error.Message);
            Assert.Null(error.RetryAfterMs);
        }

        [Theory]
        [InlineData(@"{""message"":""顶层消息""}", "顶层消息")]
        [InlineData(@"{""statusMessage"":""状态消息""}", "状态消息")]
        [InlineData(@"{""data"":{""message"":""数据消息""}}", "数据消息")]
        [InlineData("这不是 JSON", "服务器请求失败（400）")]
        public async Task Error_MessageFallback(string body, string expected)
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(400, body));
            var client = NewClient(transport, SignedIn());

            var error = await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());

            Assert.Equal(expected, error.Message);
            Assert.True(error.CodeUnknown); // 这些 body 都没有 data.code
        }

        [Fact]
        public async Task Error_RetryAfterHttpDate_UsesInjectedClock()
        {
            var now = new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(503, null,
                new Dictionary<string, string> { ["retry-after"] = now.AddSeconds(90).ToString("r") }));
            transport.Enqueue(Json(503, null,
                new Dictionary<string, string> { ["retry-after"] = now.AddSeconds(-5).ToString("r") }));
            var client = NewClient(transport, SignedIn(), clock: () => now);

            var future = await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());
            var past = await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());

            Assert.Equal(90000L, future.RetryAfterMs);
            Assert.Equal(0L, past.RetryAfterMs);
        }

        [Fact]
        public async Task Auth_WithoutToken_ThrowsBeforeSend()
        {
            var transport = new FakeHttpTransport();
            var client = NewClient(transport, new FakeTokenStore()); // 未登录

            var error = await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());

            Assert.Equal(ApiErrorKind.Authentication, error.Kind);
            Assert.Equal(ApiError.CodeAuthRequired, error.Code);
            Assert.Empty(transport.Requests); // 未发出请求
        }

        [Fact]
        public async Task Network_Timeout_Mapped()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(new TimeoutException("到时"));
            transport.Enqueue(new TimeoutException("到时"));
            var client = NewClient(transport, SignedIn());

            // POST（非 GET/HEAD）→ Ambiguous=true：服务端可能已执行
            var postError = await Assert.ThrowsAsync<ApiError>(
                () => client.PutSyncDocumentAsync(SampleDocument(), "12", "idem-1"));
            Assert.Equal(ApiErrorKind.Timeout, postError.Kind);
            Assert.Equal(ApiError.CodeRequestTimeout, postError.Code);
            Assert.True(postError.Ambiguous);

            var getError = await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());
            Assert.Equal(ApiErrorKind.Timeout, getError.Kind);
            Assert.False(getError.Ambiguous);
        }

        [Fact]
        public async Task Network_Exception_Mapped()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(new System.Net.Sockets.SocketException(10061));
            var client = NewClient(transport, SignedIn());

            var error = await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());

            Assert.Equal(ApiErrorKind.Network, error.Kind);
            Assert.Equal(ApiError.CodeNetworkError, error.Code);
            Assert.False(error.Ambiguous); // GET
        }

        [Fact]
        public async Task Success_204_ReturnsDefault()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(204, null));
            var client = NewClient(transport, SignedIn());

            await client.LogoutAsync(); // 不抛即通过
        }

        [Fact]
        public async Task Success_InvalidJson_ProtocolError()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(200, "这不是 JSON", new Dictionary<string, string> { ["x-request-id"] = "req-7" }));
            var client = NewClient(transport, SignedIn());

            var error = await Assert.ThrowsAsync<ApiError>(() => client.GetSyncDocumentAsync());

            Assert.Equal(ApiErrorKind.Protocol, error.Kind);
            Assert.Equal(ApiError.CodeResponseInvalid, error.Code);
            Assert.Equal(200, error.Status);
            Assert.Equal("req-7", error.RequestId);
        }

        [Fact]
        public async Task Success_WrongShape_ProtocolError()
        {
            var transport = new FakeHttpTransport();
            // revision 应为字符串，实际给了数字
            transport.Enqueue(Json(200, @"{""revision"":123,""updatedAt"":""2026-09-17T08:00:00.000Z""}"));
            var client = NewClient(transport, SignedIn());

            var error = await Assert.ThrowsAsync<ApiError>(
                () => client.PutSyncDocumentAsync(SampleDocument(), "12", "idem-1"));

            Assert.Equal(ApiErrorKind.Protocol, error.Kind);
            Assert.Equal(ApiError.CodeResponseInvalid, error.Code);
        }

        [Fact]
        public async Task Register_SavesTokensWithoutAuthHeader()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(200,
                @"{""accessToken"":""a-new"",""refreshToken"":""r-new"",""expiresIn"":3600," +
                @"""user"":{""id"":""u1"",""email"":""a@b.c""},""device"":{""id"":""d1"",""name"":""Lumia""}}"));
            var tokens = new FakeTokenStore();
            var client = NewClient(transport, tokens);

            var result = await client.RegisterAsync("a@b.c", "pw", null,
                new DeviceDescriptorData { Name = "Lumia", Platform = "windows-arm", AppVersion = "0.1.0" });

            Assert.Equal("a-new", result.AccessToken);
            Assert.Equal("a-new", tokens.Saved.AccessToken);
            Assert.Equal("a-new", tokens.GetTokens().AccessToken);
            var request = Assert.Single(transport.Requests);
            Assert.Null(HeaderOf(request, "Authorization")); // auth:false
            Assert.DoesNotContain("inviteCode", request.Body); // null 时不发该键
        }
    }
}
