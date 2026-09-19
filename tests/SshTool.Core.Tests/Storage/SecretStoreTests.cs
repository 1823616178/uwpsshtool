using System.Threading.Tasks;
using SshTool.Core.Storage;
using Xunit;

namespace SshTool.Core.Tests.Storage
{
    public class SecretStoreTests
    {
        [Fact]
        public void SecretKeys_FollowHostAndKeyConventions()
        {
            Assert.Equal("host:abc:password", SecretKeys.HostPassword("abc"));
            Assert.Equal("host:abc:passphrase", SecretKeys.HostPassphrase("abc"));
            Assert.Equal("key:k1:private", SecretKeys.KeyPrivate("k1"));
            Assert.Equal("key:k1:passphrase", SecretKeys.KeyPassphrase("k1"));
            Assert.Equal("host:abc:", SecretKeys.HostPrefix("abc"));
            Assert.Equal("key:k1:", SecretKeys.KeyPrefix("k1"));
        }

        [Fact]
        public async Task InMemory_GetSetRemove()
        {
            var store = new InMemorySecretStore();
            Assert.Null(await store.GetAsync(SecretKeys.HostPassword("h1")));
            await store.SetAsync(SecretKeys.HostPassword("h1"), "s3cret");
            Assert.Equal("s3cret", await store.GetAsync(SecretKeys.HostPassword("h1")));
            await store.RemoveAsync(SecretKeys.HostPassword("h1"));
            Assert.Null(await store.GetAsync(SecretKeys.HostPassword("h1")));
        }

        [Fact]
        public async Task InMemory_RemoveByPrefix_KeepsOtherHosts()
        {
            var store = new InMemorySecretStore();
            await store.SetAsync(SecretKeys.HostPassword("a"), "pa");
            await store.SetAsync(SecretKeys.HostPassphrase("a"), "pha");
            await store.SetAsync(SecretKeys.HostPassword("b"), "pb");
            await store.SetAsync(SecretKeys.KeyPrivate("k"), "priv");
            await store.RemoveByPrefixAsync(SecretKeys.HostPrefix("a"));
            Assert.Null(await store.GetAsync(SecretKeys.HostPassword("a")));
            Assert.Null(await store.GetAsync(SecretKeys.HostPassphrase("a")));
            Assert.Equal("pb", await store.GetAsync(SecretKeys.HostPassword("b")));
            Assert.Equal("priv", await store.GetAsync(SecretKeys.KeyPrivate("k")));
        }

        [Fact]
        public async Task InMemorySecureFile_RoundTripBytes()
        {
            var file = new InMemorySecureFile();
            await file.WriteAsync(new byte[] { 1, 2, 3 });
            Assert.Equal(new byte[] { 1, 2, 3 }, await file.ReadAsync());
            byte[] copy = await file.ReadAsync();
            copy[0] = 9;
            Assert.Equal(1, (await file.ReadAsync())[0]);
        }

        [Fact]
        public async Task SetNull_RemovesKey()
        {
            var store = new InMemorySecretStore();
            await store.SetAsync("k", "v");
            await store.SetAsync("k", null);
            Assert.Null(await store.GetAsync("k"));
        }
    }
}
