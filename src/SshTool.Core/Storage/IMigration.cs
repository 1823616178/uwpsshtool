using Newtonsoft.Json.Linq;

namespace SshTool.Core.Storage
{
    // 01-DESIGN.md §8.4：schemaVersion 链式迁移。JsonStore 从文件版本起逐段找
    // From == 当前版本的迁移执行，直到追上存储当前版本；缺段按损坏文件处理（备份 + 空集合）。
    public interface IMigration
    {
        int From { get; }
        int To { get; }
        void Apply(JObject document);
    }
}
