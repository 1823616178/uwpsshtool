using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.Core.Sync.Api;
using SshTool.Core.Sync.Api.Dtos;
using SshTool.Core.Tests.Fakes;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // fix/login-feedback：https 失败后已切到 http、但本次登录 POST 结果不明而没有重放时，
    // ApiError.HttpFallbackActivated=true，登录页据此提示「已切换，请再试一次」；再次提交直接走 http。
    public class ApiClientFallbackRetryFlagTests
    {
        private const string TokenJson =
            @"{""user"":{""id"":""u1"",""email"":""a@b.c""},""device"":{""id"":""d1"",""name"":""Lumia""}," +
            @"""accessToken"":""at"",""refreshToken"":""rt"",""expiresIn"":900}";

        private static HttpResponseData Ok(string body)
        {
            return new HttpResponseData(200,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), body);
        }

        private static DeviceDescriptorData Device()
        {
            return new DeviceDescriptorData { Name = "Lumia", Platform = "windows-mobile", AppVersion = "1.0.0" };
        }

        [Fact]
        public async Task AmbiguousHttpsFailure_Login_FlagsFallbackPending_ThenRetryGoesHttp()
        {
            var transport = new FakeHttpTransport();
            transport.Handler = request =>
            {
                if (request.Url.StartsWith("https://", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("connection reset during handshake");
                }
                return Ok(TokenJson);
            };
            var tokens = new FakeTokenStore();
            var client = new ApiClient("https://sync.example.test:46926", tokens, transport,
                allowHttp: true, retryLimit: 2, httpFallback: true);

            var error = await Assert.ThrowsAsync<ApiError>(() => client.LoginAsync("a@b.c", "pw", Device()));

            Assert.Equal(ApiErrorKind.Network, error.Kind);
            Assert.True(error.HttpFallbackActivated);
            Assert.Single(transport.Requests);
            Assert.True(client.UsingHttpFallback);

            // 用户再点一次：直接走 http，成功。
            await client.LoginAsync("a@b.c", "pw", Device());
            Assert.Equal(2, transport.Requests.Count);
            Assert.Equal("http://sync.example.test:46926/api/v1/auth/login", transport.Requests[1].Url);
        }

        [Fact]
        public async Task PreSendHttpsFailure_Login_IsReplayedImmediately_NoFlag()
        {
            var transport = new FakeHttpTransport();
            transport.Handler = request =>
            {
                if (request.Url.StartsWith("https://", StringComparison.Ordinal))
                {
                    throw new HttpConnectionFailedException("TLS handshake failed", null);
                }
                return Ok(TokenJson);
            };
            var client = new ApiClient("https://sync.example.test:46926", new FakeTokenStore(), transport,
                allowHttp: true, retryLimit: 2, httpFallback: true);

            await client.LoginAsync("a@b.c", "pw", Device());

            Assert.Equal(2, transport.Requests.Count);
            Assert.StartsWith("http://", transport.Requests[1].Url);
        }

        [Fact]
        public async Task ReplayAlsoFails_NotFlagged_GenuineNetworkError()
        {
            var transport = new FakeHttpTransport();
            transport.Handler = request =>
            {
                throw new HttpConnectionFailedException("cannot connect", null);
            };
            var client = new ApiClient("https://sync.example.test", new FakeTokenStore(), transport,
                allowHttp: true, retryLimit: 0, httpFallback: true);

            var error = await Assert.ThrowsAsync<ApiError>(() => client.LoginAsync("a@b.c", "pw", Device()));

            Assert.Equal(ApiErrorKind.Network, error.Kind);
            Assert.False(error.HttpFallbackActivated);
            Assert.Equal(2, transport.Requests.Count);
        }

        [Fact]
        public async Task FallbackDisabled_NotFlagged()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(new InvalidOperationException("reset"));
            var client = new ApiClient("https://sync.example.test", new FakeTokenStore(), transport,
                allowHttp: true, retryLimit: 0, httpFallback: false);

            var error = await Assert.ThrowsAsync<ApiError>(() => client.LoginAsync("a@b.c", "pw", Device()));

            Assert.False(error.HttpFallbackActivated);
        }

        // 已经在 http 上（回退已粘滞）再失败：是真的连不上，不能再提示「已切换，请重试」。
        [Fact]
        public async Task FailureAfterFallbackAlreadyActive_NotFlagged()
        {
            var transport = new FakeHttpTransport();
            transport.Handler = request =>
            {
                throw new InvalidOperationException("reset");
            };
            var client = new ApiClient("https://sync.example.test", new FakeTokenStore(), transport,
                allowHttp: true, retryLimit: 0, httpFallback: true);

            var first = await Assert.ThrowsAsync<ApiError>(() => client.LoginAsync("a@b.c", "pw", Device()));
            Assert.True(first.HttpFallbackActivated);

            var second = await Assert.ThrowsAsync<ApiError>(() => client.LoginAsync("a@b.c", "pw", Device()));
            Assert.False(second.HttpFallbackActivated);
            Assert.StartsWith("http://", transport.Requests[1].Url);
        }
    }
}
