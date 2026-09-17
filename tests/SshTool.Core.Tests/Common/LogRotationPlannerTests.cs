using System.Collections.Generic;
using System.Linq;
using SshTool.Core.Common;
using Xunit;

namespace SshTool.Core.Tests.Common
{
    public class LogRotationPlannerTests
    {
        private const string Log = @"C:\data\logs\app.log";

        [Fact]
        public void UnderLimit_NoSteps()
        {
            var steps = LogRotationPlanner.Plan(Log, 512 * 1024, new HashSet<string>());
            Assert.Empty(steps);
        }

        [Fact]
        public void OverLimit_NoHistory_RenamesCurrentOnly()
        {
            var steps = LogRotationPlanner.Plan(Log, 2 * 1024 * 1024, new HashSet<string>());
            var step = Assert.Single(steps);
            Assert.Equal(RotationStepType.Rename, step.Type);
            Assert.Equal(Log, step.SourcePath);
            Assert.Equal(Log + ".1", step.TargetPath);
        }

        [Fact]
        public void OverLimit_FullHistory_DeletesOldestAndShifts()
        {
            var existing = new HashSet<string> { Log + ".1", Log + ".2", Log + ".3" };
            var steps = LogRotationPlanner.Plan(Log, LogRotationPlanner.DefaultMaxBytes, existing);

            Assert.Equal(4, steps.Count);
            Assert.Equal(RotationStepType.Delete, steps[0].Type);
            Assert.Equal(Log + ".3", steps[0].SourcePath);
            Assert.Equal(Log + ".2", steps[1].SourcePath);
            Assert.Equal(Log + ".3", steps[1].TargetPath);
            Assert.Equal(Log + ".1", steps[2].SourcePath);
            Assert.Equal(Log + ".2", steps[2].TargetPath);
            Assert.Equal(Log, steps[3].SourcePath);
            Assert.Equal(Log + ".1", steps[3].TargetPath);
        }

        [Fact]
        public void OverLimit_PartialHistory_SkipsMissing()
        {
            var existing = new HashSet<string> { Log + ".2" };
            var steps = LogRotationPlanner.Plan(Log, LogRotationPlanner.DefaultMaxBytes, existing);

            Assert.Equal(2, steps.Count);
            Assert.Equal(RotationStepType.Rename, steps[0].Type);
            Assert.Equal(Log + ".2", steps[0].SourcePath);
            Assert.Equal(Log + ".3", steps[0].TargetPath);
            Assert.Equal(Log, steps[1].SourcePath);
        }
    }
}
