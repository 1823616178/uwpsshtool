using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SshTool.Core.Models;
using SshTool.Core.Storage;

namespace SshTool.Core.Sync.Protocol
{
    // 03-SYNC-PROTOCOL.md §4.3：逐键严格校验 —— 未知键/缺键/类型不符（整数必须是整数 token）→
    // SyncDocumentInvalidException(path, reason)。结构通过后交 SyncDocumentValidator 做值与引用约束。
    public static class SyncDocumentReader
    {
        private static readonly string[] DocumentKeys =
            { "schemaVersion", "updatedAt", "preferences", "servers", "tunnels", "groups" };
        private static readonly string[] PreferencesKeys = { "syncPasswords", "syncPrivateKeys" };
        private static readonly string[] ServerKeys = { "profile", "secrets" };
        private static readonly string[] ProfileKeys =
            { "id", "name", "host", "port", "username", "authType", "hostFingerprint", "keepalive" };
        private static readonly string[] SecretsKeys =
            { "password", "passphrase", "privateKey", "privateKeyEncoding", "privateKeyFormat", "privateKeyFingerprint" };
        private static readonly string[] TunnelKeys =
            { "id", "name", "serverId", "groupId", "type", "listenHost", "listenPort",
              "destHost", "destPort", "destServerId", "autoReconnect", "enabled" };
        private static readonly string[] GroupKeys = { "id", "name", "color" };

        public static SyncDocumentV1 Read(byte[] utf8)
        {
            if (utf8 == null)
            {
                throw new SyncDocumentInvalidException("$", "内容为空");
            }
            return Read(new System.Text.UTF8Encoding(false).GetString(utf8));
        }

        public static SyncDocumentV1 Read(string json)
        {
            JObject root;
            try
            {
                root = JsonText.ParseObject(json);
            }
            catch (Exception ex)
            {
                throw new SyncDocumentInvalidException("$", "不是有效 JSON: " + ex.Message);
            }
            var doc = ReadDocument(root);
            SyncDocumentValidator.Validate(doc);
            return doc;
        }

        private static SyncDocumentV1 ReadDocument(JObject root)
        {
            RejectUnknownKeys(root, "$", DocumentKeys);
            RequireKeys(root, "$", DocumentKeys);

            var doc = new SyncDocumentV1();
            doc.SchemaVersion = Int(root, "schemaVersion", "schemaVersion");
            doc.UpdatedAt = Str(root, "updatedAt", "updatedAt");
            doc.Preferences = ReadPreferences(Obj(root, "preferences", "preferences"), "preferences");
            var servers = Arr(root, "servers", "servers");
            for (int i = 0; i < servers.Count; i++)
            {
                doc.Servers.Add(ReadServer(AsObject(servers[i], "servers[" + i + "]"), "servers[" + i + "]"));
            }
            var tunnels = Arr(root, "tunnels", "tunnels");
            for (int i = 0; i < tunnels.Count; i++)
            {
                doc.Tunnels.Add(ReadTunnel(AsObject(tunnels[i], "tunnels[" + i + "]"), "tunnels[" + i + "]"));
            }
            var groups = Arr(root, "groups", "groups");
            for (int i = 0; i < groups.Count; i++)
            {
                doc.Groups.Add(ReadGroup(AsObject(groups[i], "groups[" + i + "]"), "groups[" + i + "]"));
            }
            return doc;
        }

        private static SyncPreferencesV1 ReadPreferences(JObject o, string path)
        {
            RejectUnknownKeys(o, path, PreferencesKeys);
            RequireKeys(o, path, PreferencesKeys);
            return new SyncPreferencesV1
            {
                SyncPasswords = Bool(o, "syncPasswords", path + ".syncPasswords"),
                SyncPrivateKeys = Bool(o, "syncPrivateKeys", path + ".syncPrivateKeys")
            };
        }

        private static ServerRecord ReadServer(JObject o, string path)
        {
            RejectUnknownKeys(o, path, ServerKeys);
            RequireKeys(o, path, "profile");
            var record = new ServerRecord();
            record.Profile = ReadProfile(Obj(o, "profile", path + ".profile"), path + ".profile");
            var secretsToken = o["secrets"];
            if (secretsToken != null)
            {
                if (secretsToken.Type == JTokenType.Null)
                {
                    throw new SyncDocumentInvalidException(path + ".secrets", "应为对象或缺省，实际 Null");
                }
                record.Secrets = ReadSecrets(AsObject(secretsToken, path + ".secrets"), path + ".secrets");
            }
            return record;
        }

        private static PortableServerProfile ReadProfile(JObject o, string path)
        {
            RejectUnknownKeys(o, path, ProfileKeys);
            RequireKeys(o, path, ProfileKeys);
            return new PortableServerProfile
            {
                Id = Str(o, "id", path + ".id"),
                Name = Str(o, "name", path + ".name"),
                Host = Str(o, "host", path + ".host"),
                Port = Int(o, "port", path + ".port"),
                Username = Str(o, "username", path + ".username"),
                AuthType = AuthTypeOf(Str(o, "authType", path + ".authType"), path + ".authType"),
                HostFingerprint = Str(o, "hostFingerprint", path + ".hostFingerprint"),
                Keepalive = Int(o, "keepalive", path + ".keepalive")
            };
        }

