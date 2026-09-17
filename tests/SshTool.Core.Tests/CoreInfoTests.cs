using SshTool.Core;
using Xunit;

namespace SshTool.Core.Tests
{
    public class CoreInfoTests
    {
        [Fact]
        public void Version_IsExpected()
        {
            Assert.Equal("0.0.1", CoreInfo.Version);
        }
    }
}
