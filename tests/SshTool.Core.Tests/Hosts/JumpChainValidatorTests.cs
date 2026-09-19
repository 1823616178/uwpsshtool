using System.Collections.Generic;
using SshTool.Core.Hosts;
using SshTool.Core.Models;
using Xunit;

namespace SshTool.Core.Tests.Hosts
{
    public class JumpChainValidatorTests
    {
        private static Host H(string id, string jump = null)
        {
            var host = Defaults.NewHost();
            host.Id = id;
            host.Name = id;
            host.HostName = id + ".local";
            host.Username = "u";
            host.JumpHostId = jump;
            return host;
        }

        [Fact]
        public void EmptyJump_Ok()
        {
            Assert.Equal(JumpChainStatus.None, JumpChainValidator.Validate("a", null, new[] { H("a") }));
            Assert.Equal(JumpChainStatus.None, JumpChainValidator.Validate("a", "", new[] { H("a") }));
        }

        [Fact]
        public void SelfLoop_Detected()
        {
            Assert.Equal(JumpChainStatus.SelfLoop, JumpChainValidator.Validate("a", "a", new[] { H("a") }));
        }

        [Fact]
        public void TwoHostCycle_Detected()
        {
            var hosts = new[] { H("a", "b"), H("b", "a") };
            Assert.Equal(JumpChainStatus.Cycle, JumpChainValidator.Validate("a", "b", hosts));
        }

        [Fact]
        public void MultiLevelCycle_Detected()
        {
            var hosts = new[] { H("a"), H("b", "c"), H("c", "d"), H("d", "b") };
            Assert.Equal(JumpChainStatus.Cycle, JumpChainValidator.Validate("a", "b", hosts));
        }

        [Fact]
        public void DepthFive_Ok()
        {
            // a → b → c → d → e  共 5 台
            var hosts = new[] { H("a"), H("b", "c"), H("c", "d"), H("d", "e"), H("e") };
            Assert.Equal(JumpChainStatus.None, JumpChainValidator.Validate("a", "b", hosts));
        }

        [Fact]
        public void DepthSix_TooDeep()
        {
            var hosts = new[] { H("a"), H("b", "c"), H("c", "d"), H("d", "e"), H("e", "f"), H("f") };
            Assert.Equal(JumpChainStatus.TooDeep, JumpChainValidator.Validate("a", "b", hosts));
        }

        [Fact]
        public void EligibleJumps_ExcludesSelfAndCycle()
        {
            var hosts = new List<Host> { H("a"), H("b", "a"), H("c") };
            List<Host> eligible = JumpChainValidator.EligibleJumps("a", hosts);
            Assert.Single(eligible);
            Assert.Equal("c", eligible[0].Id);
        }
    }
}
