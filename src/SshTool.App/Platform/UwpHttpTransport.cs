using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SshTool.Core.Sync.Api;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace SshTool.App.Platform
{
    // S06：IHttpTransport 的 UWP 实现（Windows.Web.Http）。
    // 约定：超时抛 TimeoutException；调用方取消原样抛 OperationCanceledException；
    // 其余异常上抛，由 Core 侧 ApiClient 映射为 network 错误。
    public sealed class UwpHttpTransport : IHttpTransport
    {
        // O06：filter 与 client 复用，不再每请求新建。Windows.Web.Http 的连接池
        // 挂在 filter 上——每请求新建 filter 就是每请求重做一次 TLS 握手。一个
        // 同步周期要打 revision/download/upload/devices/history 多个请求，再叠加
        // 60 s 轮询与 3 s 去抖上传，在蜂窝网下是明显的延迟与耗电放大。
        // HttpClient 是线程安全的，允许并发 SendRequestAsync。
        //
        // 不 Dispose：本对象是应用级单例（AppServices 持有），与进程同寿命；
        // 提前释放会让在飞行的同步请求失败。
        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var filter = new HttpBaseProtocolFilter();
            // 同步请求不碰缓存与 Cookie（§2.1：凭证只走 Authorization 头）
            filter.CacheControl.ReadBehavior = HttpCacheReadBehavior.NoCache;
            filter.CacheControl.WriteBehavior = HttpCacheWriteBehavior.NoCache;
            filter.CookieUsageBehavior = HttpCookieUsageBehavior.NoCookies;
            filter.AllowAutoRedirect = false;
            return new HttpClient(filter);
        }

        public async Task<HttpResponseData> SendAsync(HttpRequestData request, CancellationToken cancellationToken)
        {
            // 请求/响应对象仍按请求创建与释放：可复用的只有连接池，不是消息。
            using (var message = new HttpRequestMessage(MethodOf(request.Method), new Uri(request.Url)))
            {
                foreach (var header in request.Headers)
                {
                    // TryAppendWithoutValidation：If-Match 的引号值等会被强校验拒绝
                    message.Headers.TryAppendWithoutValidation(header.Key, header.Value);
                }
                if (request.Body != null)
                {
                    message.Content = new HttpStringContent(
                        request.Body, Windows.Storage.Streams.UnicodeEncoding.Utf8, "application/json");
                }

                HttpResponseMessage response;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(request.TimeoutMs);
                    try
                    {
                        response = await Client.SendRequestAsync(message).AsTask(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            throw new TimeoutException("请求超时（" + request.TimeoutMs + "ms）");
                        }
                        throw;
                    }
                }
                using (response)
                {
                    return await ReadResponse(response).ConfigureAwait(false);
                }
            }
        }

        private static HttpMethod MethodOf(string method)
        {
            switch (method)
            {
                case "GET": return HttpMethod.Get;
                case "POST": return HttpMethod.Post;
                case "PUT": return HttpMethod.Put;
                case "DELETE": return HttpMethod.Delete;
                case "PATCH": return HttpMethod.Patch;
                case "HEAD": return HttpMethod.Head;
                default: return new HttpMethod(method);
            }
        }

        private static async Task<HttpResponseData> ReadResponse(HttpResponseMessage response)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in response.Headers)
            {
                headers[header.Key] = header.Value;
            }
            string body = null;
            if (response.Content != null)
            {
                foreach (var header in response.Content.Headers)
                {
                    headers[header.Key] = header.Value;
                }
                body = await response.Content.ReadAsStringAsync().AsTask().ConfigureAwait(false);
            }
            return new HttpResponseData((int)response.StatusCode, headers, body);
        }
    }
}
