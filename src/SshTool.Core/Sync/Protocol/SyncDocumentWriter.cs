using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using SshTool.Core.Models;

namespace SshTool.Core.Sync.Protocol
{
    // 03-SYNC-PROTOCOL.md §4.2：键序固定（§4.1 书写顺序）、数组按 id 序数排序、
    // UTF-8 无 BOM、无缩进、>2 MiB 拒绝；写出前必须通过 SyncDocumentValidator。
    public static class SyncDocumentWriter
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static string Write(SyncDocumentV1 doc)
        {
            SyncDocumentValidator.Validate(doc);
            string json = BuildJson(doc, omitUpdatedAt: false);
            if (Utf8NoBom.GetByteCount(json) > SyncConstants.DocumentMaxBytes)
            {
                throw new SyncDocumentInvalidException("$", "文档超过 2 MiB，拒绝上传");
            }
            return json;
        }

        public static byte[] WriteUtf8(SyncDocumentV1 doc)
        {
            return Utf8NoBom.GetBytes(Write(doc));
        }

        // §7.3 SameContent：忽略 updatedAt 的语义等价比较（其余字节级一致）。
        public static bool SameContent(SyncDocumentV1 a, SyncDocumentV1 b)
        {
            if (a == null || b == null)
            {
                return a == b;
            }
            return string.Equals(BuildJson(a, true), BuildJson(b, true), StringComparison.Ordinal);
        }

