using System;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Api;
using Windows.ApplicationModel.Resources;

namespace SshTool.App.ViewModels.Sync
{
    // U16：保险库页错误文案（ApiError → Api_<CODE> resw；其余回退异常消息）。
    // 与 LoginViewModel.Describe 同构但独立成类：同步密码/恢复密钥校验失败由 Coordinator
    // 以普通 InvalidOperationException 抛出（消息即面向用户的文案，如「同步密码不正确」），
    // 不能误走登录页的「已登录」映射；ApiError（网络/业务码）走与登录页同一套 resw。
    // 脱敏：输出不含密码/恢复密钥/token，仅服务端 message 与请求 ID。
    internal static class VaultErrorText
    {
        public static string Describe(Exception ex, ResourceLoader loader)
        {
            if (ex == null)
            {
                return string.Empty;
            }
            // fix/functional-pass（P2-2）：Coordinator 的面向用户失败带 SyncErrorCode，按码查 resw。
            SyncOperationException sync = ex as SyncOperationException;
            if (sync != null)
            {
                return SyncText.Error(sync, loader);
            }
            ApiError api = ex as ApiError;
            if (api == null)
            {
                return ex.Message ?? string.Empty;
            }
            return DescribeApi(api, loader);
        }

        // RATE_LIMITED 用 RetryAfterMs 填秒数；推断码（HTTP_<status> 等）无 resw 键时
        // 回退服务端原文；末尾附请求 ID。
        public static string DescribeApi(ApiError api, ResourceLoader loader)
        {
            if (api == null)
            {
                return string.Empty;
            }
            if (string.Equals(api.Code, ApiErrorCatalog.RateLimited, StringComparison.Ordinal))
            {
                if (api.RetryAfterMs.HasValue)
                {
                    long seconds = Math.Max(1L, (api.RetryAfterMs.Value + 999L) / 1000L);
                    string template = GetString(loader, ApiErrorCatalog.ResourceKey(api.Code), null);
                    if (!string.IsNullOrEmpty(template))
                    {
                        return AppendRequestId(
                            string.Format(FormatCulture, template, seconds), api.RequestId, loader);
                    }
                }
                return AppendRequestId(GetString(loader, "Login_BusyRetry", api.Message), api.RequestId, loader);
            }
            string text = GetString(loader, ApiErrorCatalog.ResourceKey(api.Code), null);
            if (string.IsNullOrEmpty(text))
            {
                text = FallbackText(api, loader);
            }
            return AppendRequestId(text, api.RequestId, loader);
        }

        // 推断码（HTTP_<status>）无 resw 键：服务端给了文案就显示原文；没给（Core 只有英文诊断
        // 「HTTP 500」）则显示本地化「服务器请求失败（500）」。
        internal static string FallbackText(ApiError api, ResourceLoader loader)
        {
            if (api.MessageFromServer || api.Status == null)
            {
                return api.Message;
            }
            string template = GetString(loader, "Api_HttpStatusFailed", null);
            if (string.IsNullOrEmpty(template))
            {
                return api.Message;
            }
            return string.Format(FormatCulture, template, api.Status.Value);
        }

        private static string AppendRequestId(string text, string requestId, ResourceLoader loader)
        {
            if (string.IsNullOrEmpty(requestId))
            {
                return text;
            }
            string template = GetString(loader, "Login_RequestId", null);
            string suffix = template != null
                ? string.Format(FormatCulture, template, requestId)
                : requestId;
            return text + " (" + suffix + ")";
        }

        private static string GetString(ResourceLoader loader, string key, string fallback)
        {
            try
            {
                string value = loader.GetString(key);
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            catch (Exception)
            {
            }
            return fallback;
        }

        private static System.IFormatProvider FormatCulture
        {
            get { return System.Globalization.CultureInfo.InvariantCulture; }
        }
    }
}
