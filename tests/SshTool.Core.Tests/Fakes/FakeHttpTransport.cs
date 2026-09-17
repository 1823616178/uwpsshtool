using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SshTool.Core.Sync.Api;

namespace SshTool.Core.Tests.Fakes
{
    // IHttpTransport 测试替身：按队列依次返回响应/抛异常，或交给 Handler 全权处理；
    // 记录所有收到的请求供断言。
    public sealed class FakeHttpTransport : IHttpTransport
    {
        private readonly Queue<object> _queue = new Queue<object>();

        public List<HttpRequestData> Requests { get; } = new List<HttpRequestData>();

        public Func<HttpRequestData, HttpResponseData> Handler { get; set; }

        // 异步处理器（如用 Task.Yield 制造真并发），优先级高于 Handler 与队列
        public Func<HttpRequestData, Task<HttpResponseData>> AsyncHandler { get; set; }

        public void Enqueue(HttpResponseData response)
        {
            _queue.Enqueue(response);
        }

        public void Enqueue(Exception error)
        {
            _queue.Enqueue(error);
        }

        public Task<HttpResponseData> SendAsync(HttpRequestData request, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }
            if (AsyncHandler != null)
            {
                return AsyncHandler(request);
            }
            if (Handler != null)
            {
                return Task.FromResult(Handler(request));
            }
            if (_queue.Count == 0)
            {
                throw new InvalidOperationException("FakeHttpTransport 队列已空，且未设置 Handler");
            }
            var item = _queue.Dequeue();
            var error = item as Exception;
            if (error != null)
            {
                throw error;
            }
            return Task.FromResult((HttpResponseData)item);
        }
    }
}