        private static string BuildJson(SyncDocumentV1 doc, bool omitUpdatedAt)
        {
            var servers = new List<ServerRecord>(doc.Servers);
            servers.Sort((x, y) => string.CompareOrdinal(x.Profile.Id, y.Profile.Id));
            var tunnels = new List<TunnelRecord>(doc.Tunnels);
            tunnels.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
            var groups = new List<GroupRecord>(doc.Groups);
            groups.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));

            var sb = new StringBuilder();
            using (var sw = new StringWriter(sb, CultureInfo.InvariantCulture))
            using (var w = new JsonTextWriter(sw))
            {
                w.WriteStartObject();
                w.WritePropertyName("schemaVersion");
                w.WriteValue(doc.SchemaVersion);
                if (!omitUpdatedAt)
                {
                    w.WritePropertyName("updatedAt");
                    w.WriteValue(doc.UpdatedAt);
                }
                w.WritePropertyName("preferences");
                w.WriteStartObject();
                w.WritePropertyName("syncPasswords");
                w.WriteValue(doc.Preferences.SyncPasswords);
                w.WritePropertyName("syncPrivateKeys");
                w.WriteValue(doc.Preferences.SyncPrivateKeys);
                w.WriteEndObject();

                w.WritePropertyName("servers");
                w.WriteStartArray();
                foreach (var server in servers)
                {
                    WriteServer(w, server);
                }
                w.WriteEndArray();

                w.WritePropertyName("tunnels");
                w.WriteStartArray();
                foreach (var tunnel in tunnels)
                {
                    WriteTunnel(w, tunnel);
                }
                w.WriteEndArray();

                w.WritePropertyName("groups");
                w.WriteStartArray();
                foreach (var group in groups)
                {
                    WriteGroup(w, group);
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return sb.ToString();
        }

        private static void WriteServer(JsonTextWriter w, ServerRecord server)
        {
            var p = server.Profile;
            w.WriteStartObject();
            w.WritePropertyName("profile");
            w.WriteStartObject();
            w.WritePropertyName("id");
            w.WriteValue(p.Id);
            w.WritePropertyName("name");
            w.WriteValue(p.Name);
            w.WritePropertyName("host");
            w.WriteValue(p.Host);
            w.WritePropertyName("port");
            w.WriteValue(p.Port);
            w.WritePropertyName("username");
            w.WriteValue(p.Username);
            w.WritePropertyName("authType");
            w.WriteValue(AuthTypeJson(p.AuthType));
            w.WritePropertyName("hostFingerprint");
            w.WriteValue(p.HostFingerprint);
            w.WritePropertyName("keepalive");
            w.WriteValue(p.Keepalive);
            w.WriteEndObject();

            var s = server.Secrets;
            bool hasSecrets = s != null
                && (s.Password != null || s.Passphrase != null || s.PrivateKey != null
                    || s.PrivateKeyEncoding != null || s.PrivateKeyFormat != null || s.PrivateKeyFingerprint != null);
            if (hasSecrets)
            {
                // §4.2：只写存在的键，固定顺序
                w.WritePropertyName("secrets");
                w.WriteStartObject();
                if (s.Password != null)
                {
                    w.WritePropertyName("password");
                    w.WriteValue(s.Password);
                }
                if (s.Passphrase != null)
                {
                    w.WritePropertyName("passphrase");
                    w.WriteValue(s.Passphrase);
                }
                if (s.PrivateKey != null)
                {
                    w.WritePropertyName("privateKey");
                    w.WriteValue(s.PrivateKey);
                }
                if (s.PrivateKeyEncoding != null)
                {
                    w.WritePropertyName("privateKeyEncoding");
                    w.WriteValue(s.PrivateKeyEncoding);
                }
                if (s.PrivateKeyFormat != null)
                {
                    w.WritePropertyName("privateKeyFormat");
                    w.WriteValue(s.PrivateKeyFormat);
                }
                if (s.PrivateKeyFingerprint != null)
                {
                    w.WritePropertyName("privateKeyFingerprint");
                    w.WriteValue(s.PrivateKeyFingerprint);
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }

        private static void WriteTunnel(JsonTextWriter w, TunnelRecord t)
        {
            w.WriteStartObject();
            w.WritePropertyName("id");
            w.WriteValue(t.Id);
            w.WritePropertyName("name");
            w.WriteValue(t.Name);
            w.WritePropertyName("serverId");
            w.WriteValue(t.ServerId);
            w.WritePropertyName("groupId");
            if (t.GroupId == null)
            {
                w.WriteNull();
            }
            else
            {
                w.WriteValue(t.GroupId);
            }
            w.WritePropertyName("type");
            w.WriteValue(TunnelTypeJson(t.Type));
            w.WritePropertyName("listenHost");
            w.WriteValue(t.ListenHost);
            w.WritePropertyName("listenPort");
            w.WriteValue(t.ListenPort);
            w.WritePropertyName("destHost");
            w.WriteValue(t.DestHost);
            w.WritePropertyName("destPort");
            w.WriteValue(t.DestPort);
            w.WritePropertyName("destServerId");
            w.WriteValue(t.DestServerId);
            w.WritePropertyName("autoReconnect");
            w.WriteValue(t.AutoReconnect);
            w.WritePropertyName("enabled");
            w.WriteValue(t.Enabled);
            w.WriteEndObject();
        }

        private static void WriteGroup(JsonTextWriter w, GroupRecord g)
        {
            w.WriteStartObject();
            w.WritePropertyName("id");
            w.WriteValue(g.Id);
            w.WritePropertyName("name");
            w.WriteValue(g.Name);
            w.WritePropertyName("color");
            w.WriteValue(g.Color);
            w.WriteEndObject();
        }

        private static string AuthTypeJson(AuthType value)
        {
            switch (value)
            {
                case AuthType.Password: return "password";
                case AuthType.Key: return "key";
                case AuthType.Agent: return "agent";
                default: throw new SyncDocumentInvalidException("authType", "非法值 " + (int)value);
            }
        }

        private static string TunnelTypeJson(TunnelType value)
        {
            switch (value)
            {
                case TunnelType.Local: return "local";
                case TunnelType.Remote: return "remote";
                case TunnelType.Dynamic: return "dynamic";
                case TunnelType.Relay: return "relay";
                default: throw new SyncDocumentInvalidException("type", "非法值 " + (int)value);
            }
        }
    }
}
