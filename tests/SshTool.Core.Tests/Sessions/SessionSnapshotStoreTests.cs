using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.Core.Sessions;
using SshTool.Core.Storage;
using Xunit;

namespace SshTool.Core.Tests.Sessions
{
    public class SessionSnapshotStoreTests
    {
        [Fact]
        public async Task RoundTrip_KeepsHostIdsAndLayout()
        {
            var fs = new InMemoryFileSystem();
            var store = new SessionSnapshotStore(fs);
            await store.SaveAsync(new SessionSnapshot
            {
                HostIds = new List<string> { "a", "b" },
                PaneLayoutJson = "{\"split\":true}"
            });
            var existing = new HashSet<string> { "a", "b", "c" };
            SessionSnapshot loaded = await store.LoadAsync(existing);
            Assert.Equal(new[] { "a", "b" }, loaded.HostIds.ToArray());
            Assert.Equal("{\"split\":true}", loaded.PaneLayoutJson);
        }

        [Fact]
        public async Task Load_FiltersDeletedHosts()
        {
            var fs = new InMemoryFileSystem();
            var store = new SessionSnapshotStore(fs);
            await store.SaveAsync(new SessionSnapshot
            {
                HostIds = new List<string> { "keep", "gone" }
            });
            var existing = new HashSet<string> { "keep" };
            SessionSnapshot loaded = await store.LoadAsync(existing);
            Assert.Equal(new[] { "keep" }, loaded.HostIds.ToArray());
        }

        [Fact]
        public async Task Load_MissingFile_Empty()
        {
            var store = new SessionSnapshotStore(new InMemoryFileSystem());
            SessionSnapshot loaded = await store.LoadAsync(new HashSet<string>());
            Assert.Empty(loaded.HostIds);
        }
    }
}
