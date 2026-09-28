using SshTool.Core.Hosts;
using Xunit;

namespace SshTool.Core.Tests.Hosts
{
    // W03：磁贴参数与 ssh:// 链接解析。
    public class HostLaunchLinksTests
    {
        [Fact]
        public void TileArguments_RoundTrip()
        {
            string id;
            Assert.True(HostLaunchLinks.TryParseTileArguments(HostLaunchLinks.TileArguments("abc-123"), out id));
            Assert.Equal("abc-123", id);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("host:")]
        [InlineData("other:abc")]
        public void TileArguments_RejectsForeign(string args)
        {
            string id;
            Assert.False(HostLaunchLinks.TryParseTileArguments(args, out id));
        }

        [Fact]
        public void TileId_SanitizedAndBounded()
        {
            Assert.Equal("host_0f8f_ab", HostLaunchLinks.TileId("0f8f-ab"));
            Assert.True(HostLaunchLinks.TileId(new string('x', 200)).Length < 64);
            Assert.Equal("host_a_b", HostLaunchLinks.TileId("a/b"));
        }

        [Theory]
        [InlineData("ssh://root@10.0.0.5", "root@10.0.0.5")]
        [InlineData("ssh://deploy@prod.example.com:2222/", "deploy@prod.example.com:2222")]
        [InlineData("SSH://pi@nas.local:22?x=1", "pi@nas.local:22")]
        [InlineData("ssh://example.com", "example.com")]
        [InlineData("ssh://kim;fingerprint=SHA256-abc@h:2200", "kim@h:2200")]
        [InlineData("ssh://root@[fe80::1]:2022", "root@[fe80::1]:2022")]
        public void SshUri_ProducesQuickConnectText(string uri, string expected)
        {
            string text;
            Assert.True(HostLaunchLinks.TryParseSshUri(uri, out text));
            Assert.Equal(expected, text);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("http://example.com")]
        [InlineData("ssh://")]
        [InlineData("ssh://root@")]
        [InlineData("ssh://root@host:0")]
        [InlineData("ssh://root@host:99999")]
        [InlineData("ssh://root@host:abc")]
        [InlineData("ssh://root@[::1")]
        [InlineData("ssh://u%40x@h")] // 解码后用户名含 @：快速连接无法无歧义表示
        public void SshUri_RejectsInvalid(string uri)
        {
            string text;
            Assert.False(HostLaunchLinks.TryParseSshUri(uri, out text));
        }
    }
}
