using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SshTool.Core.Models;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Codecs;
using Xunit;

namespace SshTool.Core.Tests.Storage
{
    // D02 验收：JsonStore 原子写、损坏备份、迁移链、LoadWarnings。
    public class JsonStoreTests
    {
        private static readonly DateTime FixedNow = new DateTime(2026, 1, 2, 3, 4, 5);

        private static JsonStore<Snippet> NewStore(InMemoryFileSystem fs, string path = "data/snippets.json",
            IReadOnlyList<IMigration> migrations = null, int schemaVersion = 1)
        {
            return new JsonStore<Snippet>(fs, path, new SnippetCodec(), migrations, null, () => FixedNow, schemaVersion);
        }

        private static Snippet NewSnippet(string id, string name)
        {
            var s = Defaults.NewSnippet();
            s.Id = id;
            s.Name = name;
            return s;
        }

        [Fact]
        public async Task SaveThenLoad_RoundTrips()
        {
            var fs = new InMemoryFileSystem();
            var store = NewStore(fs);
            await store.SaveAsync(new List<Snippet> { NewSnippet("s1", "一"), NewSnippet("s2", "二") });

            var loaded = await NewStore(fs).LoadAsync();
            Assert.Equal(new[] { "s1", "s2" }, loaded.Select(s => s.Id).ToArray());
            Assert.Equal("一", loaded[0].Name);
            Assert.Empty(store.LoadWarnings);

            var doc = JsonText.ParseObject(fs.Files["data/snippets.json"]);
            Assert.Equal(1, (int)doc["schemaVersion"]);
            Assert.Equal(2, ((JArray)doc["items"]).Count);
        }

        [Fact]
        public async Task Save_AtomicWrite_TmpRemovedAfterMove()
        {
            var fs = new InMemoryFileSystem();
            var store = NewStore(fs);
            await store.SaveAsync(new List<Snippet> { NewSnippet("s1", "x") });
            Assert.True(fs.Files.ContainsKey("data/snippets.json"));
            Assert.False(fs.Files.ContainsKey("data/snippets.json.tmp"));
        }

        [Fact]
        public async Task Save_WriteFailure_OriginalUnchanged()
        {
            var fs = new InMemoryFileSystem();
            var store = NewStore(fs);
            await store.SaveAsync(new List<Snippet> { NewSnippet("s1", "旧") });
            string before = fs.Files["data/snippets.json"];

            fs.FailNextWrite(new IOException("磁盘写一半断电"));
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(new List<Snippet> { NewSnippet("s2", "新") }));

            Assert.Equal(before, fs.Files["data/snippets.json"]);
            Assert.False(fs.Files.ContainsKey("data/snippets.json.tmp"));
        }

        [Fact]
        public async Task Save_MoveFailure_OriginalUnchanged()
        {
            var fs = new InMemoryFileSystem();
            var store = NewStore(fs);
            await store.SaveAsync(new List<Snippet> { NewSnippet("s1", "旧") });
            string before = fs.Files["data/snippets.json"];

            fs.FailNextMove(new IOException("替换失败"));
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(new List<Snippet> { NewSnippet("s2", "新") }));

