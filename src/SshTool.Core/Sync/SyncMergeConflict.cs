using Newtonsoft.Json.Linq;

namespace SshTool.Core.Sync
{
    public enum SyncMergeEntity
    {
        Server,
        Tunnel,
        Group,
        Settings
    }

    public enum SyncMergeKind
    {
        Field,
        AddAdd,
        DeleteModify
    }

    // 03-SYNC-PROTOCOL.md §8：冲突记录 entity/id/field/sensitive/kind，绝不含值（日志与 UI 安全）。
    public sealed class SyncMergeConflict
    {
        public SyncMergeEntity Entity { get; set; }
        public string Id { get; set; }
        public string Field { get; set; }
        public bool Sensitive { get; set; }
        public SyncMergeKind Kind { get; set; }

        // 桌面端 SyncConflictSummary.fields[] 线形（kind 不进摘要，仅供本端诊断）
        public JObject ToJson()
        {
            return new JObject
            {
                ["entity"] = EntityJson(Entity),
                ["id"] = Id,
                ["field"] = Field,
                ["sensitive"] = Sensitive
            };
        }

        public static string EntityJson(SyncMergeEntity entity)
        {
            switch (entity)
            {
                case SyncMergeEntity.Server: return "server";
                case SyncMergeEntity.Tunnel: return "tunnel";
                case SyncMergeEntity.Group: return "group";
                default: return "settings";
            }
        }

        public static string KindJson(SyncMergeKind kind)
        {
            switch (kind)
            {
                case SyncMergeKind.AddAdd: return "add-add";
                case SyncMergeKind.DeleteModify: return "delete-modify";
                default: return "field";
            }
        }
    }
}
