using SshTool.Core.Hosts;
using SshTool.Core.Models;
using SshTool.Core.Validation;
using Xunit;

namespace SshTool.Core.Tests.Hosts
{
    public class HostEditStateTests
    {
        [Fact]
        public void New_IsNotDirty()
        {
            Assert.False(HostEditState.ForNew().IsDirty);
        }

        [Fact]
        public void Edit_ChangeName_IsDirty()
        {
            Host host = Defaults.NewHost();
            host.Name = "web";
            host.HostName = "10.0.0.1";
            host.Username = "root";
            HostEditState state = HostEditState.ForEdit(host);
            Assert.False(state.IsDirty);
            state.Name = "web-2";
            Assert.True(state.IsDirty);
        }

        [Fact]
        public void CaptureBaseline_ClearsDirty()
        {
            HostEditState state = HostEditState.ForNew();
            state.Name = "x";
            Assert.True(state.IsDirty);
            state.CaptureBaseline();
            Assert.False(state.IsDirty);
        }

        [Fact]
        public void Duplicate_RenamesAndNewId()
        {
            Host host = Defaults.NewHost();
            host.Name = "web";
            host.HostName = "10.0.0.1";
            host.Username = "root";
            HostEditState state = HostEditState.ForDuplicate(host);
            Assert.Equal("web 副本", state.Name);
            Assert.NotEqual(host.Id, state.HostId);
            Assert.False(state.IsDirty);
        }

        [Fact]
        public void ToHost_RoundTripEnvAndCommands()
        {
            HostEditState state = HostEditState.ForNew();
            state.Name = "web";
            state.HostName = "10.0.0.1";
            state.Username = "root";
            state.PortText = "2222";
            state.InitCommandsText = "echo a\n\necho b";
            state.EnvVars.Add(new EnvVarRow("FOO", "1"));
            state.EnvVars.Add(new EnvVarRow("BAR", "2"));
            Host host = state.ToHost();
            Assert.Equal(2222, host.Port);
            Assert.Equal(new[] { "echo a", "echo b" }, host.InitCommands.ToArray());
            Assert.Equal("1", host.EnvVars["FOO"]);
            Assert.Equal("2", host.EnvVars["BAR"]);
        }

        [Fact]
        public void Validate_RequiredFields()
        {
            HostEditState state = HostEditState.ForNew();
            ValidationResult result = state.Validate(new Host[0]);
            Assert.False(result.IsValid);
            Assert.Equal(ValidationKeys.Required, result.Errors["name"]);
            Assert.Equal(ValidationKeys.Required, result.Errors["host"]);
            Assert.Equal(ValidationKeys.Required, result.Errors["username"]);
        }

        [Fact]
        public void Validate_BadPortAndKeepalive()
        {
            HostEditState state = HostEditState.ForNew();
            state.Name = "n";
            state.HostName = "h";
            state.Username = "u";
            state.PortText = "abc";
            state.KeepaliveText = "99999";
            ValidationResult result = state.Validate(new Host[0]);
            Assert.Equal(ValidationKeys.PortRange, result.Errors["port"]);
            Assert.Equal(ValidationKeys.KeepaliveRange, result.Errors["keepalive"]);
        }

        [Fact]
        public void Validate_BadEnvKey()
        {
            HostEditState state = HostEditState.ForNew();
            state.Name = "n";
            state.HostName = "h";
            state.Username = "u";
            state.EnvVars.Add(new EnvVarRow("1BAD", "x"));
            Assert.Equal(ValidationKeys.EnvVarsFormat, state.Validate(new Host[0]).Errors["envVars"]);
        }

        [Fact]
        public void Validate_JumpSelfLoop()
        {
            HostEditState state = HostEditState.ForNew();
            state.Name = "n";
            state.HostName = "h";
            state.Username = "u";
            state.JumpHostId = state.HostId;
            Assert.Equal(ValidationKeys.JumpSelfLoop, state.Validate(new Host[0]).Errors["jumpHostId"]);
        }

        [Fact]
        public void PivotIndexForField_MapsTabs()
        {
            Assert.Equal(0, HostEditState.PivotIndexForField("name"));
            Assert.Equal(1, HostEditState.PivotIndexForField("authType"));
            Assert.Equal(2, HostEditState.PivotIndexForField("envVars"));
            Assert.Equal(3, HostEditState.PivotIndexForField("jumpHostId"));
        }
    }
}
