using System.Collections.Generic;
using System.Linq;
using SshTool.Core.Models;
using SshTool.Core.Sessions;
using Xunit;

namespace SshTool.Core.Tests.Sessions
{
    // 01-DESIGN.md §9.4：AutoRun 纯函数——tmux 附着在前、初始命令逐条在后。
    public class AutoRunTests
    {
        private static Host NewHost()
        {
            return new Host
            {
                InitCommands = new List<string>(),
                EnvVars = new Dictionary<string, string>()
            };
        }

        [Fact]
        public void Build_NullHostOrAllOff_Empty()
        {
            Assert.Empty(AutoRun.BuildCommands(null));
            Host host = NewHost();
            host.TmuxAutoAttach = false;
            Assert.Empty(AutoRun.BuildCommands(host));
        }

        [Fact]
        public void Build_TmuxFirst_ThenInitCommands_InOrder()
        {
            Host host = NewHost();
            host.TmuxAutoAttach = true;
            host.TmuxSessionName = "web";
            host.InitCommands = new List<string> { "cd /srv", "htop" };

            Assert.Equal(
                new[] { "tmux new-session -A -s web", "cd /srv", "htop" },
                AutoRun.BuildCommands(host).ToArray());
        }

        [Fact]
        public void Build_BlankInitCommands_Skipped()
        {
            Host host = NewHost();
            host.InitCommands = new List<string> { "ls", "", "   ", null, "pwd" };
            Assert.Equal(new[] { "ls", "pwd" }, AutoRun.BuildCommands(host).ToArray());
        }

        [Theory]
        [InlineData("web", "web")]
        [InlineData("a.b_c-d9", "a.b_c-d9")]
        [InlineData("my session; rm -rf /", "my_session__rm_-rf__")]
        [InlineData("中文 名", "____")]
        [InlineData("", "main")]
        [InlineData("   ", "main")]
        [InlineData(null, "main")]
        public void SanitizeTmuxName_ReplacesUnsafeChars(string input, string expected)
        {
            Assert.Equal(expected, AutoRun.SanitizeTmuxName(input));
        }

        [Fact]
        public void Build_TmuxNoName_UsesMain()
        {
            Host host = NewHost();
            host.TmuxAutoAttach = true;
            host.TmuxSessionName = "";
            Assert.Equal(new[] { "tmux new-session -A -s main" }, AutoRun.BuildCommands(host).ToArray());
        }
    }
}
