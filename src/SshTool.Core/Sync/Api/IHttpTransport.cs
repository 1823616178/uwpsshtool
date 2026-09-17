using System;
using System.Threading;
using System.Threading.Tasks;

namespace SshTool.Core.Sync.Api
{
    // 传输抽象：实现侧负责超时（到时抛 TimeoutException）与底层异常；
    // ApiClient 把 TimeoutException 映射为 timeout、其余异常映射为 network。
    public interface IHttpTransport
    {
        Task<HttpResponseData> SendAsync(HttpRequestData request, CancellationToken cancellationToken);
    }
}
