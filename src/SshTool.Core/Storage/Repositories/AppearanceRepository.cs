using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Storage.Codecs;

namespace SshTool.Core.Storage.Repositories
{
    // §8.2：LocalFolder/data/appearances.json
    public sealed class AppearanceRepository : Repository<AppearanceProfile>
    {
        public const string DataFilePath = "data/appearances.json";

        public AppearanceRepository(IFileSystem fs, ILogger logger = null)
            : base(new JsonStore<AppearanceProfile>(fs, DataFilePath, new AppearanceCodec(), StorageMigrations.All, logger))
        {
        }
    }
}