            Assert.Equal(before, fs.Files["data/snippets.json"]);
        }

        [Fact]
        public async Task Load_MissingFile_EmptyNoWarnings()
        {
            var store = NewStore(new InMemoryFileSystem());
            Assert.Empty(await store.LoadAsync());
            Assert.Empty(store.LoadWarnings);
        }

        [Fact]
        public async Task Load_CorruptJson_BackupAndEmpty()
        {
            var fs = new InMemoryFileSystem();
            await fs.WriteAllTextAsync("data/snippets.json", "{ 这不是 JSON");
            var store = NewStore(fs);

            var items = await store.LoadAsync();

            Assert.Empty(items);
            Assert.NotEmpty(store.LoadWarnings);
            string backup = "data/snippets.json.corrupt-20260102030405";
            Assert.True(fs.Files.ContainsKey(backup));
            Assert.Equal("{ 这不是 JSON", fs.Files[backup]);
            Assert.Equal("{ 这不是 JSON", fs.Files["data/snippets.json"]);
        }

        [Fact]
        public async Task Load_WrongItemType_Corrupt()
        {
            var fs = new InMemoryFileSystem();
            await fs.WriteAllTextAsync("data/snippets.json", "{\"schemaVersion\":1,\"items\":[123]}");
            var store = NewStore(fs);

            Assert.Empty(await store.LoadAsync());
            Assert.NotEmpty(store.LoadWarnings);
            Assert.True(fs.Files.ContainsKey("data/snippets.json.corrupt-20260102030405"));
        }

        [Fact]
        public async Task Load_LegacyNoVersion_NoOpMigrationToV1()
        {
            var fs = new InMemoryFileSystem();
            await fs.WriteAllTextAsync("data/snippets.json", "{\"items\":[{\"id\":\"s1\",\"name\":\"旧格式\"}]}");
            var store = NewStore(fs, migrations: new IMigration[] { new LegacyV0Migration() });

            var items = await store.LoadAsync();

            Assert.Single(items);
            Assert.Equal("旧格式", items[0].Name);
            Assert.Empty(store.LoadWarnings);
        }

        [Fact]
        public async Task Load_LegacyNoVersion_WithoutMigration_Corrupt()
        {
            var fs = new InMemoryFileSystem();
            await fs.WriteAllTextAsync("data/snippets.json", "{\"items\":[{\"id\":\"s1\"}]}");
            var store = NewStore(fs);

            Assert.Empty(await store.LoadAsync());
            Assert.Contains(store.LoadWarnings, w => w.Contains("缺少迁移段"));
        }

        private sealed class RenameMigration : IMigration
        {
            private readonly int _from;
            private readonly string _oldKey;
            private readonly string _newKey;

            public RenameMigration(int from, string oldKey, string newKey)
            {
                _from = from;
                _oldKey = oldKey;
                _newKey = newKey;
            }

            public int From
            {
                get { return _from; }
            }

            public int To
            {
                get { return _from + 1; }
            }

            public void Apply(JObject document)
            {
                foreach (var item in (JArray)document["items"])
                {
                    var o = (JObject)item;
                    var value = o[_oldKey];
                    if (value != null)
                    {
                        o[_newKey] = value;
                        o.Remove(_oldKey);
                    }
                }
            }
        }

        [Fact]
        public async Task Load_MigrationChain_AppliesInOrder()
        {
            var fs = new InMemoryFileSystem();
            await fs.WriteAllTextAsync("data/snippets.json",
                "{\"schemaVersion\":1,\"items\":[{\"id\":\"s1\",\"title\":\"改名前\",\"text\":\"旧内容键\"}]}");
            var migrations = new IMigration[]
            {
                new RenameMigration(1, "title", "name"),
                new RenameMigration(2, "text", "content")
            };
            var store = NewStore(fs, migrations: migrations, schemaVersion: 3);

            var items = await store.LoadAsync();

            Assert.Single(items);
            Assert.Equal("改名前", items[0].Name);
            Assert.Equal("旧内容键", items[0].Content);
            Assert.Empty(store.LoadWarnings);
        }

        [Fact]
        public async Task Load_MissingMigrationSegment_Corrupt()
        {
            var fs = new InMemoryFileSystem();
            await fs.WriteAllTextAsync("data/snippets.json",
                "{\"schemaVersion\":1,\"items\":[{\"id\":\"s1\",\"title\":\"x\"}]}");
            var store = NewStore(fs, migrations: new IMigration[] { new RenameMigration(2, "text", "content") }, schemaVersion: 3);

            Assert.Empty(await store.LoadAsync());
            Assert.Contains(store.LoadWarnings, w => w.Contains("缺少迁移段 1"));
            Assert.True(fs.Files.ContainsKey("data/snippets.json.corrupt-20260102030405"));
        }

        [Fact]
        public async Task Load_FutureVersion_DecodesWithWarning()
        {
            var fs = new InMemoryFileSystem();
            await fs.WriteAllTextAsync("data/snippets.json",
                "{\"schemaVersion\":99,\"items\":[{\"id\":\"s1\",\"name\":\"来自未来\",\"newField\":1}]}");
            var store = NewStore(fs);

            var items = await store.LoadAsync();

            Assert.Single(items);
            Assert.Equal("来自未来", items[0].Name);
            Assert.NotNull(items[0].Extra);
            Assert.Equal(1, (int)items[0].Extra["newField"]);
            Assert.Contains(store.LoadWarnings, w => w.Contains("99"));
        }
    
        // code-review-pass：读取失败（文件被占用、编码异常）时内容拿不到，没法写文本备份；
        // 此前直接以空集合继续，下一次保存就把完好的原文件覆盖成空。
        [Fact]
        public async Task Load_ReadFailure_MovesOriginalAsideBeforeAnySave()
        {
            var fs = new InMemoryFileSystem();
            await NewStore(fs).SaveAsync(new List<Snippet> { NewSnippet("s1", "原有"), NewSnippet("s2", "数据") });
            string original = fs.Files["data/snippets.json"];
            var store = NewStore(fs);

            fs.FailNextRead(new IOException("文件被占用"));
            var items = await store.LoadAsync();

            Assert.Empty(items);
            Assert.True(store.CanSave);
            string backup = "data/snippets.json.corrupt-20260102030405";
            Assert.Equal(original, fs.Files[backup]);
            Assert.Contains(store.LoadWarnings, w => w.Contains(backup));

            await store.SaveAsync(new List<Snippet> { NewSnippet("s3", "新") });
            Assert.Equal(original, fs.Files[backup]);
        }

        [Fact]
        public async Task Load_ReadFailure_AndBackupFails_RefusesToOverwrite()
        {
            var fs = new InMemoryFileSystem();
            await NewStore(fs).SaveAsync(new List<Snippet> { NewSnippet("s1", "原有") });
            string original = fs.Files["data/snippets.json"];
            var store = NewStore(fs);

            fs.FailNextRead(new IOException("文件被占用"));
            fs.FailNextMove(new IOException("仍被占用"));
            Assert.Empty(await store.LoadAsync());

            Assert.False(store.CanSave);
            await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(new List<Snippet> { NewSnippet("s2", "新") }));
            Assert.Equal(original, fs.Files["data/snippets.json"]);

            // 下次加载成功即解除封锁。
            Assert.Single(await store.LoadAsync());
            Assert.True(store.CanSave);
        }
}
}
