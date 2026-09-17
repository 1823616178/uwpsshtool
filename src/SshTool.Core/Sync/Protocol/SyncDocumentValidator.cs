using System.Collections.Generic;
using System.Text.RegularExpressions;
using SshTool.Core.Models;

namespace SshTool.Core.Sync.Protocol
{
    // 03-SYNC-PROTOCOL.md §4.1：SyncDocumentV1 的全部值约束与跨条目引用约束，
    // 错误带 JSON 路径（Reader 结构校验通过后调用；Writer 写出前必须通过）。
    // 与桌面端 sync-schemas.ts 的 zod schema 一条不差；私钥指纹复算属入站额外校验（§4.1 末段），
    // 需要 native KeyTool（S10），不在本层。
    public static class SyncDocumentValidator
    {
        private static readonly Regex UpdatedAtFormat = new Regex(
            @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", RegexOptions.Compiled);
        private static readonly Regex GroupColor = new Regex(
            @"^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

        public static void Validate(SyncDocumentV1 doc)
        {
            if (doc == null)
            {
                throw new SyncDocumentInvalidException("$", "文档为空");
            }
            if (doc.SchemaVersion != SyncConstants.SchemaVersion)
            {
                throw new SyncDocumentInvalidException("schemaVersion",
                    "只支持 1，实际 " + doc.SchemaVersion);
            }
            if (doc.UpdatedAt == null || !UpdatedAtFormat.IsMatch(doc.UpdatedAt))
            {
                throw new SyncDocumentInvalidException("updatedAt",
                    "必须是 yyyy-MM-ddTHH:mm:ss.fffZ 形式");
            }
            if (doc.Preferences == null)
            {
                throw new SyncDocumentInvalidException("preferences", "缺失");
            }
            if (doc.Servers == null)
            {
                throw new SyncDocumentInvalidException("servers", "缺失");
            }
            if (doc.Tunnels == null)
            {
                throw new SyncDocumentInvalidException("tunnels", "缺失");
            }
            if (doc.Groups == null)
            {
                throw new SyncDocumentInvalidException("groups", "缺失");
            }
            if (doc.Servers.Count > SyncConstants.ServersMax)
            {
                throw new SyncDocumentInvalidException("servers", "最多 " + SyncConstants.ServersMax + " 条");
            }
            if (doc.Tunnels.Count > SyncConstants.TunnelsMax)
            {
                throw new SyncDocumentInvalidException("tunnels", "最多 " + SyncConstants.TunnelsMax + " 条");
            }
            if (doc.Groups.Count > SyncConstants.GroupsMax)
            {
                throw new SyncDocumentInvalidException("groups", "最多 " + SyncConstants.GroupsMax + " 条");
            }

            for (int i = 0; i < doc.Servers.Count; i++)
            {
                ValidateServer(doc.Servers[i], "servers[" + i + "]");
            }
            for (int i = 0; i < doc.Tunnels.Count; i++)
            {
                ValidateTunnel(doc.Tunnels[i], "tunnels[" + i + "]");
            }
            for (int i = 0; i < doc.Groups.Count; i++)
            {
                ValidateGroup(doc.Groups[i], "groups[" + i + "]");
            }

            // 跨条目引用（§4.1 superRefine）
            var serverIds = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var s in doc.Servers)
            {
                serverIds.Add(s.Profile.Id);
            }
            var groupIds = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var g in doc.Groups)
            {
                groupIds.Add(g.Id);
            }
            for (int i = 0; i < doc.Tunnels.Count; i++)
            {
                var t = doc.Tunnels[i];
                string path = "tunnels[" + i + "]";
                if (!serverIds.Contains(t.ServerId))
                {
                    throw new SyncDocumentInvalidException(path + ".serverId", "隧道引用了不存在的服务器");
                }
                if (t.GroupId != null && !groupIds.Contains(t.GroupId))
                {
                    throw new SyncDocumentInvalidException(path + ".groupId", "隧道引用了不存在的分组");
                }
                if (t.Type == TunnelType.Relay && !serverIds.Contains(t.DestServerId ?? ""))
                {
                    throw new SyncDocumentInvalidException(path + ".destServerId", "中转隧道引用了不存在的目标服务器");
                }
            }
        }

        private static void ValidateServer(ServerRecord server, string path)
        {
            if (server == null)
            {
                throw new SyncDocumentInvalidException(path, "条目为 null");
            }
            ValidateProfile(server.Profile, path + ".profile");
            if (server.Secrets != null)
            {
                ValidateSecrets(server.Secrets, path + ".secrets");
            }
        }

        private static void ValidateProfile(PortableServerProfile p, string path)
        {
            if (p == null)
            {
                throw new SyncDocumentInvalidException(path, "缺失");
            }
            RequireNonEmpty(p.Id, path + ".id");
            RequireLength(p.Name, path + ".name", 1, 255);
            RequireLength(p.Host, path + ".host", 1, 1024);
            RequireRange(p.Port, path + ".port", 1, 65535);
            RequireLength(p.Username, path + ".username", 1, 255);
            switch (p.AuthType)
            {
                case AuthType.Password:
                case AuthType.Key:
                case AuthType.Agent:
                    break;
                default:
                    throw new SyncDocumentInvalidException(path + ".authType", "非法值 " + (int)p.AuthType);
            }
            RequireLength(p.HostFingerprint, path + ".hostFingerprint", 0, 255);
            RequireRange(p.Keepalive, path + ".keepalive", 0, 3600);
        }

