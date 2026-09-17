using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Storage.Codecs;

namespace SshTool.Core.Storage.Repositories
{
    // §8.2：LocalFolder/data/keys.json（仅元数据）
    public sealed class KeyRepository : Repository<KeyEntry>
    {
        public const string DataFilePath = "data/keys.json";

        public KeyRepository(IFileSystem fs, ILogger logger = null)
            : base(new JsonStore<KeyEntry>(fs, DataFilePath, new KeyEntryCodec(), StorageMigrations.All, logger))
        {
        }
    }
}
