using System;
using System.Threading;
using System.Threading.Tasks;

namespace SshTool.Core.Sync.Api
{
    // 传输抽象：实现侧负责超时（到时抛 TimeoutException）与底层异常；
    // ApiClient 把 TimeoutException 映射为 timeout、其余异常映射为 network。
    // 能确定请求尚未发出（DNS/连接/TLS 握手失败）时应抛 HttpConnectionFailedException，
    // ApiClient 据此判断 https→http 回退时能否安全重发非幂等请求。
    public interface IHttpTransport
    {
        Task<HttpResponseData> SendAsync(HttpRequestData request, CancellationToken cancellationToken);
    }
}
