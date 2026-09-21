using System;
using System.Collections.Generic;
using SshTool.Core.Models;
using SshTool.Core.Sessions;
using Xunit;

namespace SshTool.Core.Tests.Sessions
{
    public class JumpChainPlannerTests
    {
        private static Host MakeHost(string id, string name, string jumpId = null)
        {
            return new Host
            {
                Id = id,
                Name = name,
                HostName = name.ToLowerInvariant() + ".example.com",
                Port = 22,
                Username = "root",
                JumpHostId = jumpId
            };
        }

        [Fact]
        public void Plan_NullTarget_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() => JumpChainPlanner.Plan(null, Array.Empty<Host>()));
        }

        [Fact]
        public void Plan_NullHostsList_ThrowsArgumentNullException()
        {
            Host target = MakeHost("h1", "Target");
            Assert.Throws<ArgumentNullException>(() => JumpChainPlanner.Plan(target, null));
        }

        [Fact]
        public void Plan_NoJumpHost_ReturnsSingleHopPlan()
        {
            Host target = MakeHost("h1", "DirectServer");
            JumpChainPlan plan = JumpChainPlanner.Plan(target, new[] { target });

            Assert.False(plan.HasJump);
            Assert.Equal(1, plan.TotalHops);
            Assert.Same(target, plan.Target.Host);
            Assert.True(plan.Target.IsTarget);
            Assert.Equal(1, plan.Target.HopIndex);
            Assert.Equal(1, plan.Target.TotalHops);
            Assert.Equal("DirectServer", plan.Target.Title);
        }

        [Fact]
        public void Plan_SingleJumpHop_ReturnsTwoHopsInOrder()
        {
            Host jump1 = MakeHost("j1", "JumpHost1");
            Host target = MakeHost("t1", "TargetServer", jumpId: "j1");

            var hosts = new[] { jump1, target };
            JumpChainPlan plan = JumpChainPlanner.Plan(target, hosts);

            Assert.True(plan.HasJump);
            Assert.Equal(2, plan.TotalHops);
            Assert.Same(jump1, plan.Hops[0].Host);
            Assert.False(plan.Hops[0].IsTarget);
            Assert.Equal(1, plan.Hops[0].HopIndex);
            Assert.Equal("[1/2] JumpHost1", plan.Hops[0].Title);

            Assert.Same(target, plan.Hops[1].Host);
            Assert.True(plan.Hops[1].IsTarget);
            Assert.Equal(2, plan.Hops[1].HopIndex);
            Assert.Equal("[2/2] TargetServer", plan.Hops[1].Title);
        }

        [Fact]
        public void Plan_ThreeHopsChain_ReturnsCorrectOrder()
        {
            // Chain: target -> j2 -> j1
            Host j1 = MakeHost("j1", "Bastion");
            Host j2 = MakeHost("j2", "InternalRouter", jumpId: "j1");
            Host target = MakeHost("t1", "Database", jumpId: "j2");

            var hosts = new[] { target, j1, j2 };
            JumpChainPlan plan = JumpChainPlanner.Plan(target, hosts);

            Assert.True(plan.HasJump);
            Assert.Equal(3, plan.TotalHops);
            Assert.Equal("j1", plan.Hops[0].Host.Id);
            Assert.Equal("j2", plan.Hops[1].Host.Id);
            Assert.Equal("t1", plan.Hops[2].Host.Id);
            Assert.Equal("[1/3] Bastion", plan.Hops[0].Title);
            Assert.Equal("[2/3] InternalRouter", plan.Hops[1].Title);
            Assert.Equal("[3/3] Database", plan.Hops[2].Title);
        }

        [Fact]
        public void Plan_MaxAllowedDepth_5Hops_Succeeds()
        {
            // 4 jump hosts + 1 target = 5 total
            Host h1 = MakeHost("h1", "H1");
            Host h2 = MakeHost("h2", "H2", "h1");
            Host h3 = MakeHost("h3", "H3", "h2");
            Host h4 = MakeHost("h4", "H4", "h3");
            Host h5 = MakeHost("h5", "H5", "h4");

            var hosts = new[] { h1, h2, h3, h4, h5 };
            JumpChainPlan plan = JumpChainPlanner.Plan(h5, hosts);

            Assert.Equal(5, plan.TotalHops);
            Assert.Equal("h1", plan.Hops[0].Host.Id);
            Assert.Equal("h5", plan.Target.Host.Id);
        }

        [Fact]
        public void Plan_ExceedsMaxDepth6_ThrowsInvalidOperationException()
        {
            // 5 jump hosts + 1 target = 6 total > 5
            Host h1 = MakeHost("h1", "H1");
            Host h2 = MakeHost("h2", "H2", "h1");
            Host h3 = MakeHost("h3", "H3", "h2");
            Host h4 = MakeHost("h4", "H4", "h3");
            Host h5 = MakeHost("h5", "H5", "h4");
            Host h6 = MakeHost("h6", "H6", "h5");

            var hosts = new[] { h1, h2, h3, h4, h5, h6 };
            var ex = Assert.Throws<InvalidOperationException>(() => JumpChainPlanner.Plan(h6, hosts));
            Assert.Contains("exceeds maximum depth", ex.Message);
        }

        [Fact]
        public void Plan_SelfLoop_ThrowsInvalidOperationException()
        {
            Host loop = MakeHost("h1", "Self", jumpId: "h1");
            var ex = Assert.Throws<InvalidOperationException>(() => JumpChainPlanner.Plan(loop, new[] { loop }));
            Assert.Contains("Self loop", ex.Message);
        }

        [Fact]
        public void Plan_CycleDetection_ThrowsInvalidOperationException()
        {
            // Cycle: t -> j1 -> j2 -> j1
            Host j1 = MakeHost("j1", "J1", "j2");
            Host j2 = MakeHost("j2", "J2", "j1");
            Host target = MakeHost("t", "Target", "j1");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                JumpChainPlanner.Plan(target, new[] { target, j1, j2 }));
            Assert.Contains("Cycle detected", ex.Message);
        }

        [Fact]
        public void Plan_MissingJumpHost_ThrowsInvalidOperationException()
        {
            Host target = MakeHost("t", "Target", jumpId: "nonexistent-id");
            var ex = Assert.Throws<InvalidOperationException>(() =>
                JumpChainPlanner.Plan(target, new[] { target }));
            Assert.Contains("Jump host not found", ex.Message);
        }
    }
}
