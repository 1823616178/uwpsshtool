using SshTool.Core.Models;
using SshTool.Core.Validation;
using Xunit;

namespace SshTool.Core.Tests.Validation
{
    public class HostValidatorTests
    {
        private static Host ValidHost()
        {
            var h = Defaults.NewHost();
            h.Name = "web-1";
            h.HostName = "192.168.1.10";
            h.Username = "kim";
            return h;
        }

        [Fact]
        public void Valid_Passes()
        {
            Assert.True(HostValidator.Validate(ValidHost()).IsValid);
        }

        [Fact]
        public void Name_Empty_Fails()
        {
            var h = ValidHost();
            h.Name = "  ";
            Assert.Equal(ValidationKeys.Required, HostValidator.Validate(h).Errors["name"]);
        }

        [Fact]
        public void Name_TooLong_Fails()
        {
            var h = ValidHost();
            h.Name = new string('n', 256);
            Assert.Equal(ValidationKeys.NameTooLong, HostValidator.Validate(h).Errors["name"]);
        }

        [Fact]
        public void Host_Empty_Fails()
        {
            var h = ValidHost();
            h.HostName = "";
            Assert.Equal(ValidationKeys.Required, HostValidator.Validate(h).Errors["host"]);
        }

        [Fact]
        public void Host_TooLong_Fails()
        {
            var h = ValidHost();
            h.HostName = new string('h', 1025);
            Assert.Equal(ValidationKeys.HostTooLong, HostValidator.Validate(h).Errors["host"]);
        }

        [Fact]
        public void Username_Empty_Fails()
        {
            var h = ValidHost();
            h.Username = null;
            Assert.Equal(ValidationKeys.Required, HostValidator.Validate(h).Errors["username"]);
        }

        [Fact]
        public void Username_TooLong_Fails()
        {
            var h = ValidHost();
            h.Username = new string('u', 256);
            Assert.Equal(ValidationKeys.NameTooLong, HostValidator.Validate(h).Errors["username"]);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(65536)]
        [InlineData(-1)]
        public void Port_OutOfRange_Fails(int port)
        {
            var h = ValidHost();
            h.Port = port;
            Assert.Equal(ValidationKeys.PortRange, HostValidator.Validate(h).Errors["port"]);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(65535)]
        public void Port_Boundary_Passes(int port)
        {
            var h = ValidHost();
            h.Port = port;
            Assert.True(HostValidator.Validate(h).IsValid);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(3601)]
        public void Keepalive_OutOfRange_Fails(int keepalive)
        {
            var h = ValidHost();
            h.Keepalive = keepalive;
            Assert.Equal(ValidationKeys.KeepaliveRange, HostValidator.Validate(h).Errors["keepalive"]);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(3600)]
        public void Keepalive_Boundary_Passes(int keepalive)
        {
            var h = ValidHost();
            h.Keepalive = keepalive;
            Assert.True(HostValidator.Validate(h).IsValid);
        }
    }
}
