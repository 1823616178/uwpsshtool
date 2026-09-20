using System;
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
            string text = loader.GetString(ApiErrorCatalog.ResourceKey(api.Code));
            if (string.IsNullOrEmpty(text))
            {
                text = api.Message;
            }
            return AppendRequestId(text, api.RequestId, loader);
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
