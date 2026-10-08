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
                    catch (Exception ex) when (IsPreSendFailure(ex))
                    {
                        // 评审（PR #1）：DNS/连接/TLS 握手阶段失败——请求必然没发出去，
                        // 标记出来，ApiClient 才允许在 https→http 回退后重发非幂等请求。
                        throw new HttpConnectionFailedException("connect phase failed (request not sent)", ex);
                    }
                }
                using (response)
                {
                    return await ReadResponse(response).ConfigureAwait(false);
                }
            }
        }

        // 只收「请求字节一定还没发出」的错误：名字解析、连不上、TLS/证书握手失败。
        // 连接中途断开（ConnectionAborted/Reset/Disconnected）等结果不明，不在此列。
        // WinINet HRESULT：12007 名字解析失败、12029 无法连接、12157 安全通道错误
        // （对端不是 TLS 时的典型结果）、SEC_E_ILLEGAL_MESSAGE / SEC_E_INVALID_TOKEN（Schannel 握手报文非法）。
        private const int HResultNameNotResolved = unchecked((int)0x80072EE7);
        private const int HResultCannotConnect = unchecked((int)0x80072EFD);
        private const int HResultSecurityChannelError = unchecked((int)0x80072F7D);
        private const int HResultSecIllegalMessage = unchecked((int)0x80090326);
        private const int HResultSecInvalidToken = unchecked((int)0x80090308);

        private static bool IsPreSendFailure(Exception ex)
        {
            int hr = ex.HResult;
            if (hr == HResultNameNotResolved || hr == HResultCannotConnect
                || hr == HResultSecurityChannelError || hr == HResultSecIllegalMessage
                || hr == HResultSecInvalidToken)
            {
                return true;
            }
            switch (Windows.Web.WebError.GetStatus(hr))
            {
                case Windows.Web.WebErrorStatus.HostNameNotResolved:
                case Windows.Web.WebErrorStatus.CannotConnect:
                case Windows.Web.WebErrorStatus.CertificateCommonNameIsIncorrect:
                case Windows.Web.WebErrorStatus.CertificateExpired:
                case Windows.Web.WebErrorStatus.CertificateContainsErrors:
                case Windows.Web.WebErrorStatus.CertificateRevoked:
                case Windows.Web.WebErrorStatus.CertificateIsInvalid:
                    return true;
                default:
                    return false;
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
