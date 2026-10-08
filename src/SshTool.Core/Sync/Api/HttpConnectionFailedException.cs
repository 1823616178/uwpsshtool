using System;

namespace SshTool.Core.Sync.Api
{
    // 评审（PR #1）：传输实现确认「请求尚未发出」时抛出——DNS 解析失败、TCP 连不上、
    // TLS 握手失败（含对端根本不是 TLS、证书错误）。此时服务器不可能执行过该请求，
    // ApiClient 才允许把非幂等写请求在 https→http 回退后立即重发。
    // 其余传输异常（连接中途断开、读响应失败等）一律视为「结果不明」，不重放写请求。
    public sealed class HttpConnectionFailedException : Exception
    {
        public HttpConnectionFailedException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
