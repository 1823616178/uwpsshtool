using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SshTool.Core.Spikes
{
    /// <summary>
    /// SP01：验证 Newtonsoft.Json 12.0.3（JsonTextReader/JsonTextWriter/JObject）在当前运行时
    /// 的读写一致性，以及 ulong/时间格式等 .NET Native 敏感行为。真机（.NET Native）若全部通过，
    /// 则 D9 决策成立；任何一项失败需记录并按 D9 兜底评估降级。
    /// </summary>
    public static class JsonSpike
    {
        public sealed class SpikeCheck
        {
            public string Name { get; }
            public bool Passed { get; }
            public string Detail { get; }

            public SpikeCheck(string name, bool passed, string detail)
            {
                Name = name;
                Passed = passed;
                Detail = detail;
            }
        }

        private const string SampleName = "Lumia SSH 终端 😀";
        private static readonly string[] ExpectedKeyOrder = { "schemaVersion", "name", "enabled", "note", "ports", "nested" };
        private static readonly int[] ExpectedPorts = { 22, 2222, 65535 };

        public static IReadOnlyList<SpikeCheck> RunChecks()
        {
            var checks = new List<SpikeCheck>();
            string json = WriteSample();

            JObject doc = null;
            Exception readError = null;
            try
            {
                using (var sr = new StringReader(json))
                using (var reader = new JsonTextReader(sr))
                {
                    doc = JObject.Load(reader);
                }
            }
            catch (Exception ex)
            {
                readError = ex;
            }

            if (doc == null)
            {
                checks.Add(new SpikeCheck("readback", false, "JObject.Load 失败: " + Describe(readError)));
                return checks;
            }

            var keys = new List<string>();
            foreach (var prop in doc.Properties())
            {
                keys.Add(prop.Name);
            }
            checks.Add(new SpikeCheck(
                "keyOrder",
                string.Join(",", keys.ToArray()) == string.Join(",", ExpectedKeyOrder),
                "实际键序: " + string.Join(",", keys.ToArray())));

            checks.Add(CheckInt(doc, "schemaVersion", 1));
            checks.Add(new SpikeCheck(
                "string-unicode",
                doc["name"] != null && doc["name"].Type == JTokenType.String && (string)doc["name"] == SampleName,
                "name=" + (doc["name"] == null ? "<missing>" : doc["name"].ToString())));
            checks.Add(new SpikeCheck(
                "bool",
                doc["enabled"] != null && doc["enabled"].Type == JTokenType.Boolean && (bool)doc["enabled"],
                "enabled=" + (doc["enabled"] == null ? "<missing>" : doc["enabled"].ToString())));
            checks.Add(new SpikeCheck(
                "null",
                doc["note"] != null && doc["note"].Type == JTokenType.Null,
                "note.Type=" + (doc["note"] == null ? "<missing>" : doc["note"].Type.ToString())));

            var ports = doc["ports"] as JArray;
            bool portsOk = ports != null && ports.Count == ExpectedPorts.Length;
            if (portsOk)
            {
                for (int i = 0; i < ExpectedPorts.Length; i++)
                {
                    portsOk = ports[i].Type == JTokenType.Integer && (long)ports[i] == ExpectedPorts[i];
                    if (!portsOk)
                    {
                        break;
                    }
                }
            }
            checks.Add(new SpikeCheck("array-int", portsOk, "ports=" + (ports == null ? "<missing>" : ports.ToString(Formatting.None))));

            var nested = doc["nested"] as JObject;
            bool nestedOk = nested != null
                && nested["host"] != null && nested["host"].Type == JTokenType.String && (string)nested["host"] == "example.com"
                && nested["keepAlive"] != null && nested["keepAlive"].Type == JTokenType.Integer && (long)nested["keepAlive"] == 30;
            checks.Add(new SpikeCheck("nested-object", nestedOk, "nested=" + (nested == null ? "<missing>" : nested.ToString(Formatting.None))));

            checks.Add(CheckUlong());
            checks.Add(CheckDateTimeFormat());
            return checks;
        }

        public static string RoundTrip()
        {
            var checks = RunChecks();
            var sb = new StringBuilder();
            int passed = 0;
            foreach (var c in checks)
            {
                if (c.Passed)
                {
                    passed++;
                }
                sb.AppendLine((c.Passed ? "[PASS] " : "[FAIL] ") + c.Name + " — " + c.Detail);
            }
            sb.AppendLine("RESULT: " + (passed == checks.Count ? "PASS" : "FAIL") + " (" + passed.ToString(CultureInfo.InvariantCulture)
                + "/" + checks.Count.ToString(CultureInfo.InvariantCulture) + ")");
            return sb.ToString();
        }

        private static string WriteSample()
        {
            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb, CultureInfo.InvariantCulture))
            using (var w = new JsonTextWriter(sw))
            {
                w.WriteStartObject();
                w.WritePropertyName("schemaVersion");
                w.WriteValue(1);
                w.WritePropertyName("name");
                w.WriteValue(SampleName);
                w.WritePropertyName("enabled");
                w.WriteValue(true);
                w.WritePropertyName("note");
                w.WriteNull();
                w.WritePropertyName("ports");
                w.WriteStartArray();
                foreach (var p in ExpectedPorts)
                {
                    w.WriteValue(p);
                }
                w.WriteEndArray();
                w.WritePropertyName("nested");
                w.WriteStartObject();
                w.WritePropertyName("host");
                w.WriteValue("example.com");
                w.WritePropertyName("keepAlive");
                w.WriteValue(30);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            return sb.ToString();
        }

        private static SpikeCheck CheckInt(JObject doc, string key, long expected)
        {
            var token = doc[key];
            bool ok = token != null && token.Type == JTokenType.Integer && (long)token == expected;
            return new SpikeCheck("int-" + key, ok, key + "=" + (token == null ? "<missing>" : token.ToString() + " (" + token.Type + ")"));
        }

        private static SpikeCheck CheckUlong()
        {
            try
            {
                ulong v = ulong.Parse("18446744073709551615", CultureInfo.InvariantCulture);
                bool ok = v == ulong.MaxValue && v.ToString(CultureInfo.InvariantCulture) == "18446744073709551615";
                return new SpikeCheck("ulong-max", ok, "解析值=" + v.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                return new SpikeCheck("ulong-max", false, "异常: " + Describe(ex));
            }
        }

        private static SpikeCheck CheckDateTimeFormat()
        {
            string s = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            bool ok = Regex.IsMatch(s, "^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{3}Z$");
            return new SpikeCheck("datetime-format", ok, "样例=" + s);
        }

        private static string Describe(Exception ex)
        {
            return ex == null ? "<unknown>" : ex.GetType().Name + ": " + ex.Message;
        }
    }
}
