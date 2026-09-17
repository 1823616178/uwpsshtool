using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SshTool.Core.Storage.Codecs
{
    // D9：手写编解码的取值/设值辅助。读取严格校验类型（不符抛 JsonException → JsonStore 按损坏处理）；
    // 缺失/Null 走默认值。编码先写已知字段（固定键序），再由 MergeExtra 追加未知字段。
    internal static class CodecExtensions
    {
        public static string GetString(this JObject o, string key)
        {
            var t = o[key];
            if (t == null || t.Type == JTokenType.Null)
            {
                return null;
            }
            if (t.Type != JTokenType.String)
            {
                throw new JsonException(key + " 应为 string，实际 " + t.Type);
            }
            return (string)t;
        }

        public static int GetInt(this JObject o, string key, int defaultValue = 0)
        {
            var t = o[key];
            if (t == null || t.Type == JTokenType.Null)
            {
                return defaultValue;
            }
            if (t.Type != JTokenType.Integer)
            {
                throw new JsonException(key + " 应为 int，实际 " + t.Type);
            }
            return checked((int)(long)t);
        }

        public static bool GetBool(this JObject o, string key, bool defaultValue = false)
        {
            var t = o[key];
            if (t == null || t.Type == JTokenType.Null)
            {
                return defaultValue;
            }
            if (t.Type != JTokenType.Boolean)
            {
                throw new JsonException(key + " 应为 bool，实际 " + t.Type);
            }
            return (bool)t;
        }

        public static double GetDouble(this JObject o, string key, double defaultValue = 0)
        {
            var t = o[key];
            if (t == null || t.Type == JTokenType.Null)
            {
                return defaultValue;
            }
            if (t.Type == JTokenType.Integer)
            {
                return (double)(long)t;
            }
            if (t.Type == JTokenType.Float)
            {
                return (double)t;
            }
            throw new JsonException(key + " 应为 number，实际 " + t.Type);
        }

        public static List<string> GetStringList(this JObject o, string key)
        {
            var t = o[key];
            if (t == null || t.Type == JTokenType.Null)
            {
                return new List<string>();
            }
            if (t.Type != JTokenType.Array)
            {
                throw new JsonException(key + " 应为数组，实际 " + t.Type);
            }
            var list = new List<string>();
            foreach (var item in (JArray)t)
            {
                if (item.Type != JTokenType.String)
                {
                    throw new JsonException(key + " 数组元素应为 string，实际 " + item.Type);
                }
                list.Add((string)item);
            }
            return list;
        }

        public static Dictionary<string, string> GetStringMap(this JObject o, string key)
        {
            var t = o[key];
            if (t == null || t.Type == JTokenType.Null)
            {
                return new Dictionary<string, string>();
            }
            if (t.Type != JTokenType.Object)
            {
                throw new JsonException(key + " 应为对象，实际 " + t.Type);
            }
            var map = new Dictionary<string, string>();
            foreach (var p in ((JObject)t).Properties())
            {
                if (p.Value.Type == JTokenType.Null)
                {
                    map[p.Name] = null;
                }
                else if (p.Value.Type == JTokenType.String)
                {
                    map[p.Name] = (string)p.Value;
                }
                else
                {
                    throw new JsonException(key + "." + p.Name + " 应为 string，实际 " + p.Value.Type);
                }
            }
            return map;
        }

        public static void Set(this JObject o, string key, string value)
        {
            o[key] = value == null ? JValue.CreateNull() : new JValue(value);
        }

        public static void Set(this JObject o, string key, int value)
        {
            o[key] = new JValue(value);
        }

        public static void Set(this JObject o, string key, bool value)
        {
            o[key] = new JValue(value);
        }

        public static void Set(this JObject o, string key, double value)
        {
            o[key] = new JValue(value);
        }

        public static void Set(this JObject o, string key, IEnumerable<string> values)
        {
            o[key] = new JArray(values ?? new List<string>());
        }

        public static void Set(this JObject o, string key, IDictionary<string, string> map)
        {
            var m = new JObject();
            if (map != null)
            {
                foreach (var kv in map)
                {
                    m[kv.Key] = kv.Value == null ? JValue.CreateNull() : new JValue(kv.Value);
                }
            }
            o[key] = m;
        }

        // 解码：收集未知字段到 Extra（无未知字段时返回 null，与模型默认值一致）。
        public static JObject ExtractExtra(this JObject o, ISet<string> knownKeys)
        {
            JObject extra = null;
            foreach (var p in o.Properties())
            {
                if (knownKeys.Contains(p.Name))
                {
                    continue;
                }
                if (extra == null)
                {
                    extra = new JObject();
                }
                extra.Add(p.Name, p.Value.DeepClone());
            }
            return extra;
        }

        // 编码：追加 Extra 中的未知字段；与已知字段同名的键跳过（以实体字段为准）。
        public static void MergeExtra(this JObject o, JObject extra)
        {
            if (extra == null)
            {
                return;
            }
            foreach (var p in extra.Properties())
            {
                if (o[p.Name] != null)
                {
                    continue;
                }
                o.Add(p.Name, p.Value.DeepClone());
            }
        }
    }
}
