using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Storage.Codecs;

namespace SshTool.Core.Storage.Repositories
{
    // §8.2：LocalFolder/data/knownhosts.json
    public sealed class KnownHostRepository : Repository<KnownHost>
    {
        public const string DataFilePath = "data/knownhosts.json";

        public KnownHostRepository(IFileSystem fs, ILogger logger = null)
            : base(new JsonStore<KnownHost>(fs, DataFilePath, new KnownHostCodec(), StorageMigrations.All, logger))
        {
        }
    }
}
