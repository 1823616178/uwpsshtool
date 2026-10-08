using System.Threading.Tasks;
using SshTool.Core.Models;
using SshTool.Core.Sessions;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;
using Xunit;

namespace SshTool.Core.Tests.Sessions
{
    public class HostKeyResetTests
    {
        private static async Task<Host> AddHostAsync(HostRepository hosts, string id, string hostName, int port, string fp)
        {
            Host h = Defaults.NewHost();
            h.Id = id;
            h.Name = id;
            h.HostName = hostName;
            h.Port = port;
            h.HostFingerprint = fp;
            await hosts.AddAsync(h);
            return h;
        }

        [Fact]
        public async Task Reset_RemovesKnownHostsAndPinsForSameAddressOnly()
        {
            var fs = new InMemoryFileSystem();
            var known = new KnownHostRepository(fs);
            var hosts = new HostRepository(fs);
            await known.AddAsync(new KnownHost { Id = "a", Host = "Example.com", Port = 22, FingerprintSha256 = "SHA256:x" });
            await known.AddAsync(new KnownHost { Id = "b", Host = "example.com", Port = 2222, FingerprintSha256 = "SHA256:y" });
            await AddHostAsync(hosts, "h1", "example.com", 22, "SHA256:x");
            await AddHostAsync(hosts, "h2", "EXAMPLE.COM", 0, "SHA256:x");
            await AddHostAsync(hosts, "h3", "example.com", 2222, "SHA256:y");

            HostKeyReset.Result r = await HostKeyReset.ResetAsync(known, hosts, "example.com", 22, "h1");

            Assert.Equal(1, r.KnownHostsRemoved);
            Assert.Equal(2, r.PinsCleared);
            Assert.Null(await known.FindAsync("example.com", 22));
            Assert.NotNull(await known.FindAsync("example.com", 2222));
            Assert.Equal(string.Empty, (await hosts.GetByIdAsync("h1")).HostFingerprint);
            Assert.Equal(string.Empty, (await hosts.GetByIdAsync("h2")).HostFingerprint);
            Assert.Equal("SHA256:y", (await hosts.GetByIdAsync("h3")).HostFingerprint);
        }

        [Fact]
        public async Task ClearPins_ByIdEvenWhenAddressDiffers()
        {
            var fs = new InMemoryFileSystem();
            var hosts = new HostRepository(fs);
            await AddHostAsync(hosts, "h1", "10.0.0.1", 22, "SHA256:x");

            int n = await HostKeyReset.ClearPinsAsync(hosts, "other", 22, "h1");

            Assert.Equal(1, n);
            Assert.Equal(string.Empty, (await hosts.GetByIdAsync("h1")).HostFingerprint);
        }

        [Fact]
        public async Task ClearPins_NothingPinned_NoWrites()
        {
            var fs = new InMemoryFileSystem();
            var hosts = new HostRepository(fs);
            await AddHostAsync(hosts, "h1", "10.0.0.1", 22, "");
            Assert.Equal(0, await HostKeyReset.ClearPinsAsync(hosts, "10.0.0.1", 22, null));
        }

        [Theory]
        [InlineData("web", "10.0.0.1", 22, "web (10.0.0.1)")]
        [InlineData("", "10.0.0.1", 2222, "10.0.0.1:2222")]
        [InlineData("10.0.0.1", "10.0.0.1", 0, "10.0.0.1")]
        public void ConnectionTester_HostLabel_UsesNameAndAddress(string name, string hostName, int port, string expected)
        {
            Host h = Defaults.NewHost();
            h.Name = name;
            h.HostName = hostName;
            h.Port = port;
            Assert.Equal(expected, ConnectionTester.HostLabel(h));
        }
    }
}
