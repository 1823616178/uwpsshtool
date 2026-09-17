using SshTool.Core.Spikes;
using Xunit;

namespace SshTool.Core.Tests.Spikes
{
    public class JsonSpikeTests
    {
        [Fact]
        public void RoundTrip_AllChecksPass()
        {
            var checks = JsonSpike.RunChecks();
            Assert.All(checks, c => Assert.True(c.Passed, c.Name + ": " + c.Detail));
        }

        [Fact]
        public void RoundTrip_ReportSaysPass()
        {
            Assert.Contains("RESULT: PASS", JsonSpike.RoundTrip());
        }
    }
}
