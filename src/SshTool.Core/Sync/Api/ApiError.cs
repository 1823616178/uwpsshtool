using System;

namespace SshTool.Core.Sync.Api
{
    // 03-SYNC-PROTOCOL.md §2.3 错误模型：与桌面端 ApiClientError 同构。
    public sealed class ApiError : Exception
    {
        public ApiError(
            ApiErrorKind kind,
            string code,
            string message,
            int? status = null,
            string requestId = null,
            long? retryAfterMs = null,
            bool ambiguous = false,
            bool codeUnknown = false,
            Exception inner = null,
            bool messageFromServer = false)
            : base(message, inner)
        {
            Kind = kind;
            Code = code;
            Status = status;
            RequestId = requestId;
            RetryAfterMs = retryAfterMs;
            Ambiguous = ambiguous;
            CodeUnknown = codeUnknown;
            MessageFromServer = messageFromServer;
        }

        public ApiErrorKind Kind { get; private set; }
        public string Code { get; private set; }
        public int? Status { get; private set; }
        public string RequestId { get; private set; }
        public long? RetryAfterMs { get; private set; }

        // 非 GET/HEAD 请求发生网络错误/超时（服务端可能已执行）
        public bool Ambiguous { get; private set; }

        // 响应体里没有 data.code（HEAD 无响应体、或代理返回 HTML），code 是按状态码推的
        public bool CodeUnknown { get; private set; }

        // fix/functional-pass：Message 是否为服务端原文（false=客户端诊断文本，App 应显示本地化文案）。
        public bool MessageFromServer { get; private set; }

        // 客户端自产错误码（§2.3 末）
        public const string CodeAuthRequired = "AUTH_REQUIRED";
        public const string CodeAuthRefreshUnavailable = "AUTH_REFRESH_UNAVAILABLE";
        public const string CodeNetworkError = "NETWORK_ERROR";
        public const string CodeRequestTimeout = "REQUEST_TIMEOUT";
        public const string CodeResponseInvalid = "RESPONSE_INVALID";
        public const string CodeSyncRevisionInvalid = "SYNC_REVISION_INVALID";
        public const string CodeSyncMetadataInvalid = "SYNC_METADATA_INVALID";
    }
}
