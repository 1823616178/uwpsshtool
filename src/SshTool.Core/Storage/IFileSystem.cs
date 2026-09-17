using System.Threading.Tasks;

namespace SshTool.Core.Storage
{
    // 01-DESIGN.md §8.2/D8：仓储落盘只依赖这四件套，路径为 '/' 分隔的相对路径
    // （App 侧相对 LocalFolder，测试侧为内存字典）。原子写 = WriteAllTextAsync(*.tmp) + MoveAndReplaceAsync。
    public interface IFileSystem
    {
        Task<bool> ExistsAsync(string path);
        Task<string> ReadAllTextAsync(string path);
        Task WriteAllTextAsync(string path, string contents);
        Task MoveAndReplaceAsync(string sourcePath, string targetPath);
    }
}