        private static void ValidateSecrets(ServerSecrets s, string path)
        {
            RequireLength(s.Password, path + ".password", 0, 4096, optional: true);
            RequireLength(s.Passphrase, path + ".passphrase", 0, 4096, optional: true);

            // §4.1 私钥元数据规则：privateKey 缺失时三个元数据键都不得出现；出现时都必须出现。
            if (s.PrivateKey == null)
            {
                if (s.PrivateKeyEncoding != null || s.PrivateKeyFormat != null || s.PrivateKeyFingerprint != null)
                {
                    throw new SyncDocumentInvalidException(path + ".privateKey", "私钥元数据不能脱离 privateKey 存在");
                }
                return;
            }
            if (s.PrivateKeyEncoding == null || s.PrivateKeyFormat == null || s.PrivateKeyFingerprint == null)
            {
                throw new SyncDocumentInvalidException(path + ".privateKey",
                    "privateKey 必须同时包含 encoding、format 和 fingerprint");
            }
            if (s.PrivateKeyEncoding != PrivateKeyFormat.EncodingBase64)
            {
                throw new SyncDocumentInvalidException(path + ".privateKeyEncoding", "只支持 base64");
            }
            if (s.PrivateKeyFormat != PrivateKeyFormat.OpenSsh && s.PrivateKeyFormat != PrivateKeyFormat.Pem)
            {
                throw new SyncDocumentInvalidException(path + ".privateKeyFormat", "只支持 openssh/pem");
            }
            if (!PrivateKeyFormat.IsValidFingerprint(s.PrivateKeyFingerprint))
            {
                throw new SyncDocumentInvalidException(path + ".privateKeyFingerprint", "指纹格式非法");
            }
            if (s.PrivateKey.Length > CanonicalBase64.MaxPrivateKeyBase64Length)
            {
                throw new SyncDocumentInvalidException(path + ".privateKey", "Base64 长度超限");
            }
            byte[] decoded = CanonicalBase64.TryDecode(s.PrivateKey);
            if (decoded == null)
            {
                throw new SyncDocumentInvalidException(path + ".privateKey", "必须是规范 Base64");
            }
            if (decoded.Length > SyncConstants.PrivateKeyMaxBytes)
            {
                throw new SyncDocumentInvalidException(path + ".privateKey", "私钥解码后不能超过 256 KiB");
            }
            string actualFormat = PrivateKeyFormat.Detect(decoded);
            if (actualFormat == null || actualFormat != s.PrivateKeyFormat)
            {
                throw new SyncDocumentInvalidException(path + ".privateKeyFormat", "私钥 header 与声明格式不一致");
            }
        }

        private static void ValidateTunnel(TunnelRecord t, string path)
        {
            if (t == null)
            {
                throw new SyncDocumentInvalidException(path, "条目为 null");
            }
            RequireNonEmpty(t.Id, path + ".id");
            RequireLength(t.Name, path + ".name", 1, 255);
            RequireNonEmpty(t.ServerId, path + ".serverId");
            if (t.GroupId != null && t.GroupId.Length == 0)
            {
                throw new SyncDocumentInvalidException(path + ".groupId", "必须是非空字符串或 null");
            }
            switch (t.Type)
            {
                case TunnelType.Local:
                case TunnelType.Remote:
                case TunnelType.Dynamic:
                case TunnelType.Relay:
                    break;
                default:
                    throw new SyncDocumentInvalidException(path + ".type", "非法值 " + (int)t.Type);
            }
            RequireLength(t.ListenHost, path + ".listenHost", 0, 1024);
            RequireRange(t.ListenPort, path + ".listenPort", 1, 65535);
            RequireLength(t.DestHost, path + ".destHost", 0, 1024);
            RequireRange(t.DestPort, path + ".destPort", 0, 65535);
            RequireLength(t.DestServerId, path + ".destServerId", 0, 255);
        }

        private static void ValidateGroup(GroupRecord g, string path)
        {
            if (g == null)
            {
                throw new SyncDocumentInvalidException(path, "条目为 null");
            }
            RequireNonEmpty(g.Id, path + ".id");
            RequireLength(g.Name, path + ".name", 1, 255);
            if (g.Color == null || !GroupColor.IsMatch(g.Color))
            {
                throw new SyncDocumentInvalidException(path + ".color", "必须匹配 ^#[0-9a-fA-F]{6}$");
            }
        }

        private static void RequireNonEmpty(string value, string path)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new SyncDocumentInvalidException(path, "非空字符串");
            }
        }

        private static void RequireLength(string value, string path, int min, int max, bool optional = false)
        {
            if (value == null)
            {
                if (optional)
                {
                    return;
                }
                throw new SyncDocumentInvalidException(path, "缺失或 null");
            }
            if (value.Length < min || value.Length > max)
            {
                throw new SyncDocumentInvalidException(path, "长度须 " + min + "–" + max + "，实际 " + value.Length);
            }
        }

        private static void RequireRange(int value, string path, int min, int max)
        {
            if (value < min || value > max)
            {
                throw new SyncDocumentInvalidException(path, "须 " + min + "–" + max + "，实际 " + value);
            }
        }
    }
}
