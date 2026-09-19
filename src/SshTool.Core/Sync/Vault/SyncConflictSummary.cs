using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SshTool.Core.Sync.Api.Dtos;

namespace SshTool.Core.Sync.Vault
{
    // 03-SYNC-PROTOCOL.md §6.2 conflict（对应桌面端 SyncConflictSummary，见 sync-types.ts）。
    // S11 只定义模型与编解码；SaveConflict/ResolveConflict 在 S12b 落地。
    // 脱敏：冲突信息只含实体/id/字段名与是否敏感，绝不含字段值（见 §10.1 Coordinator 用例 3 的 Merge 要求）。
    public enum SyncConflictReason
    {
        InitialImport,
        RemoteDeletion,
        MergeConflict
    }

    public enum SyncConflictEntity
    {
        Server,
        Tunnel,
        Group,
        Settings
    }

    public sealed class SyncConflictField
    {
        public SyncConflictEntity Entity { get; set; }

        public string Id { get; set; }

        public string Field { get; set; }

        public bool Sensitive { get; set; }

        public SyncConflictField Clone()
        {
            return new SyncConflictField
            {
                Entity = Entity,
                Id = Id,
                Field = Field,
                Sensitive = Sensitive
            };
        }
    }

    public sealed class SyncRemoteSummary
    {
        public int Servers { get; set; }

        public int Tunnels { get; set; }

        public int Groups { get; set; }

        public bool IncludesPasswords { get; set; }

        public bool IncludesPrivateKeys { get; set; }

        public SyncRemoteSummary Clone()
        {
            return new SyncRemoteSummary
            {
                Servers = Servers,
                Tunnels = Tunnels,
                Groups = Groups,
                IncludesPasswords = IncludesPasswords,
                IncludesPrivateKeys = IncludesPrivateKeys
            };
        }
    }

    public sealed class SyncConflictSummary
    {
        public SyncConflictReason Reason { get; set; }

        public string LocalRevision { get; set; }

        public string RemoteRevision { get; set; }

        public string LocalUpdatedAt { get; set; }

        public string RemoteUpdatedAt { get; set; }

        public SyncRemoteSummary RemoteSummary { get; set; }

        public List<SyncConflictField> Fields { get; set; } = new List<SyncConflictField>();

        public SyncConflictSummary Clone()
        {
            var copy = new SyncConflictSummary
            {
                Reason = Reason,
                LocalRevision = LocalRevision,
                RemoteRevision = RemoteRevision,
                LocalUpdatedAt = LocalUpdatedAt,
                RemoteUpdatedAt = RemoteUpdatedAt,
                RemoteSummary = RemoteSummary == null ? null : RemoteSummary.Clone(),
                Fields = new List<SyncConflictField>(Fields.Count)
            };
            foreach (var field in Fields)
            {
                copy.Fields.Add(field == null ? null : field.Clone());
            }
            return copy;
        }

        public static string ReasonToJson(SyncConflictReason reason)
        {
            switch (reason)
            {
                case SyncConflictReason.InitialImport: return "initial-import";
                case SyncConflictReason.RemoteDeletion: return "remote-deletion";
                case SyncConflictReason.MergeConflict: return "merge-conflict";
                default: throw new InvalidOperationException("未知冲突原因 " + (int)reason);
            }
        }

        public static SyncConflictReason ReasonFromJson(string value)
        {
            if (string.Equals(value, "initial-import", StringComparison.Ordinal))
            {
                return SyncConflictReason.InitialImport;
            }
            if (string.Equals(value, "remote-deletion", StringComparison.Ordinal))
            {
                return SyncConflictReason.RemoteDeletion;
            }
            if (string.Equals(value, "merge-conflict", StringComparison.Ordinal))
            {
                return SyncConflictReason.MergeConflict;
            }
            throw new ProtocolParseException("conflict.reason", "未知冲突原因");
        }

        public static string EntityToJson(SyncConflictEntity entity)
        {
            switch (entity)
            {
                case SyncConflictEntity.Server: return "server";
                case SyncConflictEntity.Tunnel: return "tunnel";
                case SyncConflictEntity.Group: return "group";
                case SyncConflictEntity.Settings: return "settings";
                default: throw new InvalidOperationException("未知冲突实体 " + (int)entity);
            }
        }