        private static ServerSecrets ReadSecrets(JObject o, string path)
        {
            RejectUnknownKeys(o, path, SecretsKeys);
            // 六键全部可选，但出现即不得为 null（zod optional ≠ nullable）。
            return new ServerSecrets
            {
                Password = OptionalStr(o, "password", path + ".password"),
                Passphrase = OptionalStr(o, "passphrase", path + ".passphrase"),
                PrivateKey = OptionalStr(o, "privateKey", path + ".privateKey"),
                PrivateKeyEncoding = OptionalStr(o, "privateKeyEncoding", path + ".privateKeyEncoding"),
                PrivateKeyFormat = OptionalStr(o, "privateKeyFormat", path + ".privateKeyFormat"),
                PrivateKeyFingerprint = OptionalStr(o, "privateKeyFingerprint", path + ".privateKeyFingerprint")
            };
        }

        private static TunnelRecord ReadTunnel(JObject o, string path)
        {
            RejectUnknownKeys(o, path, TunnelKeys);
            RequireKeys(o, path, TunnelKeys);
            return new TunnelRecord
            {
                Id = Str(o, "id", path + ".id"),
                Name = Str(o, "name", path + ".name"),
                ServerId = Str(o, "serverId", path + ".serverId"),
                GroupId = StrOrNull(o, "groupId", path + ".groupId"),
                Type = TunnelTypeOf(Str(o, "type", path + ".type"), path + ".type"),
                ListenHost = Str(o, "listenHost", path + ".listenHost"),
                ListenPort = Int(o, "listenPort", path + ".listenPort"),
                DestHost = Str(o, "destHost", path + ".destHost"),
                DestPort = Int(o, "destPort", path + ".destPort"),
                DestServerId = Str(o, "destServerId", path + ".destServerId"),
                AutoReconnect = Bool(o, "autoReconnect", path + ".autoReconnect"),
                Enabled = Bool(o, "enabled", path + ".enabled")
            };
        }

        private static GroupRecord ReadGroup(JObject o, string path)
        {
            RejectUnknownKeys(o, path, GroupKeys);
            RequireKeys(o, path, GroupKeys);
            return new GroupRecord
            {
                Id = Str(o, "id", path + ".id"),
                Name = Str(o, "name", path + ".name"),
                Color = Str(o, "color", path + ".color")
            };
        }

        private static AuthType AuthTypeOf(string value, string path)
        {
            switch (value)
            {
                case "password": return AuthType.Password;
                case "key": return AuthType.Key;
                case "agent": return AuthType.Agent;
                default: throw new SyncDocumentInvalidException(path, "非法 authType: " + value);
            }
        }

        private static TunnelType TunnelTypeOf(string value, string path)
        {
            switch (value)
            {
                case "local": return TunnelType.Local;
                case "remote": return TunnelType.Remote;
                case "dynamic": return TunnelType.Dynamic;
                case "relay": return TunnelType.Relay;
                default: throw new SyncDocumentInvalidException(path, "非法 tunnel type: " + value);
            }
        }

        private static void RejectUnknownKeys(JObject o, string path, string[] allowed)
        {
            var set = new HashSet<string>(allowed, StringComparer.Ordinal);
            foreach (var p in o.Properties())
            {
                if (!set.Contains(p.Name))
                {
                    throw new SyncDocumentInvalidException(Path(path, p.Name), "未知键");
                }
            }
        }

        private static void RequireKeys(JObject o, string path, params string[] required)
        {
            foreach (var key in required)
            {
                if (o[key] == null)
                {
                    throw new SyncDocumentInvalidException(Path(path, key), "缺键");
                }
            }
        }

        private static string Path(string parent, string key)
        {
            return parent == "$" ? key : parent + "." + key;
        }

        private static JObject AsObject(JToken token, string path)
        {
            var o = token as JObject;
            if (o == null)
            {
                throw new SyncDocumentInvalidException(path, "应为对象，实际 " + token.Type);
            }
            return o;
        }

        private static JObject Obj(JObject parent, string key, string path)
        {
            return AsObject(parent[key], path);
        }

        private static JArray Arr(JObject parent, string key, string path)
        {
            var a = parent[key] as JArray;
            if (a == null)
            {
                throw new SyncDocumentInvalidException(path, "应为数组，实际 " + parent[key].Type);
            }
            return a;
        }

        private static string Str(JObject parent, string key, string path)
        {
            var t = parent[key];
            if (t == null || t.Type != JTokenType.String)
            {
                throw new SyncDocumentInvalidException(path, "应为 string，实际 " + (t == null ? "<缺>" : t.Type.ToString()));
            }
            return (string)t;
        }

        private static string OptionalStr(JObject parent, string key, string path)
        {
            var t = parent[key];
            if (t == null)
            {
                return null;
            }
            if (t.Type != JTokenType.String)
            {
                throw new SyncDocumentInvalidException(path, "应为 string，实际 " + t.Type);
            }
            return (string)t;
        }

        private static string StrOrNull(JObject parent, string key, string path)
        {
            var t = parent[key];
            if (t == null)
            {
                throw new SyncDocumentInvalidException(path, "缺键");
            }
            if (t.Type == JTokenType.Null)
            {
                return null;
            }
            if (t.Type != JTokenType.String)
            {
                throw new SyncDocumentInvalidException(path, "应为 string 或 null，实际 " + t.Type);
            }
            return (string)t;
        }

        private static int Int(JObject parent, string key, string path)
        {
            var t = parent[key];
            if (t == null || t.Type != JTokenType.Integer)
            {
                throw new SyncDocumentInvalidException(path, "应为整数，实际 " + (t == null ? "<缺>" : t.Type.ToString()));
            }
            return checked((int)(long)t);
        }

        private static bool Bool(JObject parent, string key, string path)
        {
            var t = parent[key];
            if (t == null || t.Type != JTokenType.Boolean)
            {
                throw new SyncDocumentInvalidException(path, "应为 bool，实际 " + (t == null ? "<缺>" : t.Type.ToString()));
            }
            return (bool)t;
        }
    }
}
