using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Storage.Codecs;

namespace SshTool.Core.Storage.Repositories
{
    // §8.2：LocalFolder/data/tunnels.json
    public sealed class TunnelRepository : Repository<Tunnel>
    {
        public const string DataFilePath = "data/tunnels.json";

        public TunnelRepository(IFileSystem fs, ILogger logger = null)
            : base(new JsonStore<Tunnel>(fs, DataFilePath, new TunnelCodec(), StorageMigrations.All, logger))
        {
        }
    }
}
