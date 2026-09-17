using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Storage.Codecs;

namespace SshTool.Core.Storage.Repositories
{
    // §8.2：LocalFolder/data/snippets.json
    public sealed class SnippetRepository : Repository<Snippet>
    {
        public const string DataFilePath = "data/snippets.json";

        public SnippetRepository(IFileSystem fs, ILogger logger = null)
            : base(new JsonStore<Snippet>(fs, DataFilePath, new SnippetCodec(), StorageMigrations.All, logger))
        {
        }
    }
}
