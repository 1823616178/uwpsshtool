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
            var warnings = new List<string>();
            Dictionary<string, string> map = await SecretMapCodec.LoadAsync(file, warnings.Add);
            Assert.Empty(map);
            Assert.Equal(1, file.QuarantineCalls);
            Assert.Equal("parse", file.LastReason);
            Assert.Single(warnings);
        }

        [Fact]
        public async Task Corrupt_QuarantineFailure_StillStartsEmpty()
        {
            var file = new FakeFile { Data = new byte[] { 0xFF, 0xFE, 0x00 }, QuarantineThrows = true };
            var warnings = new List<string>();
            Dictionary<string, string> map = await SecretMapCodec.LoadAsync(file, warnings.Add);
            Assert.Empty(map);
            Assert.Single(warnings);
        }

        [Fact]
        public async Task Corrupt_PlainSecureFile_WithoutQuarantine_StartsEmpty()
        {
            var file = new InMemorySecureFile();
            await file.WriteAsync(Encoding.UTF8.GetBytes("garbage"));
            Dictionary<string, string> map = await SecretMapCodec.LoadAsync(file, null);
            Assert.Empty(map);
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
