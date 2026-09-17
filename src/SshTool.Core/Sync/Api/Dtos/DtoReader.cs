using Newtonsoft.Json.Linq;

namespace SshTool.Core.Sync.Api.Dtos
{
    // DTO 逐键读取帮助：必需键缺失或类型不符 → ProtocolParseException；未知键忽略。
    internal static class DtoReader
    {
        public static JObject Obj(JObject o, string key, string path)
        {
            var token = o[key];
            if (token == null || token.Type != JTokenType.Object)
            {
                throw new ProtocolParseException(path, "应为对象，实际 " + TypeOf(token));
            }
            return (JObject)token;
        }

        public static JArray Arr(JObject o, string key, string path)
        {
            var token = o[key];
            if (token == null || token.Type != JTokenType.Array)
            {
                throw new ProtocolParseException(path, "应为数组，实际 " + TypeOf(token));
            }
            return (JArray)token;
        }

        public static JObject ItemObj(JToken token, string path)
        {
            if (token == null || token.Type != JTokenType.Object)
            {
                throw new ProtocolParseException(path, "应为对象，实际 " + TypeOf(token));
            }
            return (JObject)token;
        }

        public static string Str(JObject o, string key, string path)
        {
            var token = o[key];
            if (token == null || token.Type != JTokenType.String)
            {
                throw new ProtocolParseException(path, "应为字符串，实际 " + TypeOf(token));
            }
            return (string)token;
        }

        // 允许键缺失或值为 null；存在时必须是字符串
        public static string StrOrNull(JObject o, string key, string path)
        {
            var token = o[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }
            if (token.Type != JTokenType.String)
            {
                throw new ProtocolParseException(path, "应为字符串或 null，实际 " + TypeOf(token));
            }
            return (string)token;
        }

        public static int Int(JObject o, string key, string path)
        {
            var token = o[key];
            if (token == null || token.Type != JTokenType.Integer)
            {
                throw new ProtocolParseException(path, "应为整数，实际 " + TypeOf(token));
            }
            long value = (long)token;
            if (value < int.MinValue || value > int.MaxValue)
            {
                throw new ProtocolParseException(path, "整数超出 Int32 范围");
            }
            return (int)value;
        }

        public static bool Bool(JObject o, string key, string path)
        {
            var token = o[key];
            if (token == null || token.Type != JTokenType.Boolean)
            {
                throw new ProtocolParseException(path, "应为布尔，实际 " + TypeOf(token));
            }
            return (bool)token;
        }

        private static string TypeOf(JToken token)
        {
            return token == null ? "缺失" : token.Type.ToString();
        }
    }
}
