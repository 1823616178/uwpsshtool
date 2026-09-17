using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Storage.Codecs;

namespace SshTool.Core.Storage.Repositories
{
    // §8.2：LocalFolder/data/hosts.json
    public sealed class HostRepository : Repository<Host>
    {
        public const string DataFilePath = "data/hosts.json";

        public HostRepository(IFileSystem fs, ILogger logger = null)
            : base(new JsonStore<Host>(fs, DataFilePath, new HostCodec(), StorageMigrations.All, logger))
        {
        }
    }
}
