using SshTool.Core.Hosts;
using Xunit;

namespace SshTool.Core.Tests.Hosts
{
    public class QuickConnectParserTests
    {
        [Fact]
        public void UserAtHost_DefaultsPort22()
        {
            QuickConnectTarget target;
            Assert.True(QuickConnectParser.TryParse("kim@web-01", out target));
            Assert.Equal("kim", target.Username);
            Assert.Equal("web-01", target.HostName);
            Assert.Equal(22, target.Port);
        }

        [Fact]
        public void UserAtHostPort()
        {
            QuickConnectTarget target;
            Assert.True(QuickConnectParser.TryParse(" root@10.0.0.11:2222 ", out target));
            Assert.Equal("root", target.Username);
            Assert.Equal("10.0.0.11", target.HostName);
            Assert.Equal(2222, target.Port);
        }

        [Fact]
        public void BracketIpv6_DefaultPort()
        {
            QuickConnectTarget target;
            Assert.True(QuickConnectParser.TryParse("user@[::1]", out target));
            Assert.Equal("user", target.Username);
            Assert.Equal("::1", target.HostName);
            Assert.Equal(22, target.Port);
        }

        [Fact]
        public void BracketIpv6_WithPort()
        {
            QuickConnectTarget target;
            Assert.True(QuickConnectParser.TryParse("user@[::1]:22", out target));
            Assert.Equal("::1", target.HostName);
            Assert.Equal(22, target.Port);
        }

        [Fact]
        public void UnbracketedIpv6_NoPort()
        {
            QuickConnectTarget target;
            Assert.True(QuickConnectParser.TryParse("user@2001:db8::1", out target));
            Assert.Equal("2001:db8::1", target.HostName);
            Assert.Equal(22, target.Port);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("web-01")]
        [InlineData("@web-01")]
        [InlineData("user@")]
        [InlineData("user@host:0")]
        [InlineData("user@host:65536")]
        [InlineData("user@host:abc")]
        [InlineData("user@[]")]
        [InlineData("user@[::1")]
        [InlineData("user@[::1]:abc")]
        [InlineData("user@host:22x")]
        public void Invalid_ReturnsFalse(string input)
        {
            QuickConnectTarget target;
            Assert.False(QuickConnectParser.TryParse(input, out target));
            Assert.Null(target);
        }
    }
}
