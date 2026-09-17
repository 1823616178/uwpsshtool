using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SshTool.Core.Appearance;
using SshTool.Core.Models;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;
using Xunit;

namespace SshTool.Core.Tests.Appearance
{
    // A01 验收：删除回退、内置只读、变化事件带受影响主机集合。
    public class AppearanceServiceTests
    {
        private sealed class Fixture
        {
            public readonly InMemoryFileSystem Fs = new InMemoryFileSystem();
            public readonly HostRepository Hosts;
            public readonly AppearanceRepository Appearances;
            public readonly SettingsRepository Settings;
            public readonly AppearanceService Service;
            public readonly List<AppearanceChangedEventArgs> Events = new List<AppearanceChangedEventArgs>();

            public Fixture()
            {
                Hosts = new HostRepository(Fs);
                Appearances = new AppearanceRepository(Fs);
                Settings = new SettingsRepository(new InMemorySettingsStore());
                Service = new AppearanceService(Appearances, Hosts, Settings, AppearanceResolverTests.TwoBuiltIns);
                Service.Changed += (s, e) => Events.Add(e);
            }

            public Task AddHostAsync(string id, string appearanceId)
            {
                return Hosts.AddAsync(new Host
                {
                    Id = id,
                    Name = id,
                    HostName = "example.com",
                    Username = "u",
                    AppearanceId = appearanceId
                });
            }
        }

        private static AppearanceProfile UserProfile(string id)
        {
            return AppearanceResolverTests.Profile(id);
        }

        [Fact]
        public async Task ResolveForHost_UsesHostThenDefaultThenBuiltIn()
        {
            var f = new Fixture();
            await f.Appearances.AddAsync(UserProfile("user-a"));
            await f.AddHostAsync("h1", "user-a");
            await f.AddHostAsync("h2", null);

            // 默认 defaultAppearanceId = builtin-harmony-dark（D04 默认值），在内置列表里。
            Assert.Equal("user-a", (await f.Service.ResolveForHostAsync("h1")).Profile.Id);
            var r2 = await f.Service.ResolveForHostAsync("h2");
            Assert.Equal(AppearanceSource.GlobalDefault, r2.Source);
            Assert.Equal(AppearanceResolver.DefaultBuiltInId, r2.Profile.Id);

            // 全局默认被改成一个不存在的 id 时回退内置默认。
            f.Settings.DefaultAppearanceId = "missing";
            var r3 = await f.Service.ResolveForHostAsync("h2");
            Assert.Equal(AppearanceSource.BuiltInFallback, r3.Source);
            Assert.Equal(AppearanceResolver.DefaultBuiltInId, r3.Profile.Id);
        }

        [Fact]
        public async Task ListAsync_BuiltInsFirst_ThenUserProfiles()
        {
            var f = new Fixture();
            await f.Appearances.AddAsync(UserProfile("user-a"));
            var all = await f.Service.ListAsync();
            Assert.Equal(3, all.Count);
            Assert.Equal(AppearanceResolver.DefaultBuiltInId, all[0].Id);
            Assert.Equal("builtin-other", all[1].Id);
            Assert.Equal("user-a", all[2].Id);
        }