        public static SyncConflictEntity EntityFromJson(string value)
        {
            if (string.Equals(value, "server", StringComparison.Ordinal)) return SyncConflictEntity.Server;
            if (string.Equals(value, "tunnel", StringComparison.Ordinal)) return SyncConflictEntity.Tunnel;
            if (string.Equals(value, "group", StringComparison.Ordinal)) return SyncConflictEntity.Group;
            if (string.Equals(value, "settings", StringComparison.Ordinal)) return SyncConflictEntity.Settings;
            throw new ProtocolParseException("conflict.fields.entity", "未知冲突实体");
        }

        public JObject ToJson()
        {
            var fields = new JArray();
            foreach (var field in Fields)
            {
                fields.Add(new JObject
                {
                    ["entity"] = EntityToJson(field.Entity),
                    ["id"] = field.Id,
                    ["field"] = field.Field,
                    ["sensitive"] = field.Sensitive
                });
            }
            return new JObject
            {
                ["reason"] = ReasonToJson(Reason),
                ["localRevision"] = LocalRevision,
                ["remoteRevision"] = RemoteRevision,
                ["localUpdatedAt"] = LocalUpdatedAt,
                ["remoteUpdatedAt"] = RemoteUpdatedAt,
                ["remoteSummary"] = new JObject
                {
                    ["servers"] = RemoteSummary.Servers,
                    ["tunnels"] = RemoteSummary.Tunnels,
                    ["groups"] = RemoteSummary.Groups,
                    ["includesPasswords"] = RemoteSummary.IncludesPasswords,
                    ["includesPrivateKeys"] = RemoteSummary.IncludesPrivateKeys
                },
                ["fields"] = fields
            };
        }

        public static SyncConflictSummary Parse(JObject o)
        {
            if (o == null)
            {
                throw new ProtocolParseException("conflict", "应为对象");
            }
            var summary = new SyncConflictSummary
            {
                Reason = ReasonFromJson(DtoReader.Str(o, "reason", "conflict.reason")),
                LocalRevision = DtoReader.Str(o, "localRevision", "conflict.localRevision"),
                RemoteRevision = DtoReader.Str(o, "remoteRevision", "conflict.remoteRevision"),
                LocalUpdatedAt = DtoReader.Str(o, "localUpdatedAt", "conflict.localUpdatedAt"),
                RemoteUpdatedAt = DtoReader.Str(o, "remoteUpdatedAt", "conflict.remoteUpdatedAt")
            };
            var remote = DtoReader.Obj(o, "remoteSummary", "conflict.remoteSummary");
            summary.RemoteSummary = new SyncRemoteSummary
            {
                Servers = DtoReader.Int(remote, "servers", "conflict.remoteSummary.servers"),
                Tunnels = DtoReader.Int(remote, "tunnels", "conflict.remoteSummary.tunnels"),
                Groups = DtoReader.Int(remote, "groups", "conflict.remoteSummary.groups"),
                IncludesPasswords = DtoReader.Bool(remote, "includesPasswords", "conflict.remoteSummary.includesPasswords"),
                IncludesPrivateKeys = DtoReader.Bool(remote, "includesPrivateKeys", "conflict.remoteSummary.includesPrivateKeys")
            };
            var fields = DtoReader.Arr(o, "fields", "conflict.fields");
            for (int i = 0; i < fields.Count; i++)
            {
                var item = DtoReader.ItemObj(fields[i], "conflict.fields[" + i + "]");
                summary.Fields.Add(new SyncConflictField
                {
                    Entity = EntityFromJson(DtoReader.Str(item, "entity", "conflict.fields[" + i + "].entity")),
                    Id = DtoReader.Str(item, "id", "conflict.fields[" + i + "].id"),
                    Field = DtoReader.Str(item, "field", "conflict.fields[" + i + "].field"),
                    Sensitive = DtoReader.Bool(item, "sensitive", "conflict.fields[" + i + "].sensitive")
                });
            }
            return summary;
        }
    }
}
