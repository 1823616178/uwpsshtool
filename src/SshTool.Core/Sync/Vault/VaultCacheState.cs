using System;
using Newtonsoft.Json.Linq;
using SshTool.Core.Sync.Api.Dtos;
using SshTool.Core.Sync.Protocol;
using SshTool.Core.Storage;

namespace SshTool.Core.Sync.Vault
{
    // 03-SYNC-PROTOCOL.md §6.2 VaultCacheState（对应桌面端 vault-cache.ts）。
    // 同步偏好（4 键）：enabled/autoSync 是本机偏好（只存 VaultCache，不进文档）；
    // syncPasswords/syncPrivateKeys 同时进文档 preferences（见 §4.1/§5.1）。
    public sealed class SyncPreferences
    {
        public bool Enabled { get; set; }

        public bool AutoSync { get; set; } = true;

        public bool SyncPasswords { get; set; }

        public bool SyncPrivateKeys { get; set; }

        public static SyncPreferences Defaults()
        {
            return new SyncPreferences
            {
                Enabled = false,
                AutoSync = true,
                SyncPasswords = false,
                SyncPrivateKeys = false
            };
        }

        public SyncPreferences Clone()
        {
            return new SyncPreferences
            {
                Enabled = Enabled,
                AutoSync = AutoSync,
                SyncPasswords = SyncPasswords,
                SyncPrivateKeys = SyncPrivateKeys
            };
        }

        public JObject ToJson()
        {
            return new JObject
            {
                ["enabled"] = Enabled,
                ["autoSync"] = AutoSync,
                ["syncPasswords"] = SyncPasswords,
                ["syncPrivateKeys"] = SyncPrivateKeys
            };
        }

        public static SyncPreferences Parse(JObject o)
        {
            if (o == null)
            {
                throw new ProtocolParseException("preferences", "应为对象");
            }
            return new SyncPreferences
            {
                Enabled = DtoReader.Bool(o, "enabled", "preferences.enabled"),
                AutoSync = DtoReader.Bool(o, "autoSync", "preferences.autoSync"),
                SyncPasswords = DtoReader.Bool(o, "syncPasswords", "preferences.syncPasswords"),
                SyncPrivateKeys = DtoReader.Bool(o, "syncPrivateKeys", "preferences.syncPrivateKeys")
            };
        }
    }

    // §6.2 持久状态快照。baseDocument / conflictRemoteDocument / pendingUpload.document
    // 一律经 S02 Reader/Writer 编解码（同步零偏差同样适用于本地快照）。
    // 绑定用户：登录用户 id 与 UserId 不同 → 整个缓存重置（只保留 preferences 默认值）。
    // Lock()：VaultKey 置 null（其余保留）。Clear()：重置为初始（保留 UserId 由调用方决定）。
    public sealed class VaultCacheState
    {
        public string UserId { get; set; }

        public string VaultId { get; set; }

        // 标准 Base64 的 32 字节 vaultKey；锁定时为 null。
        public string VaultKeyBase64 { get; set; }

        // 0 表示未知（尚未探测到云端保险库）。
        public int KeyVersion { get; set; }

        // u64 十进制字符串（踩坑 #8：只做字符串比较，不转 double/int）。
        public string Revision { get; set; } = "0";

        public SyncPreferences Preferences { get; set; } = SyncPreferences.Defaults();

        public SyncDocumentV1 BaseDocument { get; set; }

        public bool Dirty { get; set; }

        public string LastSyncedAt { get; set; }

        public PendingVaultSetup PendingVaultSetup { get; set; }

        public PendingUpload PendingUpload { get; set; }

        public SyncConflictSummary Conflict { get; set; }

        public SyncDocumentV1 ConflictRemoteDocument { get; set; }

        public string ConflictRemoteRevision { get; set; }

        public static VaultCacheState Initial()
        {
            return new VaultCacheState
            {
                UserId = null,
                VaultId = null,
                VaultKeyBase64 = null,
                KeyVersion = 0,
                Revision = "0",
                Preferences = SyncPreferences.Defaults(),
                BaseDocument = null,
                Dirty = false,
                LastSyncedAt = null,
                PendingVaultSetup = null,
                PendingUpload = null,
                Conflict = null,
                ConflictRemoteDocument = null,
                ConflictRemoteRevision = null
            };
        }

        public VaultCacheState Clone()
        {
            return new VaultCacheState
            {
                UserId = UserId,
                VaultId = VaultId,
                VaultKeyBase64 = VaultKeyBase64,
                KeyVersion = KeyVersion,
                Revision = Revision,
                Preferences = Preferences == null ? SyncPreferences.Defaults() : Preferences.Clone(),
                BaseDocument = BaseDocument == null ? null : BaseDocument.Clone(),
                Dirty = Dirty,
                LastSyncedAt = LastSyncedAt,
                PendingVaultSetup = PendingVaultSetup == null ? null : PendingVaultSetup.Clone(),
                PendingUpload = PendingUpload == null ? null : PendingUpload.Clone(),
                Conflict = Conflict == null ? null : Conflict.Clone(),
                ConflictRemoteDocument = ConflictRemoteDocument == null ? null : ConflictRemoteDocument.Clone(),
                ConflictRemoteRevision = ConflictRemoteRevision
            };
        }

