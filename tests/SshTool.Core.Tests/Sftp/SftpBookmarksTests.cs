using System.Linq;
using SshTool.Core.Sftp;
using Xunit;

namespace SshTool.Core.Tests.Sftp
{
    // W05：SFTP 路径书签。
    public class SftpBookmarksTests
    {
        [Fact]
        public void Add_NewestFirst_Deduplicated_PerHost()
        {
            string json = "{}";
            json = SftpBookmarks.Add(json, "h1", "/var/log");
            json = SftpBookmarks.Add(json, "h1", "/etc");
            json = SftpBookmarks.Add(json, "h1", "/var/log");
            json = SftpBookmarks.Add(json, "h2", "/srv");
            Assert.Equal(new[] { "/var/log", "/etc" }, SftpBookmarks.Get(json, "h1").ToArray());
            Assert.Equal(new[] { "/srv" }, SftpBookmarks.Get(json, "h2").ToArray());
            Assert.True(SftpBookmarks.Contains(json, "h1", "/etc"));
            Assert.False(SftpBookmarks.Contains(json, "h2", "/etc"));
        }

        [Fact]
        public void Add_CapsAtMaxPerHost_DroppingOldest()
        {
            string json = "{}";
            for (int i = 0; i < SftpBookmarks.MaxPerHost + 5; i++)
            {
                json = SftpBookmarks.Add(json, "h", "/p" + i);
            }
            var list = SftpBookmarks.Get(json, "h");
            Assert.Equal(SftpBookmarks.MaxPerHost, list.Count);
            Assert.Equal("/p" + (SftpBookmarks.MaxPerHost + 4), list[0]);
            Assert.DoesNotContain("/p0", list);
        }

        [Fact]
        public void Remove_LastPath_DropsHostKey()
        {
            string json = SftpBookmarks.Add("{}", "h", "/a");
            json = SftpBookmarks.Remove(json, "h", "/a");
            Assert.Empty(SftpBookmarks.Get(json, "h"));
            Assert.Equal("{}", json);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("[1,2]")]
        [InlineData("{\"h\":\"notarray\"}")]
        public void Get_BadJson_TreatedAsEmpty(string json)
        {
            Assert.Empty(SftpBookmarks.Get(json, "h"));
            Assert.Single(SftpBookmarks.Get(SftpBookmarks.Add(json, "h", "/x"), "h"));
        }

        [Fact]
        public void Add_IgnoresBlankInput()
        {
            Assert.Equal("{}", SftpBookmarks.Add("{}", "h", "  "));
            Assert.Equal("{}", SftpBookmarks.Add("{}", null, "/x"));
        }
    }
}
