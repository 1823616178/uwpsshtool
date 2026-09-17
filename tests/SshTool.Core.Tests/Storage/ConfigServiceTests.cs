using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SshTool.Core.Models;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;
using Xunit;

namespace SshTool.Core.Tests.Storage
{
    // D02 验收：ConfigService 引用规则。
    public class ConfigServiceTests
    {
        private sealed class RecordingSecretStore : ISecretStore
        {
            public readonly List<string> RemovedPrefixes = new List<string>();

            public Task<string> GetAsync(string key)
            {
                return Task.FromResult<string>(null);
            }

            public Task SetAsync(string key, string value)
            {
                return Task.CompletedTask;
            }

            public Task RemoveAsync(string key)
            {
                return Task.CompletedTask;
            }

            public Task RemoveByPrefixAsync(string prefix)
            {
                RemovedPrefixes.Add(prefix);
                return Task.CompletedTask;
            }
        }

        private sealed class Fixture
        {
            public readonly InMemoryFileSystem Fs = new InMemoryFileSystem();
            public readonly HostRepository Hosts;
            public readonly GroupRepository Groups;
            public readonly TunnelRepository Tunnels;
            public readonly KeyRepository Keys;
            public readonly RecordingSecretStore Secrets = new RecordingSecretStore();
            public readonly ConfigService Service;

            public Fixture()
            {
                Hosts = new HostRepository(Fs);
                Groups = new GroupRepository(Fs);
                Tunnels = new TunnelRepository(Fs);
                Keys = new KeyRepository(Fs);
                Service = new ConfigService(Hosts, Groups, Tunnels, Keys, Secrets);
            }
        }

        private static Host NewHost(string id)
        {
            var h = Defaults.NewHost();
            h.Id = id;
            h.Name = id;
            return h;
        }

        private static Tunnel NewTunnel(string id, string serverId, TunnelType type = TunnelType.Local)
        {
            var t = Defaults.NewTunnel(serverId);
            t.Id = id;
            t.Name = id;
            t.Type = type;
            return t;
        }

        [Fact]
        public async Task DeleteHost_CascadesTunnelsJumpHostAndSecrets()
        {
            var f = new Fixture();
            await f.Hosts.AddAsync(NewHost("h1"));
            var h2 = NewHost("h2");
            h2.JumpHostId = "h1";
            await f.Hosts.AddAsync(h2);
            await f.Tunnels.AddAsync(NewTunnel("t1", "h1"));
            await f.Tunnels.AddAsync(NewTunnel("t2", "h2"));
            var t3 = NewTunnel("t3", "h2", TunnelType.Relay);
            t3.DestServerId = "h1";
            await f.Tunnels.AddAsync(t3);

            await f.Service.DeleteHostAsync("h1");

            Assert.Null(await f.Hosts.GetByIdAsync("h1"));
            Assert.Null((await f.Hosts.GetByIdAsync("h2")).JumpHostId);
            Assert.Equal(new[] { "t2" }, (await f.Tunnels.GetAllAsync()).Select(t => t.Id).ToArray());
            Assert.Equal(new[] { "host:h1:" }, f.Secrets.RemovedPrefixes.ToArray());
        }

        [Fact]
        public async Task DeleteGroup_ClearsHostAndTunnelGroupId()
        {
            var f = new Fixture();
            await f.Groups.AddAsync(Defaults.NewGroup("g"));
            var group = (await f.Groups.GetAllAsync()).Single();
            var h = NewHost("h1");
            h.GroupId = group.Id;
            await f.Hosts.AddAsync(h);
            var t = NewTunnel("t1", "h1");
            t.GroupId = group.Id;
            await f.Tunnels.AddAsync(t);

            await f.Service.DeleteGroupAsync(group.Id);

            Assert.Empty(await f.Groups.GetAllAsync());
            Assert.Null((await f.Hosts.GetByIdAsync("h1")).GroupId);
            Assert.Null((await f.Tunnels.GetByIdAsync("t1")).GroupId);
        }

        [Fact]
        public async Task DeleteKey_ReferencedByHost_Refused()
        {
            var f = new Fixture();
            var key = new KeyEntry { Id = "k1", Name = "密钥" };
            await f.Keys.AddAsync(key);
            var h = NewHost("h1");
            h.KeyId = "k1";
            await f.Hosts.AddAsync(h);

            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Service.DeleteKeyAsync("k1"));

            Assert.NotNull(await f.Keys.GetByIdAsync("k1"));
            Assert.Empty(f.Secrets.RemovedPrefixes);
        }

        [Fact]
        public async Task DeleteKey_Unreferenced_RemovesAndCascadesSecrets()
        {
            var f = new Fixture();
            await f.Keys.AddAsync(new KeyEntry { Id = "k1", Name = "密钥" });

            await f.Service.DeleteKeyAsync("k1");

            Assert.Null(await f.Keys.GetByIdAsync("k1"));
            Assert.Equal(new[] { "key:k1:" }, f.Secrets.RemovedPrefixes.ToArray());
        }

        [Fact]
        public async Task DeleteHost_NonExistent_StillCascadesSecrets()
        {
            var f = new Fixture();

            await f.Service.DeleteHostAsync("幽灵");

            Assert.Equal(new[] { "host:幽灵:" }, f.Secrets.RemovedPrefixes.ToArray());
        }

        [Fact]
        public void SecretKeys_Formats()
        {
            Assert.Equal("host:h1:password", SecretKeys.HostPassword("h1"));
            Assert.Equal("host:h1:passphrase", SecretKeys.HostPassphrase("h1"));
            Assert.Equal("key:k1:private", SecretKeys.KeyPrivate("k1"));
            Assert.Equal("key:k1:passphrase", SecretKeys.KeyPassphrase("k1"));
            Assert.Equal("host:h1:", SecretKeys.HostPrefix("h1"));
            Assert.Equal("key:k1:", SecretKeys.KeyPrefix("k1"));
        }
    }
}
