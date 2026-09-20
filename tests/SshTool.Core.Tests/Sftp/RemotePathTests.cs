using SshTool.Core.Sftp;
using Xunit;

namespace SshTool.Core.Tests.Sftp
{
    public class RemotePathTests
    {
        [Theory]
        [InlineData(null, "/")]
        [InlineData("", "/")]
        [InlineData("   ", "/   ")]
        [InlineData("/", "/")]
        [InlineData("//", "/")]
        [InlineData("/a/b/c", "/a/b/c")]
        [InlineData("/a//b///c", "/a/b/c")]
        [InlineData("/a/./b", "/a/b")]
        [InlineData("/a/b/../c", "/a/c")]
        [InlineData("/a/b/../../c", "/c")]
        [InlineData("/../..", "/")]
        [InlineData("/a/b/..", "/a")]
        [InlineData("/a/b/", "/a/b")]
        [InlineData("a/b/c", "/a/b/c")]
        [InlineData("a/./b/../c", "/a/c")]
        [InlineData("\\a\\b\\c", "/a/b/c")]
        [InlineData("C:\\x\\y", "/C:/x/y")]
        [InlineData("/a/.../b", "/a/.../b")]
        [InlineData("/.", "/")]
        [InlineData("/..", "/")]
        public void Normalize_Cases(string input, string expected)
        {
            Assert.Equal(expected, RemotePath.Normalize(input));
        }

        [Theory]
        [InlineData("/", "a", "/a")]
        [InlineData("/a", "b", "/a/b")]
        [InlineData("/a/", "b", "/a/b")]
        [InlineData("/a", "b/c", "/a/b/c")]
        [InlineData("/a", "..", "/")]
        [InlineData("/a", "../b", "/b")]
        [InlineData("", "x", "/x")]
        [InlineData("/a", "", "/a")]
        [InlineData("/", "", "/")]
        public void Combine_Cases(string dir, string name, string expected)
        {
            Assert.Equal(expected, RemotePath.Combine(dir, name));
        }

        [Theory]
        [InlineData("/", "")]
        [InlineData("/a", "a")]
        [InlineData("/a/b", "b")]
        [InlineData("/a/b/", "b")]
        [InlineData("", "")]
        public void GetFileName_Cases(string path, string expected)
        {
            Assert.Equal(expected, RemotePath.GetFileName(path));
        }

        [Theory]
        [InlineData("/", "/")]
        [InlineData("/a", "/")]
        [InlineData("/a/b", "/a")]
        [InlineData("/a/b/c", "/a/b")]
        [InlineData("", "/")]
        public void GetDirectoryName_Cases(string path, string expected)
        {
            Assert.Equal(expected, RemotePath.GetDirectoryName(path));
        }

        [Theory]
        [InlineData("/a", true)]
        [InlineData("/", true)]
        [InlineData("a", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsAbsolute_Cases(string path, bool expected)
        {
            Assert.Equal(expected, RemotePath.IsAbsolute(path));
        }
    }
}
