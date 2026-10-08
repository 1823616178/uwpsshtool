using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using SshTool.Core.Storage;
using Xunit;

namespace SshTool.Core.Tests.Storage
{
    // opt/full-pass：凭据表损坏时隔离并从空表开始，IO 异常不吞。
    public class SecretMapCodecTests
    {
        private sealed class FakeFile : ISecureFile, ISecureFileQuarantine
        {
            public byte[] Data = new byte[0];
            public Exception ReadError;
            public bool QuarantineResult = true;
            public bool QuarantineThrows;
            public int QuarantineCalls;
            public string LastReason;
            public bool Discarded;

            public bool DiscardedOnLastRead
            {
                get { return Discarded; }
            }

            public Task<byte[]> ReadAsync()
            {
                if (ReadError != null)
                {
                    throw ReadError;
                }
                return Task.FromResult(Data);
            }

            public Task WriteAsync(byte[] plaintext)
            {
                Data = plaintext ?? new byte[0];
                return Task.CompletedTask;
            }

            public Task<bool> QuarantineAsync(string reason)
            {
                QuarantineCalls++;
                LastReason = reason;
                if (QuarantineThrows)
                {
                    throw new InvalidOperationException("boom");
                }
                return Task.FromResult(QuarantineResult);
            }
        }

        [Fact]
        public async Task ValidJson_LoadsStringValuesOnly()
        {
            var file = new FakeFile { Data = Encoding.UTF8.GetBytes("{\"a\":\"1\",\"b\":2,\"c\":\"x\"}") };
            Dictionary<string, string> map = await SecretMapCodec.LoadAsync(file, null);
            Assert.Equal(2, map.Count);
            Assert.Equal("1", map["a"]);
            Assert.Equal("x", map["c"]);
            Assert.Equal(0, file.QuarantineCalls);
        }

        [Fact]
        public async Task Empty_IsEmptyMap_NoQuarantine()
        {
            var file = new FakeFile();
            Dictionary<string, string> map = await SecretMapCodec.LoadAsync(file, null);
            Assert.Empty(map);
            Assert.Equal(0, file.QuarantineCalls);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("[1,2,3]")]
        [InlineData("{\"a\":")]
        public async Task Corrupt_QuarantinesAndStartsEmpty(string content)
        {
            var file = new FakeFile { Data = Encoding.UTF8.GetBytes(content) };
            var warnings = new List<SecretLoadIssue>();
            Dictionary<string, string> map = await SecretMapCodec.LoadAsync(file, warnings.Add);
            Assert.Empty(map);
            Assert.Equal(1, file.QuarantineCalls);
            Assert.Equal("parse", file.LastReason);
            Assert.Equal(SecretLoadIssue.ContentCorruptQuarantined, Assert.Single(warnings));
        }

        [Fact]
        public async Task Corrupt_QuarantineFailure_StillStartsEmpty()
        {
            var file = new FakeFile { Data = new byte[] { 0xFF, 0xFE, 0x00 }, QuarantineThrows = true };
            var warnings = new List<SecretLoadIssue>();
            Dictionary<string, string> map = await SecretMapCodec.LoadAsync(file, warnings.Add);
            Assert.Empty(map);
            Assert.Equal(SecretLoadIssue.ContentCorruptNotQuarantined, Assert.Single(warnings));
        }

        [Fact]
        public async Task Corrupt_PlainSecureFile_WithoutQuarantine_StartsEmpty()
        {
            var file = new InMemorySecureFile();
            await file.WriteAsync(Encoding.UTF8.GetBytes("garbage"));
            Dictionary<string, string> map = await SecretMapCodec.LoadAsync(file, null);
            Assert.Empty(map);
        }

        // fix/functional-pass（P2-3）：解密失败已由文件层隔离并返回空 → 回报 DecryptFailedQuarantined。
        [Fact]
        public async Task DecryptDiscarded_ReportsIssue()
        {
            var file = new FakeFile { Discarded = true };
            var issues = new List<SecretLoadIssue>();
            Dictionary<string, string> map = await SecretMapCodec.LoadAsync(file, issues.Add);
            Assert.Empty(map);
            Assert.Equal(SecretLoadIssue.DecryptFailedQuarantined, Assert.Single(issues));
            Assert.Equal(0, file.QuarantineCalls);
        }

        [Fact]
        public async Task EmptyFile_NotDiscarded_ReportsNothing()
        {
            var file = new FakeFile();
            var issues = new List<SecretLoadIssue>();
            await SecretMapCodec.LoadAsync(file, issues.Add);
            Assert.Empty(issues);
        }

        [Fact]
        public async Task ReadIoError_Propagates_NoQuarantine()
        {
            var file = new FakeFile { ReadError = new System.IO.IOException("locked") };
            await Assert.ThrowsAsync<System.IO.IOException>(() => SecretMapCodec.LoadAsync(file, null));
            Assert.Equal(0, file.QuarantineCalls);
        }

        [Fact]
        public void Serialize_RoundTrips()
        {
            var map = new Dictionary<string, string> { { "k1", "v1" }, { "k2", "密码" } };
            Dictionary<string, string> parsed;
            Assert.True(SecretMapCodec.TryParse(SecretMapCodec.Serialize(map), out parsed));
            Assert.Equal(map, parsed);
        }
    }
}
