using Newtonsoft.Json.Linq;
using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Validation;
using Xunit;

namespace SshTool.Core.Tests.Models
{
    public class ModelCloneAndDefaultsTests
    {
        [Fact]
        public void Host_Clone_DeepCopiesCollectionsAndExtra()
        {
            var host = Defaults.NewHost();
            host.Name = "a";
            host.InitCommands.Add("tmux a");
            host.EnvVars["LANG"] = "zh_CN.UTF-8";
            host.Extra = new JObject { ["future"] = 1 };

            var clone = host.Clone();
            Assert.Equal(host.Name, clone.Name);
            Assert.Equal("tmux a", clone.InitCommands[0]);

            clone.InitCommands.Add("uptime");
            clone.EnvVars["LANG"] = "en_US.UTF-8";
            clone.Extra["future"] = 2;

            Assert.Single(host.InitCommands);
            Assert.Equal("zh_CN.UTF-8", host.EnvVars["LANG"]);
            Assert.Equal(1, (int)host.Extra["future"]);
        }

        [Fact]
        public void Appearance_Clone_DeepCopiesPalette()
        {
            var appearance = Defaults.DefaultAppearance();
            var clone = appearance.Clone();
            clone.Palette[0] = "#111111";
            Assert.Equal("#000000", appearance.Palette[0]);
            Assert.NotSame(appearance.Palette, clone.Palette);
        }

        [Fact]
        public void Tunnel_Clone_IndependentExtra()
        {
            var tunnel = Defaults.NewTunnel("srv-1");
            tunnel.Extra = new JObject { ["x"] = "y" };
            var clone = tunnel.Clone();
            clone.Extra["x"] = "z";
            Assert.Equal("y", (string)tunnel.Extra["x"]);
        }

        [Fact]
        public void IdGenerator_LowercaseGuid()
        {
            var id = IdGenerator.NewId();
            Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", id);
        }

        [Fact]
        public void Defaults_HostValues()
        {
            var h = Defaults.NewHost();
            Assert.Equal(22, h.Port);
            Assert.Equal(30, h.Keepalive);
            Assert.Equal("xterm-256color", h.TermType);
            Assert.False(h.TmuxAutoAttach);
            Assert.False(h.BackspaceSendsCtrlH);
        }

        [Fact]
        public void Defaults_AppearancePalette_16ValidColors()
        {
            var a = Defaults.DefaultAppearance();
            Assert.Equal(16, a.Palette.Count);
            foreach (var color in a.Palette)
            {
                Assert.True(GroupValidator.IsValidColor(color), color);
            }
            Assert.InRange(a.FontSize, 8, 28);
            Assert.InRange(a.LineHeight, 1.0, 1.6);
            Assert.InRange(a.Padding, 0, 16);
        }
    }
}
