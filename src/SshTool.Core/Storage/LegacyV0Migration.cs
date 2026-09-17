using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SshTool.Core.Storage
{
    // D02 要点 3 的 no-op 示例：无 schemaVersion 的历史文件视为 v0，原样升级为 v1。
    public sealed class LegacyV0Migration : IMigration
    {
        public int From
        {
            get { return 0; }
        }

        public int To
        {
            get { return 1; }
        }

        public void Apply(JObject document)
        {
        }
    }

    internal static class StorageMigrations
    {
        // 全部数据文件共用的迁移链（当前只有 v0 → v1）。
        internal static readonly IReadOnlyList<IMigration> All = new IMigration[]
        {
            new LegacyV0Migration()
        };
    }
}
