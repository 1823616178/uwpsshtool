using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SshTool.Core.Common
{
    public sealed class ParsedAppConfig
    {
        public string SyncApiBaseUrl { get; set; }
        public bool AllowHttp { get; set; }
        public bool HttpFallback { get; set; }
        public string LogLevel { get; set; }
        public List<string> Warnings { get; } = new List<string>();
    }

    // X05：appconfig.json 解析（纯逻辑可单测）。缺字段用默认值并记警告；坏 JSON 全默认。
    // 注意：不包含任何服务端密钥（01-DESIGN.md §12.3）。
    public static class AppConfigParser
    {
        // feat/remember-vault：同步服务器按设计就是明文 HTTP（用户确认），默认直接用 http，
        // 不再先试 https 再降级（此前首次登录会多一步「已切换到 HTTP，请再点一次」）。
        // httpFallback 只对 https 地址起作用（https 传输层失败时降级到同地址 http，见 ApiClient）；
        // 将来换成 TLS 服务器时把地址改成 https，并把 allowHttp / httpFallback 改成 false 即可强制 HTTPS。
        public const string DefaultSyncApiBaseUrl = "http://123.161.179.32:46926";
        public const bool DefaultAllowHttp = true;
        public const bool DefaultHttpFallback = true;
        public const string DefaultLogLevel = "info";

        private static readonly HashSet<string> ValidLogLevels =
            new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "debug", "info", "warning", "error" };

        public static ParsedAppConfig Parse(string json)
        {
            var result = new ParsedAppConfig
            {
                SyncApiBaseUrl = DefaultSyncApiBaseUrl,
                AllowHttp = DefaultAllowHttp,
                HttpFallback = DefaultHttpFallback,
                LogLevel = DefaultLogLevel
            };

            JObject obj = null;
            if (!string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    obj = JObject.Parse(json);
                }
                catch (Newtonsoft.Json.JsonException)
                {
                    result.Warnings.Add("appconfig.json 不是合法 JSON，全部使用默认值");
                    return result;
                }
            }
            else
            {
                result.Warnings.Add("appconfig.json 为空，全部使用默认值");
                return result;
            }

            JToken token;
            if (obj.TryGetValue("syncApiBaseUrl", System.StringComparison.OrdinalIgnoreCase, out token)
                && token.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)token))
            {
                result.SyncApiBaseUrl = (string)token;
            }
            else
            {
                result.Warnings.Add("appconfig 缺 syncApiBaseUrl，使用默认值 " + DefaultSyncApiBaseUrl);
            }

            if (obj.TryGetValue("allowHttp", System.StringComparison.OrdinalIgnoreCase, out token)
                && token.Type == JTokenType.Boolean)
            {
                result.AllowHttp = (bool)token;
            }
            else
            {
                result.Warnings.Add("appconfig 缺 allowHttp，使用默认值 " + DefaultAllowHttp);
            }

            // httpFallback 是 opt/full-pass 新增的可选键：缺省静默取默认值，不记警告
            if (obj.TryGetValue("httpFallback", System.StringComparison.OrdinalIgnoreCase, out token))
            {
                if (token.Type == JTokenType.Boolean)
                {
                    result.HttpFallback = (bool)token;
                }
                else
                {
                    result.Warnings.Add("appconfig 的 httpFallback 不是布尔值，使用默认值 " + DefaultHttpFallback);
                }
            }

            if (obj.TryGetValue("logLevel", System.StringComparison.OrdinalIgnoreCase, out token)
                && token.Type == JTokenType.String && ValidLogLevels.Contains((string)token))
            {
                result.LogLevel = ((string)token).ToLowerInvariant();
            }
            else
            {
                result.Warnings.Add("appconfig 缺 logLevel 或值非法（debug/info/warning/error），使用默认值 " + DefaultLogLevel);
            }

            return result;
        }
    }
}