        [Fact]
        public async Task AddOrUpdate_BuiltIn_Throws()
        {
            var f = new Fixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                f.Service.AddAsync(NewBuiltIn("builtin-new")));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                f.Service.UpdateAsync(UserProfile(AppearanceResolver.DefaultBuiltInId)));
            // BuiltIn 标志位同样拒绝（防止伪造 id 绕过）
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                f.Service.AddAsync(NewBuiltIn("user-looking-id")));
        }

        private static AppearanceProfile NewBuiltIn(string id)
        {
            var p = UserProfile(id);
            p.BuiltIn = true;
            return p;
        }

        [Fact]
        public async Task Delete_BuiltIn_Throws()
        {
            var f = new Fixture();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                f.Service.DeleteAsync(AppearanceResolver.DefaultBuiltInId));
        }

        [Fact]
        public async Task Delete_ResetsReferencingHosts_AndReportsAffected()
        {
            var f = new Fixture();
            await f.Appearances.AddAsync(UserProfile("user-a"));
            await f.AddHostAsync("h1", "user-a");
            await f.AddHostAsync("h2", "user-a");
            await f.AddHostAsync("h3", null);

            var refs = await f.Service.GetReferencingHostsAsync("user-a");
            Assert.Equal(new[] { "h1", "h2" }, refs.Select(h => h.Id).OrderBy(x => x).ToArray());

            var affected = await f.Service.DeleteAsync("user-a");
            Assert.Equal(new[] { "h1", "h2" }, affected.OrderBy(x => x).ToArray());
            Assert.Null((await f.Hosts.GetByIdAsync("h1")).AppearanceId);
            Assert.Null((await f.Hosts.GetByIdAsync("h2")).AppearanceId);
            Assert.Null(await f.Appearances.GetByIdAsync("user-a"));

            Assert.Single(f.Events);
            Assert.Equal(AppearanceChangeKind.Deleted, f.Events[0].Kind);
            Assert.Equal("user-a", f.Events[0].AppearanceId);
            Assert.Equal(new[] { "h1", "h2" }, f.Events[0].AffectedHostIds.OrderBy(x => x).ToArray());

            // 删除后解析回退到全局默认
            var r = await f.Service.ResolveForHostAsync("h1");
            Assert.Equal(AppearanceSource.GlobalDefault, r.Source);
        }

        [Fact]
        public async Task Delete_Nonexistent_NoOpNoEvent()
        {
            var f = new Fixture();
            var affected = await f.Service.DeleteAsync("nope");
            Assert.Empty(affected);
            Assert.Empty(f.Events);
        }

        [Fact]
        public async Task Update_AffectsReferencing_AndDefaultFollowers()
        {
            var f = new Fixture();
            await f.Appearances.AddAsync(UserProfile("user-a"));
            await f.AddHostAsync("h1", "user-a");
            await f.AddHostAsync("h2", null);   // 跟随默认

            // user-a 不是默认：只有 h1 受影响
            await f.Service.UpdateAsync(UserProfile("user-a"));
            Assert.Equal(AppearanceChangeKind.Updated, f.Events[0].Kind);
            Assert.Equal(new[] { "h1" }, f.Events[0].AffectedHostIds.ToArray());

            // 把 user-a 设为默认后：h1（引用）+ h2（跟随默认）都受影响
            await f.Service.SetDefaultAsync("user-a");
            f.Events.Clear();
            await f.Service.UpdateAsync(UserProfile("user-a"));
            Assert.Equal(new[] { "h1", "h2" }, f.Events[0].AffectedHostIds.OrderBy(x => x).ToArray());
        }

        [Fact]
        public async Task SetDefault_UnknownId_Throws_AndKnownFiresEvent()
        {
            var f = new Fixture();
            await f.AddHostAsync("h1", "user-a");
            await f.AddHostAsync("h2", null);

            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.SetDefaultAsync("nope"));

            await f.Service.SetDefaultAsync("builtin-other");
            Assert.Equal("builtin-other", f.Settings.DefaultAppearanceId);
            Assert.Single(f.Events);
            Assert.Equal(AppearanceChangeKind.DefaultChanged, f.Events[0].Kind);
            Assert.Equal(new[] { "h2" }, f.Events[0].AffectedHostIds.ToArray());
        }

        [Fact]
        public async Task Add_FiresEvent_WithEmptyAffected()
        {
            var f = new Fixture();
            await f.Service.AddAsync(UserProfile("user-a"));
            Assert.Single(f.Events);
            Assert.Equal(AppearanceChangeKind.Added, f.Events[0].Kind);
            Assert.Empty(f.Events[0].AffectedHostIds);
            Assert.NotNull(await f.Appearances.GetByIdAsync("user-a"));
        }
    }
}
