using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Sync.Api;
using SshTool.Core.Tests.Fakes;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // opt/full-pass：配置默认 https；服务器未上 TLS 时按 httpFallback 降级到同地址 http。
    public class ApiClientHttpFallbackTests
    {
        private const string MeJson = @"{""user"":{""id"":""u1"",""email"":""a@b.c""},""deviceId"":""d1""}";

        private static HttpResponseData Ok(string body)
        {
            return new HttpResponseData(200,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), body);
        }

        // https 一律抛「握手失败」，http 正常应答
        private static FakeHttpTransport TlsLessServer()
        {
            var transport = new FakeHttpTransport();
            transport.Handler = request =>
            {
                if (request.Url.StartsWith("https://", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("TLS handshake failed: wrong version number");
                }
                return Ok(MeJson);
            };
            return transport;
        }

        [Fact]
        public async Task HttpsTransportFailure_FallsBackToHttp_AndSticks()
        {
            var transport = TlsLessServer();
            var client = new ApiClient("https://sync.example.test:46926", FakeTokenStore.SignedIn(), transport,
                allowHttp: true, retryLimit: 0, httpFallback: true);
            int raised = 0;
            client.HttpFallbackActivated += (s, e) => raised++;

            Assert.False(client.IsInsecureTransport);
            var me = await client.GetMeAsync();

            Assert.Equal("u1", me.User.Id);
            Assert.Equal(2, transport.Requests.Count);
            Assert.Equal("https://sync.example.test:46926/api/v1/me", transport.Requests[0].Url);
            Assert.Equal("http://sync.example.test:46926/api/v1/me", transport.Requests[1].Url);
            Assert.True(client.UsingHttpFallback);
            Assert.True(client.IsInsecureTransport);
            Assert.Equal("http://sync.example.test:46926/api/v1/", client.Root);
            Assert.Equal(1, raised);

            // 粘滞：后续请求直接走 http，不再先试 https
            await client.GetMeAsync();
            Assert.Equal(3, transport.Requests.Count);
            Assert.StartsWith("http://", transport.Requests[2].Url);
            Assert.Equal(1, raised);
        }

        [Fact]
        public async Task FallbackDisabled_ReportsNetworkError_NoHttpAttempt()
        {
            var transport = TlsLessServer();
            var client = new ApiClient("https://sync.example.test", FakeTokenStore.SignedIn(), transport,
                allowHttp: true, retryLimit: 0, httpFallback: false);

            var error = await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());

            Assert.Equal(ApiErrorKind.Network, error.Kind);
            Assert.Single(transport.Requests);
            Assert.False(client.UsingHttpFallback);
        }

        [Fact]
        public async Task FallbackRequiresAllowHttp()
        {
            var transport = TlsLessServer();
            var client = new ApiClient("https://sync.example.test", FakeTokenStore.SignedIn(), transport,
                allowHttp: false, retryLimit: 0, httpFallback: true);

            await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());

            Assert.Single(transport.Requests);
            Assert.False(client.IsInsecureTransport);
        }

        [Fact]
        public async Task Timeout_DoesNotDowngrade()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(new TimeoutException());
            var client = new ApiClient("https://sync.example.test", FakeTokenStore.SignedIn(), transport,
                allowHttp: true, retryLimit: 0, httpFallback: true);

            var error = await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());

            Assert.Equal(ApiErrorKind.Timeout, error.Kind);
            Assert.Single(transport.Requests);
            Assert.False(client.UsingHttpFallback);
        }

        [Fact]
        public async Task HttpsWorking_NeverTouchesHttp()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Ok(MeJson));
            var client = new ApiClient("https://sync.example.test", FakeTokenStore.SignedIn(), transport,
                allowHttp: true, retryLimit: 0, httpFallback: true);

            await client.GetMeAsync();

            Assert.Single(transport.Requests);
            Assert.StartsWith("https://", transport.Requests[0].Url);
            Assert.False(client.UsingHttpFallback);
        }

        [Fact]
        public void HttpBaseUrl_IsInsecureFromStart()
        {
            var client = new ApiClient("http://sync.example.test", FakeTokenStore.SignedIn(),
                new FakeHttpTransport(), allowHttp: true, httpFallback: true);
            Assert.True(client.IsInsecureTransport);
            Assert.False(client.UsingHttpFallback);
        }

        [Fact]
        public void ConfigDefaults_AreHttpsWithFallback()
        {
            Assert.StartsWith("https://", AppConfigParser.DefaultSyncApiBaseUrl);
            var cfg = AppConfigParser.Parse(
                "{\"syncApiBaseUrl\":\"https://x.test\",\"allowHttp\":true,\"logLevel\":\"info\"}");
            Assert.Equal(AppConfigParser.DefaultHttpFallback, cfg.HttpFallback);
            Assert.Empty(cfg.Warnings); // httpFallback 是可选键，缺省不告警

            cfg = AppConfigParser.Parse(
                "{\"syncApiBaseUrl\":\"https://x.test\",\"allowHttp\":false,\"httpFallback\":false,\"logLevel\":\"info\"}");
            Assert.False(cfg.HttpFallback);

            cfg = AppConfigParser.Parse(
                "{\"syncApiBaseUrl\":\"https://x.test\",\"allowHttp\":true,\"httpFallback\":\"yes\",\"logLevel\":\"info\"}");
            Assert.Equal(AppConfigParser.DefaultHttpFallback, cfg.HttpFallback);
            Assert.Single(cfg.Warnings);
        }
    }
}
