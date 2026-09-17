using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Storage.Codecs;

namespace SshTool.Core.Storage.Repositories
{
    // §8.2：LocalFolder/data/groups.json
    public sealed class GroupRepository : Repository<HostGroup>
    {
        public const string DataFilePath = "data/groups.json";

        public GroupRepository(IFileSystem fs, ILogger logger = null)
            : base(new JsonStore<HostGroup>(fs, DataFilePath, new HostGroupCodec(), StorageMigrations.All, logger))
        {
        }
    }
}
