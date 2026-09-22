using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SshTool.Core.Models;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;
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
        public async Task AddMany_PersistsOnceAndRaisesSingleChanged()
        {
            var fs = new InMemoryFileSystem();
            var repo = new HostRepo(fs);
            var events = Watch(repo);

            await repo.AddManyAsync(new[] { NewHost("h1"), NewHost("h2") });

            Assert.Single(events);
            Assert.Equal(2, events[0].ChangedIds.Count);
            Assert.Equal(2, (await repo.GetAllAsync()).Count);
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

        // ---------- O08：id 索引与列表不得脱节 ----------

        // 每种「整体替换 _items」的写操作之后，按 id 查都必须仍然正确。
        [Fact]
        public async Task GetById_StaysCorrect_AcrossEveryMutation()
        {
            var fs = new InMemoryFileSystem();
            var repo = new HostRepo(fs);

            await repo.AddAsync(NewHost("a"));
            await repo.AddManyAsync(new List<Host> { NewHost("b"), NewHost("c") });
            Assert.Equal("主机 a", (await repo.GetByIdAsync("a")).Name);
            Assert.Equal("主机 c", (await repo.GetByIdAsync("c")).Name);

            Host updated = NewHost("b");
            updated.Name = "改名后的 b";
            await repo.UpdateAsync(updated);
            Assert.Equal("改名后的 b", (await repo.GetByIdAsync("b")).Name);

            // 删中间一个：后面元素下标整体前移，索引必须跟着重建
            await repo.RemoveAsync("a");
            Assert.Null(await repo.GetByIdAsync("a"));
            Assert.Equal("改名后的 b", (await repo.GetByIdAsync("b")).Name);
            Assert.Equal("主机 c", (await repo.GetByIdAsync("c")).Name);

            await repo.ReplaceAllAsync(new List<Host> { NewHost("x"), NewHost("y") }, ChangeOrigin.Sync);
            Assert.Null(await repo.GetByIdAsync("b"));
            Assert.Null(await repo.GetByIdAsync("c"));
            Assert.Equal("主机 x", (await repo.GetByIdAsync("x")).Name);
            Assert.Equal("主机 y", (await repo.GetByIdAsync("y")).Name);
        }

        // 冷加载（索引在 EnsureLoadedCore 里建）后按 id 查。
        [Fact]
        public async Task GetById_WorksAfterColdLoad()
        {
            var fs = new InMemoryFileSystem();
            var seed = new HostRepo(fs);
            await seed.AddManyAsync(new List<Host> { NewHost("a"), NewHost("b") });

            var reopened = new HostRepo(fs);
            Assert.Equal("主机 b", (await reopened.GetByIdAsync("b")).Name);
            Assert.Null(await reopened.GetByIdAsync("zzz"));
        }

        [Fact]
        public async Task GetById_NullOrEmpty_ReturnsDefault()
        {
            var repo = new HostRepo(new InMemoryFileSystem());
            await repo.AddAsync(NewHost("a"));

            Assert.Null(await repo.GetByIdAsync(null));
            Assert.Null(await repo.GetByIdAsync(string.Empty));
        }

        // 重复 id 仍按「第一个命中」返回（与索引化之前的线性扫一致）。
        [Fact]
        public async Task GetById_DuplicateIds_ReturnsFirst()
        {
            var fs = new InMemoryFileSystem();
            Host first = NewHost("dup");
            first.Name = "第一个";
            Host second = NewHost("dup");
            second.Name = "第二个";
            var repo = new HostRepo(fs);
            // ReplaceAllAsync 不做重复校验（同步下行整份替换），可构造出重复 id
            await repo.ReplaceAllAsync(new List<Host> { first, second }, ChangeOrigin.Sync);

            Assert.Equal("第一个", (await repo.GetByIdAsync("dup")).Name);
        }
    }

    // O08：known_hosts 的 (host, port) 索引。
    public class KnownHostRepositoryLookupTests
    {
        private static KnownHost NewEntry(string host, int port, string fingerprint)
        {
            return new KnownHost
            {
                Id = host + ":" + port,
                Host = host,
                Port = port,
                KeyType = "ssh-ed25519",
                FingerprintSha256 = fingerprint,
                AddedAt = "2026-01-01T00:00:00.000Z"
            };
        }

        [Fact]
        public async Task FindAsync_MatchesHostCaseInsensitively_AndPortExactly()
        {
            var repo = new KnownHostRepository(new InMemoryFileSystem());
            await repo.AddManyAsync(new List<KnownHost>
            {
                NewEntry("example.com", 22, "fp-22"),
                NewEntry("example.com", 2222, "fp-2222")
            });

            Assert.Equal("fp-22", (await repo.FindAsync("example.com", 22)).FingerprintSha256);
            Assert.Equal("fp-22", (await repo.FindAsync("EXAMPLE.COM", 22)).FingerprintSha256);
            Assert.Equal("fp-2222", (await repo.FindAsync("example.com", 2222)).FingerprintSha256);
            Assert.Null(await repo.FindAsync("example.com", 22000));
            Assert.Null(await repo.FindAsync("other.com", 22));
            Assert.Null(await repo.FindAsync(null, 22));
        }

        // 索引是懒建 + Changed 失效：写入之后必须看得到新条目、看不到已删的。
        [Fact]
        public async Task FindAsync_ReflectsWritesAfterIndexBuilt()
        {
            var repo = new KnownHostRepository(new InMemoryFileSystem());
            await repo.AddAsync(NewEntry("a.com", 22, "fp-a"));
            Assert.Equal("fp-a", (await repo.FindAsync("a.com", 22)).FingerprintSha256); // 建索引

            await repo.AddAsync(NewEntry("b.com", 22, "fp-b"));
            Assert.Equal("fp-b", (await repo.FindAsync("b.com", 22)).FingerprintSha256);

            await repo.RemoveAsync("a.com:22");
            Assert.Null(await repo.FindAsync("a.com", 22));
        }

        // 端口拼键不能产生歧义："2/2.com" 与 "22/.com" 之类不得互相命中。
        [Fact]
        public async Task FindAsync_PortAndHostDoNotBleedIntoEachOther()
        {
            var repo = new KnownHostRepository(new InMemoryFileSystem());
            await repo.AddManyAsync(new List<KnownHost>
            {
                NewEntry("2.com", 2, "fp-x"),
                NewEntry(".com", 22, "fp-y")
            });

            Assert.Equal("fp-x", (await repo.FindAsync("2.com", 2)).FingerprintSha256);
            Assert.Equal("fp-y", (await repo.FindAsync(".com", 22)).FingerprintSha256);
        }
    }
}
