using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SshTool.Core.Models;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Codecs;
using Xunit;

namespace SshTool.Core.Tests.Storage
{
    // 01-DESIGN.md §8.4：仓库内存缓存、串行化写入、Changed 事件（ChangeOrigin + 变更 id 列表）。
    public class RepositoryTests
    {
        private sealed class HostRepo : Repository<Host>
        {
            public HostRepo(InMemoryFileSystem fs)
                : base(new JsonStore<Host>(fs, "data/hosts.json", new HostCodec()))
            {
            }
        }

        private static Host NewHost(string id)
        {
            var h = Defaults.NewHost();
            h.Id = id;
            h.Name = "主机 " + id;
            return h;
        }

        private static List<RepositoryChangedEventArgs> Watch(Repository<Host> repo)
        {
            var events = new List<RepositoryChangedEventArgs>();
            repo.Changed += (s, e) => events.Add(e);
            return events;
        }

        [Fact]
        public async Task Add_PersistsAndRaisesChanged()
        {
            var fs = new InMemoryFileSystem();
            var repo = new HostRepo(fs);
            var events = Watch(repo);

            await repo.AddAsync(NewHost("h1"));

            Assert.Single(events);
            Assert.Equal(ChangeOrigin.User, events[0].Origin);
            Assert.Equal(new[] { "h1" }, events[0].ChangedIds.ToArray());
            Assert.True(fs.Files.ContainsKey("data/hosts.json"));

            var reloaded = new HostRepo(fs);
            Assert.Equal("h1", (await reloaded.GetByIdAsync("h1")).Id);
        }

        [Fact]
        public async Task Add_DuplicateId_Throws()
        {
            var repo = new HostRepo(new InMemoryFileSystem());
            await repo.AddAsync(NewHost("h1"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => repo.AddAsync(NewHost("h1")));
        }

        [Fact]
        public async Task Add_EmptyId_Throws()
        {
            var repo = new HostRepo(new InMemoryFileSystem());
            var h = NewHost(null);
            await Assert.ThrowsAsync<InvalidOperationException>(() => repo.AddAsync(h));
        }

        [Fact]
        public async Task GetById_NotFound_ReturnsNull()
        {
            var repo = new HostRepo(new InMemoryFileSystem());
            Assert.Null(await repo.GetByIdAsync("不存在"));
        }

        [Fact]
        public async Task Update_LiveReferenceMutation_PersistsAfterSave()
        {
            var fs = new InMemoryFileSystem();
            var repo = new HostRepo(fs);
            var events = Watch(repo);
            await repo.AddAsync(NewHost("h1"));

            var host = await repo.GetByIdAsync("h1");
            host.Name = "改名后";
            Assert.True(await repo.UpdateAsync(host));

            Assert.Equal("h1", events[1].ChangedIds.Single());
            var reloaded = new HostRepo(fs);
            Assert.Equal("改名后", (await reloaded.GetByIdAsync("h1")).Name);
        }

        [Fact]
        public async Task UpdateMany_MultipleEntities_OneEvent()
        {
            var fs = new InMemoryFileSystem();
            var repo = new HostRepo(fs);
            await repo.AddAsync(NewHost("h1"));
            await repo.AddAsync(NewHost("h2"));
            var events = Watch(repo);

            var all = await repo.GetAllAsync();
            all[0].Name = "甲";
            all[1].Name = "乙";
            Assert.True(await repo.UpdateManyAsync(all.ToList()));

            Assert.Single(events);
            Assert.Equal(2, events[0].ChangedIds.Count);
            var reloaded = new HostRepo(fs);
            Assert.Equal(new[] { "甲", "乙" }, (await reloaded.GetAllAsync()).Select(h => h.Name).ToArray());
        }

        [Fact]
        public async Task UpdateMany_NoMatch_NoSaveNoEvent()
        {
            var fs = new InMemoryFileSystem();
            var repo = new HostRepo(fs);
            await repo.AddAsync(NewHost("h1"));
            string before = fs.Files["data/hosts.json"];
            var events = Watch(repo);

            Assert.False(await repo.UpdateManyAsync(new List<Host> { NewHost("幽灵") }));

            Assert.Empty(events);
            Assert.Equal(before, fs.Files["data/hosts.json"]);
        }

        [Fact]
        public async Task Remove_Works_AndNotFoundReturnsFalse()
        {
            var fs = new InMemoryFileSystem();
            var repo = new HostRepo(fs);
            await repo.AddAsync(NewHost("h1"));
            await repo.AddAsync(NewHost("h2"));
            var events = Watch(repo);

            Assert.True(await repo.RemoveAsync("h1"));
            Assert.False(await repo.RemoveAsync("h1"));

            Assert.Single(events);
            Assert.Equal(new[] { "h1" }, events[0].ChangedIds.ToArray());
            Assert.Equal("h2", (await repo.GetAllAsync()).Single().Id);
        }

        [Fact]
        public async Task RemoveMany_EmptyList_NoOp()
        {
            var repo = new HostRepo(new InMemoryFileSystem());
            await repo.AddAsync(NewHost("h1"));
            var events = Watch(repo);

            Assert.False(await repo.RemoveManyAsync(new List<string>()));
            Assert.False(await repo.RemoveManyAsync(new List<string> { "不存在" }));
            Assert.Empty(events);
        }

        [Fact]
        public async Task ReplaceAll_Sync_RaisesSyncOriginWithAllIds()
        {
            var repo = new HostRepo(new InMemoryFileSystem());
            await repo.AddAsync(NewHost("old"));
            var events = Watch(repo);

            await repo.ReplaceAllAsync(new List<Host> { NewHost("a"), NewHost("b") }, ChangeOrigin.Sync);

            Assert.Single(events);
            Assert.Equal(ChangeOrigin.Sync, events[0].Origin);
            Assert.Equal(new[] { "a", "b" }, events[0].ChangedIds.ToArray());
            Assert.Equal(new[] { "a", "b" }, (await repo.GetAllAsync()).Select(h => h.Id).ToArray());
        }

        [Fact]
        public async Task ConcurrentAdds_AreSerialized()
        {
            var fs = new InMemoryFileSystem();
            var repo = new HostRepo(fs);
            var tasks = Enumerable.Range(0, 10)
                .Select(i => repo.AddAsync(NewHost("h" + i)))
                .ToArray();

            await Task.WhenAll(tasks);

            Assert.Equal(10, (await repo.GetAllAsync()).Count);
            var reloaded = new HostRepo(fs);
            Assert.Equal(10, (await reloaded.GetAllAsync()).Count);
        }

        [Fact]
        public async Task LoadWarnings_SurfacedFromStore()
        {
            var fs = new InMemoryFileSystem();
            await fs.WriteAllTextAsync("data/hosts.json", "垃圾内容");
            var repo = new HostRepo(fs);

            Assert.Empty(await repo.GetAllAsync());
            Assert.NotEmpty(repo.LoadWarnings);
        }
    }
}
