using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SshTool.Core.Common
{
    public sealed class ParsedAppConfig
    {
        public string SyncApiBaseUrl { get; set; }
        public bool AllowHttp { get; set; }
        public string LogLevel { get; set; }
        public List<string> Warnings { get; } = new List<string>();
    }

    // X05：appconfig.json 解析（纯逻辑可单测）。缺字段用默认值并记警告；坏 JSON 全默认。
    // 注意：不包含任何服务端密钥（01-DESIGN.md §12.3）。
    public static class AppConfigParser
    {
        public const string DefaultSyncApiBaseUrl = "http://123.161.179.32:46926";
        public const bool DefaultAllowHttp = true;
        public const string DefaultLogLevel = "info";

        private static readonly HashSet<string> ValidLogLevels =
            new HashSet<string>(System.StringComparer.OrdinalIgnoreCase) { "debug", "info", "warning", "error" };

        public static ParsedAppConfig Parse(string json)
        {
            var result = new ParsedAppConfig
            {
                SyncApiBaseUrl = DefaultSyncApiBaseUrl,
                AllowHttp = DefaultAllowHttp,
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
