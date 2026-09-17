using System.Collections.Generic;

namespace SshTool.Core.Sync.Api
{
    public sealed class HttpResponseData
    {
        public HttpResponseData(int statusCode, IReadOnlyDictionary<string, string> headers, string body)
        {
            StatusCode = statusCode;
            Headers = headers;
            Body = body;
        }

        public int StatusCode { get; private set; }

        // 键大小写不敏感
        public IReadOnlyDictionary<string, string> Headers { get; private set; }

        // null 表示无响应体（如 HEAD/204）
        public string Body { get; private set; }

        public string Header(string name)
        {
            string value;
            return Headers != null && Headers.TryGetValue(name, out value) ? value : null;
        }
    }
}
