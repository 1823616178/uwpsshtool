using System.Collections.Generic;
using SshTool.Core.Sync.Vault;

namespace SshTool.Core.Sync
{
    // U19 同步冲突页的实体名称查找契约（对应 §5.14 字段列表的「实体名称查找顺序」）。
    // App 层按主机/分组/隧道仓库实现本接口；Core 只依赖抽象（可单测）。
    public interface IEntityNameLookup
    {
        // 本机名（按实体 id 查）；找不到返回 null。
        string LocalName(SyncConflictEntity entity, string id);

        // 远端文档标题（本机场景下等价于远端字段；找不到返回 null）。
        string RemoteTitle(SyncConflictEntity entity, string id);
    }

    // U19 冲突字段的一行展示（脱敏：敏感字段不显示真实值）。
    public sealed class ConflictFieldSpec
    {
        // 实体显示名（本机名 → 远端文档 title → id 顺序）。
        public string EntityName { get; set; }

        // 字段标签：敏感字段为 resw 键 Sync_Conflict_SensitiveChanged 文案，否则为字段名。
        public string FieldLabel { get; set; }
    }

    // U19 ConflictPresenter（纯逻辑，可单测）。
    // 按 reason 产出标题/说明/按钮文案的 resw 键，并按「本机名 → 远端 title → id」顺序
    // 解析字段显示名；敏感字段显示「有变更」而非值（见 §10.1 Coordinator 用例 3 的脱敏要求）。
    public sealed class ConflictPresenter
    {
        // 三种 reason 的标题 / 说明 / 主按钮文案键（对应 §5.14）。
        public string ResolveTitle(SyncConflictReason reason)
        {
            switch (reason)
            {
                case SyncConflictReason.InitialImport:
                    return "Conflict_Title_InitialImport";
                case SyncConflictReason.RemoteDeletion:
                    return "Conflict_Title_RemoteDeletion";
                case SyncConflictReason.MergeConflict:
                    return "Conflict_Title_MergeConflict";
                default:
                    return "Conflict_Title_MergeConflict";
            }
        }

        public string ResolveDescription(SyncConflictReason reason)
        {
            switch (reason)
            {
                case SyncConflictReason.InitialImport:
                    return "Conflict_Description_InitialImport";
                case SyncConflictReason.RemoteDeletion:
                    return "Conflict_Description_RemoteDeletion";
                case SyncConflictReason.MergeConflict:
                    return "Conflict_Description_MergeConflict";
                default:
                    return "Conflict_Description_MergeConflict";
            }
        }

        // 主按钮（「采用」侧）文案键：initial-import 与 merge-conflict 为「使用云端」，
        // remote-deletion 为「应用删除」。
        public string ResolvePrimaryButton(SyncConflictReason reason)
        {
            switch (reason)
            {
                case SyncConflictReason.InitialImport:
                    return "Conflict_Button_UseRemote";
                case SyncConflictReason.RemoteDeletion:
                    return "Conflict_Button_ApplyDeletion";
                case SyncConflictReason.MergeConflict:
                    return "Conflict_Button_UseRemote";
                default:
                    return "Conflict_Button_UseRemote";
            }
        }

        // 次按钮（「保留本机」侧）文案键。
        public string ResolveSecondaryButton(SyncConflictReason reason)
        {
            switch (reason)
            {
                case SyncConflictReason.InitialImport:
                    return "Conflict_Button_KeepLocal";
                case SyncConflictReason.RemoteDeletion:
                    return "Conflict_Button_KeepLocal";
                case SyncConflictReason.MergeConflict:
                    return "Conflict_Button_KeepLocal";
                default:
                    return "Conflict_Button_KeepLocal";
            }
        }

        // 字段显示名查找顺序：本机名 → 远端文档 title → id。
        public string ResolveFieldEntityName(SyncConflictField field, IEntityNameLookup lookup)
        {
            if (field == null)
            {
                return string.Empty;
            }
            if (lookup != null)
            {
                string local = lookup.LocalName(field.Entity, field.Id);
                if (!string.IsNullOrEmpty(local))
                {
                    return local;
                }
                string remote = lookup.RemoteTitle(field.Entity, field.Id);
                if (!string.IsNullOrEmpty(remote))
                {
                    return remote;
                }
            }
            return field.Id ?? string.Empty;
        }

        // 字段标签：敏感字段显示「有变更」键，否则为字段名本身。
        public string ResolveFieldLabel(SyncConflictField field)
        {
            if (field == null)
            {
                return string.Empty;
            }
            return field.Sensitive ? "Sync_Conflict_SensitiveChanged" : (field.Field ?? string.Empty);
        }

        // 批量解析字段展示行（脱敏：敏感字段不含实际值）。
        public List<ConflictFieldSpec> FieldDisplayNames(
            IReadOnlyList<SyncConflictField> fields, IEntityNameLookup lookup)
        {
            var result = new List<ConflictFieldSpec>(fields == null ? 0 : fields.Count);
            if (fields == null)
            {
                return result;
            }
            foreach (var field in fields)
            {
                if (field == null)
                {
                    continue;
                }
                result.Add(new ConflictFieldSpec
                {
                    EntityName = ResolveFieldEntityName(field, lookup),
                    FieldLabel = ResolveFieldLabel(field)
                });
            }
            return result;
        }
    }
}
