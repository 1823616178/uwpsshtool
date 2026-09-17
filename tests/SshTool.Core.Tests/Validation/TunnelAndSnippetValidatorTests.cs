using SshTool.Core.Models;
using SshTool.Core.Validation;
using Xunit;

namespace SshTool.Core.Tests.Validation
{
    public class TunnelValidatorTests
    {
        private static Tunnel ValidLocal()
        {
            var t = Defaults.NewTunnel("srv-1");
            t.Name = "web 转发";
            t.DestHost = "10.0.0.5";
            t.DestPort = 80;
            return t;
        }

        [Fact]
        public void Local_Valid_Passes()
        {
            Assert.True(TunnelValidator.Validate(ValidLocal()).IsValid);
        }

        [Fact]
        public void ServerId_Empty_Fails()
        {
            var t = ValidLocal();
            t.ServerId = "";
            Assert.Equal(ValidationKeys.ServerRequired, TunnelValidator.Validate(t).Errors["serverId"]);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(65536)]
        public void ListenPort_OutOfRange_Fails(int port)
        {
            var t = ValidLocal();
            t.ListenPort = port;
            Assert.Equal(ValidationKeys.PortRange, TunnelValidator.Validate(t).Errors["listenPort"]);
        }

        [Fact]
        public void Local_MissingDestHost_Fails()
        {
            var t = ValidLocal();
            t.DestHost = " ";
            Assert.Equal(ValidationKeys.DestHostRequired, TunnelValidator.Validate(t).Errors["destHost"]);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(65536)]
        public void Local_DestPort_OutOfRange_Fails(int port)
        {
            var t = ValidLocal();
            t.DestPort = port;
            Assert.Equal(ValidationKeys.PortRange, TunnelValidator.Validate(t).Errors["destPort"]);
        }

        [Fact]
        public void Dynamic_NoDest_Passes()
        {
            var t = ValidLocal();
            t.Type = TunnelType.Dynamic;
            t.DestHost = null;
            t.DestPort = 0;
            Assert.True(TunnelValidator.Validate(t).IsValid);
        }

        [Fact]
        public void Relay_MissingDestServer_Fails()
        {
            var t = ValidLocal();
            t.Type = TunnelType.Relay;
            t.DestServerId = null;
            Assert.Equal(ValidationKeys.DestServerRequired, TunnelValidator.Validate(t).Errors["destServerId"]);
        }

        [Fact]
        public void Relay_ExistingHost_Passes()
        {
            var t = ValidLocal();
            t.Type = TunnelType.Relay;
            t.DestServerId = "srv-2";
            Assert.True(TunnelValidator.Validate(t, id => id == "srv-2").IsValid);
        }

        [Fact]
        public void Relay_UnknownHost_Fails()
        {
            var t = ValidLocal();
            t.Type = TunnelType.Relay;
            t.DestServerId = "ghost";
            Assert.Equal(ValidationKeys.DestServerRequired,
                TunnelValidator.Validate(t, id => false).Errors["destServerId"]);
        }

        [Fact]
        public void Name_TooLong_Fails()
        {
            var t = ValidLocal();
            t.Name = new string('t', 256);
            Assert.Equal(ValidationKeys.NameTooLong, TunnelValidator.Validate(t).Errors["name"]);
        }
    }

    public class SnippetValidatorTests
    {
        [Fact]
        public void Valid_Passes()
        {
            var s = Defaults.NewSnippet();
            s.Name = "更新系统";
            s.Content = "sudo apt update";
            Assert.True(SnippetValidator.Validate(s).IsValid);
        }

        [Fact]
        public void Name_Empty_Fails()
        {
            var s = Defaults.NewSnippet();
            s.Name = "";
            s.Content = "ls";
            Assert.Equal(ValidationKeys.Required, SnippetValidator.Validate(s).Errors["name"]);
        }

        [Fact]
        public void Content_Empty_Fails()
        {
            var s = Defaults.NewSnippet();
            s.Name = "空片段";
            s.Content = "";
            Assert.Equal(ValidationKeys.SnippetContentRequired, SnippetValidator.Validate(s).Errors["content"]);
        }
    }
}
