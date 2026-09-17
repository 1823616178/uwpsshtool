using System.Collections.Generic;

namespace SshTool.Core.Sync.Api
{
    public sealed class HttpRequestData
    {
        public HttpRequestData(
            string method,
            string url,
            IReadOnlyList<KeyValuePair<string, string>> headers,
            string body,
            int timeoutMs)
        {
            Method = method;
            Url = url;
            Headers = headers;
            Body = body;
            TimeoutMs = timeoutMs;
        }

        public string Method { get; private set; }
        public string Url { get; private set; }
        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; private set; }

        // null 表示无请求体（GET/HEAD）
        public string Body { get; private set; }
        public int TimeoutMs { get; private set; }
    }
}
