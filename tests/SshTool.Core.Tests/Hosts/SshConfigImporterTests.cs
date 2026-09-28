using System.Linq;
using SshTool.Core.Hosts;
using SshTool.Core.Models;
using Xunit;

namespace SshTool.Core.Tests.Hosts
{
    // W05：~/.ssh/config 导入。
    public class SshConfigImporterTests
    {
        private const string Sample = @"
# 全局选项不导入
ServerAliveInterval 30

Host *
    User nobody

Host bastion jump
    HostName bastion.example.com
    User ops
    Port 2222

Host web
    HostName=10.0.0.5
    USER deploy
    ProxyJump ops@bastion:2222
    IdentityFile ~/.ssh/id_ed25519   # 行尾注释
    User ignored-second-value

Host nouser
    HostName 10.0.0.9

Match host web
    User matchuser

Host ""quoted name""
    User q
";

        [Fact]
        public void Parse_ReadsBlocks_FirstValueWins_KeywordsCaseInsensitive()
        {
            var entries = SshConfigImporter.Parse(Sample);
            Assert.Equal(new[] { "*", "bastion", "web", "nouser", "quoted name" }, entries.Select(e => e.Alias).ToArray());
            SshConfigEntry web = entries[2];
            Assert.Equal("10.0.0.5", web.HostName);
            Assert.Equal("deploy", web.User);
            Assert.Equal("ops@bastion:2222", web.ProxyJump);
            Assert.Equal("~/.ssh/id_ed25519", web.IdentityFile);
            Assert.True(entries[0].IsPattern);
            Assert.Equal(2222, entries[1].Port);
        }

        [Fact]
        public void Parse_MatchBlock_DoesNotLeakIntoPreviousHost()
        {
            var entries = SshConfigImporter.Parse(Sample);
            Assert.Equal("deploy", entries.Single(e => e.Alias == "web").User);
        }

        [Fact]
        public void ToHosts_BuildsHostsAndReportsSkips()
        {
            var result = SshConfigImporter.ToHosts(SshConfigImporter.Parse(Sample), new[] { "Quoted Name" });
            Assert.Equal(new[] { "bastion", "web" }, result.Hosts.Select(h => h.Name).ToArray());
            Assert.Equal(new[] { "*" }, result.SkippedPatterns);
            Assert.Equal(new[] { "nouser" }, result.SkippedIncomplete);
            Assert.Equal(new[] { "quoted name" }, result.SkippedDuplicates); // 与现有主机同名（不区分大小写）
            Assert.Equal(new[] { "web" }, result.NeedsKey);

            Host bastion = result.Hosts[0];
            Host web = result.Hosts[1];
            Assert.Equal("bastion.example.com", bastion.HostName);
            Assert.Equal(2222, bastion.Port);
            Assert.Equal(22, web.Port);
            Assert.Equal(bastion.Id, web.JumpHostId);
            Assert.Equal(AuthType.Password, web.AuthType);
        }

        [Fact]
        public void ToHosts_HostNameDefaultsToAlias_ExternalJumpIgnored()
        {
            const string text = "Host box\n  User me\n  ProxyJump gw.outside.net\n";
            Host box = Assert.Single(SshConfigImporter.ToHosts(SshConfigImporter.Parse(text), null).Hosts);
            Assert.Equal("box", box.HostName);
            Assert.Null(box.JumpHostId);
        }

        [Fact]
        public void ToHosts_DuplicateAliasInSameFile_SecondSkipped()
        {
            const string text = "Host a\n User x\nHost a\n User y\n";
            var result = SshConfigImporter.ToHosts(SshConfigImporter.Parse(text), null);
            Assert.Equal("x", Assert.Single(result.Hosts).Username);
            Assert.Equal(new[] { "a" }, result.SkippedDuplicates);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("# only a comment\n")]
        public void Parse_EmptyInput_NoEntries(string text)
        {
            Assert.Empty(SshConfigImporter.Parse(text));
        }

        [Fact]
        public void Parse_InvalidPort_Ignored()
        {
            var entry = Assert.Single(SshConfigImporter.Parse("Host a\n Port 99999\n Port 2200\n"));
            Assert.Equal(2200, entry.Port);
        }
    }
}
