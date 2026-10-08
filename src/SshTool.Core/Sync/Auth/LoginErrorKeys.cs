using System;
using SshTool.Core.Sync.Api;

namespace SshTool.Core.Sync.Auth
{
    // fix/login-feedback：登录 / 注册失败时「该显示哪条 resw 文案」的纯逻辑判定（App 只负责查表）。
    // 目标是登录页永远不出现「点了没反应」：任何异常都要落到一条非空、本地化的文案上。
    //  - ApiError（服务端业务码 / 客户端自产码）返回 null，由调用方按 Api_<CODE> 查表（ApiErrorCatalog）；
    //    例外：https 刚降级到 http 但本次写请求不能安全重放时，提示「已切换，请再点一次」；
    //  - SyncOperationException 返回 null，由调用方按 SyncErrorCode 查表；
    //  - 超时 / 取消 / 连接失败映射到对应的 Api_* 文案；
    //  - 其余一切（意外异常）→ 通用失败文案（带异常类型名，便于用户反馈时定位）。
    public static class LoginErrorKeys
    {
        public const string UnexpectedKey = "Login_UnexpectedError";
        public const string HttpFallbackRetryKey = "Login_HttpFallbackRetry";

        public static readonly string[] AllKeys = new string[]
        {
            UnexpectedKey,
            HttpFallbackRetryKey,
        };

        // 返回需要直接显示的 resw 键；null 表示交给 ApiError / SyncOperationException 的既有映射。
        public static string OverrideKey(Exception ex)
        {
            Exception error = Unwrap(ex);
            if (error == null)
            {
                return UnexpectedKey;
            }
            ApiError api = error as ApiError;
            if (api != null)
            {
                return api.HttpFallbackActivated ? HttpFallbackRetryKey : null;
            }
            if (error is SyncOperationException)
            {
                return null;
            }
            if (error is TimeoutException || error is OperationCanceledException)
            {
                return ApiErrorCatalog.ResourceKey(ApiError.CodeRequestTimeout);
            }
            if (error is HttpConnectionFailedException)
            {
                return ApiErrorCatalog.ResourceKey(ApiError.CodeNetworkError);
            }
            return UnexpectedKey;
        }

        // 通用失败文案的 {0}：异常类型名 + HRESULT（不含消息正文，消息可能带服务端/内部细节）。
        public static string Diagnostic(Exception ex)
        {
            Exception error = Unwrap(ex);
            if (error == null)
            {
                return "unknown";
            }
            return error.GetType().Name + " 0x"
                + error.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        }

        // await 一般已拆掉 AggregateException；Task.Wait / ContinueWith 路径仍可能包一层。
        private static Exception Unwrap(Exception ex)
        {
            Exception current = ex;
            int guard = 0;
            while (current is AggregateException && current.InnerException != null && guard < 8)
            {
                current = current.InnerException;
                guard++;
            }
            return current;
        }
    }
}
