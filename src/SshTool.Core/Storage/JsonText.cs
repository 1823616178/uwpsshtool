using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SshTool.Core.Storage
{
    // Newtonsoft 默认把 ISO 日期字符串解析成 JTokenType.Date，破坏字符串字段的往返一致性
    // （codec 对时间字段一律按 string 处理）。统一入口：解析时禁用 DateParseHandling。
    public static class JsonText
    {
        public static JObject ParseObject(string text)
        {
            using (var sr = new StringReader(text))
            using (var reader = new JsonTextReader(sr))
            {
                reader.DateParseHandling = DateParseHandling.None;
                reader.Culture = CultureInfo.InvariantCulture;
                return JObject.Load(reader);
            }
        }
    }
}
