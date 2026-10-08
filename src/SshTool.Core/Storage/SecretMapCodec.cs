using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SshTool.Core.Storage
{
    // D03 凭据表（{"键":"值"} JSON）的读写编码，从 DpapiSecretStore 抽出便于单测。
    // opt/full-pass：内容损坏（非 UTF-8 JSON 对象）时隔离旧文件并从空表开始；
    // 读文件本身的 IO 异常照常上抛（可能是暂时性的，不能据此丢弃凭据）。
    public static class SecretMapCodec
    {
        public static bool TryParse(byte[] raw, out Dictionary<string, string> map)
        {
            map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (raw == null || raw.Length == 0)
            {
                return true;
            }
            JObject obj;
            try
            {
                string json = Encoding.UTF8.GetString(raw, 0, raw.Length);
                obj = JsonText.ParseObject(json);
            }
            catch (JsonException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            if (obj == null)
            {
                return false;
            }
            foreach (JProperty property in obj.Properties())
            {
                if (property.Value != null && property.Value.Type == JTokenType.String)
                {
                    map[property.Name] = (string)property.Value;
                }
            }
            return true;
        }

        public static byte[] Serialize(IDictionary<string, string> map)
        {
            var obj = new JObject();
            foreach (KeyValuePair<string, string> pair in map)
            {
                obj[pair.Key] = pair.Value;
            }
            return Encoding.UTF8.GetBytes(obj.ToString(Formatting.None));
        }

        // 返回加载出的表；quarantined 表示本次发现损坏并已（尝试）隔离。
        public static async Task<Dictionary<string, string>> LoadAsync(ISecureFile file, Action<string> warn)
        {
            if (file == null)
            {
                throw new ArgumentNullException(nameof(file));
            }
            byte[] raw = await file.ReadAsync().ConfigureAwait(false);
            Dictionary<string, string> map;
            if (TryParse(raw, out map))
            {
                return map;
            }
            bool moved = false;
            var quarantine = file as ISecureFileQuarantine;
            if (quarantine != null)
            {
                try
                {
                    moved = await quarantine.QuarantineAsync("parse").ConfigureAwait(false);
                }
                catch (Exception)
                {
                    moved = false;
                }
            }
            if (warn != null)
            {
                warn(moved
                    ? "凭据表内容损坏，已隔离旧文件并从空表开始"
                    : "凭据表内容损坏，无法隔离；从空表开始，下次写入将覆盖");
            }
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
