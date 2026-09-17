using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SshTool.Core.Models;

namespace SshTool.Core.Storage.Codecs
{
    // §8.1 Tunnel（与桌面端 TunnelConfig 同构）；type ↔ local/remote/dynamic/relay。
    public sealed class TunnelCodec : IEntityCodec<Tunnel>
    {
        private static readonly ISet<string> KnownKeys = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "id", "name", "serverId", "groupId", "type", "listenHost", "listenPort",
            "destHost", "destPort", "destServerId", "autoReconnect", "enabled", "autoStart"
        };

        public string GetId(Tunnel item)
        {
            return item.Id;
        }

        public JObject Encode(Tunnel item)
        {
            var o = new JObject();
            o.Set("id", item.Id);
            o.Set("name", item.Name);
            o.Set("serverId", item.ServerId);
            o.Set("groupId", item.GroupId);
            o.Set("type", TunnelTypeToJson(item.Type));
            o.Set("listenHost", item.ListenHost);
            o.Set("listenPort", item.ListenPort);
            o.Set("destHost", item.DestHost);
            o.Set("destPort", item.DestPort);
            o.Set("destServerId", item.DestServerId);
            o.Set("autoReconnect", item.AutoReconnect);
            o.Set("enabled", item.Enabled);
            o.Set("autoStart", item.AutoStart);
            o.MergeExtra(item.Extra);
            return o;
        }

        public Tunnel Decode(JObject json)
        {
            var item = new Tunnel();
            item.Id = json.GetString("id");
            item.Name = json.GetString("name");
            item.ServerId = json.GetString("serverId");
            item.GroupId = json.GetString("groupId");
            item.Type = TunnelTypeFromJson(json.GetString("type"));
            item.ListenHost = json.GetString("listenHost");
            item.ListenPort = json.GetInt("listenPort");
            item.DestHost = json.GetString("destHost");
            item.DestPort = json.GetInt("destPort");
            item.DestServerId = json.GetString("destServerId");
            item.AutoReconnect = json.GetBool("autoReconnect");
            item.Enabled = json.GetBool("enabled");
            item.AutoStart = json.GetBool("autoStart");
            item.Extra = json.ExtractExtra(KnownKeys);
            return item;
        }

        private static string TunnelTypeToJson(TunnelType value)
        {
            switch (value)
            {
                case TunnelType.Local: return "local";
                case TunnelType.Remote: return "remote";
                case TunnelType.Dynamic: return "dynamic";
                case TunnelType.Relay: return "relay";
                default: throw new JsonException("未知 tunnel type: " + value);
            }
        }

        private static TunnelType TunnelTypeFromJson(string value)
        {
            if (value == null)
            {
                return TunnelType.Local;
            }
            switch (value)
            {
                case "local": return TunnelType.Local;
                case "remote": return TunnelType.Remote;
                case "dynamic": return TunnelType.Dynamic;
                case "relay": return TunnelType.Relay;
                default: throw new JsonException("未知 tunnel type: " + value);
            }
        }
    }
}