        // O15：快照读取，但跳过 BaseDocument / PendingUpload / ConflictRemoteDocument
        // 三份完整文档树的深拷贝。用于绝大多数只关心状态标量与元数据（Preferences/UserId/VaultId/Revision 等）的调用点。
        public VaultCacheState CloneWithoutDocuments()
        {
            return new VaultCacheState
            {
                UserId = UserId,
                VaultId = VaultId,
                VaultKeyBase64 = VaultKeyBase64,
                KeyVersion = KeyVersion,
                Revision = Revision,
                Preferences = Preferences == null ? SyncPreferences.Defaults() : Preferences.Clone(),
                BaseDocument = null,
                Dirty = Dirty,
                LastSyncedAt = LastSyncedAt,
                PendingVaultSetup = PendingVaultSetup == null ? null : PendingVaultSetup.Clone(),
                PendingUpload = null,
                Conflict = Conflict == null ? null : Conflict.Clone(),
                ConflictRemoteDocument = null,
                ConflictRemoteRevision = ConflictRemoteRevision
            };
        }

        public JObject ToJson()
        {
            return new JObject
            {
                ["version"] = 1,
                ["userId"] = UserId,
                ["vaultId"] = VaultId,
                ["vaultKey"] = VaultKeyBase64,
                ["keyVersion"] = KeyVersion,
                ["revision"] = Revision,
                ["preferences"] = Preferences == null ? SyncPreferences.Defaults().ToJson() : Preferences.ToJson(),
                ["baseDocument"] = WriteDocument(BaseDocument),
                ["dirty"] = Dirty,
                ["lastSyncedAt"] = LastSyncedAt,
                ["pendingVaultSetup"] = PendingVaultSetup == null ? null : PendingVaultSetup.ToJson(),
                ["pendingUpload"] = PendingUpload == null ? null : PendingUpload.ToJson(),
                ["conflict"] = Conflict == null ? null : Conflict.ToJson(),
                ["conflictRemoteDocument"] = WriteDocument(ConflictRemoteDocument),
                ["conflictRemoteRevision"] = ConflictRemoteRevision
            };
        }

        private static JObject WriteDocument(SyncDocumentV1 document)
        {
            if (document == null)
            {
                return null;
            }
            return JsonText.ParseObject(SyncDocumentWriter.Write(document));
        }

        private static SyncDocumentV1 ReadDocument(JToken token, string path)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }
            var o = token as JObject;
            if (o == null)
            {
                throw new ProtocolParseException(path, "应为对象或 null");
            }
            return SyncDocumentReader.Read(o.ToString());
        }

        private static JObject ReadOptionalObject(JObject parent, string key, string path)
        {
            var token = parent[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }
            var o = token as JObject;
            if (o == null)
            {
                throw new ProtocolParseException(path, "应为对象或 null");
            }
            return o;
        }

        // 严格解码：任一嵌套段非法 → 整份视为损坏（调用方重置为初始，fail closed）。
        public static VaultCacheState Parse(JObject root)
        {
            if (root == null)
            {
                throw new ProtocolParseException("$", "缓存不是对象");
            }
            var version = root["version"];
            if (version == null || version.Type != JTokenType.Integer || (long)version != 1)
            {
                throw new ProtocolParseException("version", "仅支持 version=1");
            }
            var state = Initial();
            state.UserId = OptionalString(root, "userId");
            state.VaultId = OptionalString(root, "vaultId");
            state.VaultKeyBase64 = OptionalString(root, "vaultKey");
            state.KeyVersion = OptionalInt(root, "keyVersion", 0);
            var revision = OptionalString(root, "revision");
            state.Revision = string.IsNullOrEmpty(revision) ? "0" : revision;
            var preferences = ReadOptionalObject(root, "preferences", "preferences");
            if (preferences != null)
            {
                state.Preferences = SyncPreferences.Parse(preferences);
            }
            state.BaseDocument = ReadDocument(root["baseDocument"], "baseDocument");
            state.Dirty = OptionalBool(root, "dirty", false);
            state.LastSyncedAt = OptionalString(root, "lastSyncedAt");
            var pendingSetup = ReadOptionalObject(root, "pendingVaultSetup", "pendingVaultSetup");
            if (pendingSetup != null)
            {
                state.PendingVaultSetup = PendingVaultSetup.Parse(pendingSetup);
            }
            var pendingUpload = ReadOptionalObject(root, "pendingUpload", "pendingUpload");
            if (pendingUpload != null)
            {
                state.PendingUpload = PendingUpload.Parse(pendingUpload);
            }
            var conflict = ReadOptionalObject(root, "conflict", "conflict");
            if (conflict != null)
            {
                state.Conflict = SyncConflictSummary.Parse(conflict);
            }
            state.ConflictRemoteDocument = ReadDocument(root["conflictRemoteDocument"], "conflictRemoteDocument");
            state.ConflictRemoteRevision = OptionalString(root, "conflictRemoteRevision");
            return state;
        }

        private static string OptionalString(JObject parent, string key)
        {
            var token = parent[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }
            if (token.Type != JTokenType.String)
            {
                throw new ProtocolParseException(key, "应为字符串或 null");
            }
            return (string)token;
        }

        private static int OptionalInt(JObject parent, string key, int fallback)
        {
            var token = parent[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                return fallback;
            }
            if (token.Type != JTokenType.Integer)
            {
                throw new ProtocolParseException(key, "应为整数");
            }
            long value = (long)token;
            if (value < int.MinValue || value > int.MaxValue)
            {
                throw new ProtocolParseException(key, "整数超出 Int32 范围");
            }
            return (int)value;
        }

        private static bool OptionalBool(JObject parent, string key, bool fallback)
        {
            var token = parent[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                return fallback;
            }
            if (token.Type != JTokenType.Boolean)
            {
                throw new ProtocolParseException(key, "应为布尔");
            }
            return (bool)token;
        }
    }
}
