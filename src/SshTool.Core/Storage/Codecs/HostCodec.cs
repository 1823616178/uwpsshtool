using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SshTool.Core.Models;

namespace SshTool.Core.Storage.Codecs
{
    // §8.1 Host：C# HostName ↔ JSON "host"；authType 枚举 ↔ password/key/agent。
    public sealed class HostCodec : IEntityCodec<Host>
    {
        private static readonly ISet<string> KnownKeys = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "id", "name", "host", "port", "username", "authType", "hostFingerprint", "keepalive",
            "groupId", "keyId", "appearanceId", "jumpHostId", "initCommands", "envVars", "termType",
            "tmuxAutoAttach", "tmuxSessionName", "backspaceSendsCtrlH", "sortOrder", "lastConnectedAt"
        };

        public string GetId(Host item)
        {
            return item.Id;
        }

        public JObject Encode(Host item)
        {
            var o = new JObject();
            o.Set("id", item.Id);
            o.Set("name", item.Name);
            o.Set("host", item.HostName);
            o.Set("port", item.Port);
            o.Set("username", item.Username);
            o.Set("authType", AuthTypeToJson(item.AuthType));
            o.Set("hostFingerprint", item.HostFingerprint);
            o.Set("keepalive", item.Keepalive);
            o.Set("groupId", item.GroupId);
            o.Set("keyId", item.KeyId);
            o.Set("appearanceId", item.AppearanceId);
            o.Set("jumpHostId", item.JumpHostId);
            o.Set("initCommands", item.InitCommands);
            o.Set("envVars", item.EnvVars);
            o.Set("termType", item.TermType);
            o.Set("tmuxAutoAttach", item.TmuxAutoAttach);
            o.Set("tmuxSessionName", item.TmuxSessionName);
            o.Set("backspaceSendsCtrlH", item.BackspaceSendsCtrlH);
            o.Set("sortOrder", item.SortOrder);
            o.Set("lastConnectedAt", item.LastConnectedAt);
            o.MergeExtra(item.Extra);
            return o;
        }

        public Host Decode(JObject json)
        {
            var item = new Host();
            item.Id = json.GetString("id");
            item.Name = json.GetString("name");
            item.HostName = json.GetString("host");
            item.Port = json.GetInt("port");
            item.Username = json.GetString("username");
            item.AuthType = AuthTypeFromJson(json.GetString("authType"));
            item.HostFingerprint = json.GetString("hostFingerprint");
            item.Keepalive = json.GetInt("keepalive");
            item.GroupId = json.GetString("groupId");
            item.KeyId = json.GetString("keyId");
            item.AppearanceId = json.GetString("appearanceId");
            item.JumpHostId = json.GetString("jumpHostId");
            item.InitCommands = json.GetStringList("initCommands");
            item.EnvVars = json.GetStringMap("envVars");
            item.TermType = json.GetString("termType");
            item.TmuxAutoAttach = json.GetBool("tmuxAutoAttach");
            item.TmuxSessionName = json.GetString("tmuxSessionName");
            item.BackspaceSendsCtrlH = json.GetBool("backspaceSendsCtrlH");
            item.SortOrder = json.GetInt("sortOrder");
            item.LastConnectedAt = json.GetString("lastConnectedAt");
            item.Extra = json.ExtractExtra(KnownKeys);
            return item;
        }

        private static string AuthTypeToJson(AuthType value)
        {
            switch (value)
            {
                case AuthType.Password: return "password";
                case AuthType.Key: return "key";
                case AuthType.Agent: return "agent";
                default: throw new JsonException("未知 authType: " + value);
            }
        }

        private static AuthType AuthTypeFromJson(string value)
        {
            if (value == null)
            {
                return AuthType.Password;
            }
            switch (value)
            {
                case "password": return AuthType.Password;
                case "key": return AuthType.Key;
                case "agent": return AuthType.Agent;
                default: throw new JsonException("未知 authType: " + value);
            }
        }
    }
}
