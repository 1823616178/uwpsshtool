using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SshTool.Core.Forwarding;
using Xunit;

namespace SshTool.Core.Tests.Forwarding
{
    // F05：SOCKS5 服务端握手解析（桌面端 socks5.ts 的 Core 移植）——分片到达、
    // 非法版本、不支持命令、地址类型与回复帧的字节级行为。
    public class Socks5ParserTests
    {
        private static byte[] Greeting(params byte[] methods)
        {
            var bytes = new List<byte> { 0x05, (byte)methods.Length };
            bytes.AddRange(methods);
            return bytes.ToArray();
        }

        private static byte[] Request(byte atyp, byte[] addr, int port)
        {
            var bytes = new List<byte> { 0x05, 0x01, 0x00, atyp };
            if (addr != null)
            {
                bytes.AddRange(addr);
            }
            bytes.Add((byte)(port >> 8));
            bytes.Add((byte)(port & 0xFF));
            return bytes.ToArray();
        }

        private static byte[] Domain(string host)
        {
            byte[] raw = Encoding.UTF8.GetBytes(host);
            var bytes = new byte[raw.Length + 1];
            bytes[0] = (byte)raw.Length;
            Array.Copy(raw, 0, bytes, 1, raw.Length);
            return bytes;
        }

        [Fact]
        public void GreetingNoAuthSelectsMethodAndReplies()
        {
            var parser = new Socks5Parser();
            Socks5ParseResult result = parser.Feed(Greeting(0x00));
            Assert.Equal(Socks5ParseKind.Reply, result.Kind);
            Assert.Equal(new byte[] { 0x05, 0x00 }, result.Reply);
            Assert.False(parser.Done);
        }

        [Fact]
        public void GreetingFragmentsReachReplyIdentically()
        {
            byte[] greeting = Greeting(0x00, 0x01);
            var parser = new Socks5Parser();
            Assert.Equal(Socks5ParseKind.NeedMore, parser.Feed(new[] { greeting[0] }).Kind);
            Assert.Equal(Socks5ParseKind.NeedMore, parser.Feed(new[] { greeting[1] }).Kind);
            Socks5ParseResult result = parser.Feed(new[] { greeting[2], greeting[3] });
            Assert.Equal(Socks5ParseKind.Reply, result.Kind);
            Assert.Equal(new byte[] { 0x05, 0x00 }, result.Reply);
        }

        [Fact]
        public void GreetingOneByteChunksReachReplyIdentically()
        {
            byte[] greeting = Greeting(0x00);
            var parser = new Socks5Parser();
            for (int i = 0; i < greeting.Length - 1; i++)
            {
                Assert.Equal(Socks5ParseKind.NeedMore, parser.Feed(new[] { greeting[i] }).Kind);
            }
            Socks5ParseResult result = parser.Feed(new[] { greeting[greeting.Length - 1] });
            Assert.Equal(Socks5ParseKind.Reply, result.Kind);
        }

        [Fact]
        public void GreetingWrongVersionFailsWithoutReply()
        {
            var parser = new Socks5Parser();
            Socks5ParseResult result = parser.Feed(new byte[] { 0x04, 0x01, 0x00 });
            Assert.Equal(Socks5ParseKind.Failure, result.Kind);
            Assert.Null(result.Reply);
            Assert.Contains("版本", result.Message);
            Assert.True(parser.Done);
        }

        [Fact]
        public void GreetingAuthOnlyRepliesNoAcceptableAndFails()
        {
            var parser = new Socks5Parser();
            Socks5ParseResult result = parser.Feed(Greeting(0x02));
            Assert.Equal(Socks5ParseKind.Failure, result.Kind);
            Assert.Equal(new byte[] { 0x05, 0xFF }, result.Reply);
            Assert.Contains("无认证", result.Message);
        }

        [Fact]
        public void GreetingMixedMethodsWithNoAuthSucceeds()
        {
            var parser = new Socks5Parser();
            Socks5ParseResult result = parser.Feed(Greeting(0x02, 0x00, 0x01));
            Assert.Equal(Socks5ParseKind.Reply, result.Kind);
            Assert.Equal(new byte[] { 0x05, 0x00 }, result.Reply);
        }

        [Fact]
        public void GreetingZeroMethodsFailsWithNoAcceptableReply()
        {
            var parser = new Socks5Parser();
            Socks5ParseResult result = parser.Feed(Greeting());
            Assert.Equal(Socks5ParseKind.Failure, result.Kind);
            Assert.Equal(new byte[] { 0x05, 0xFF }, result.Reply);
        }

        [Fact]
        public void ConnectIpv4ParsesHostPort()
        {
            var parser = new Socks5Parser();
            Assert.Equal(Socks5ParseKind.Reply, parser.Feed(Greeting(0x00)).Kind);
            Socks5ParseResult result = parser.Feed(Request(0x01, new byte[] { 1, 2, 3, 4 }, 80));
            Assert.Equal(Socks5ParseKind.Request, result.Kind);
            Assert.Equal("1.2.3.4", result.Host);
            Assert.Equal(80, result.Port);
            Assert.Empty(result.Leftover);
            Assert.True(parser.Done);
        }

        [Fact]
        public void ConnectDomainParsesHostPort()
        {
            var parser = new Socks5Parser();
            parser.Feed(Greeting(0x00));
            Socks5ParseResult result = parser.Feed(Request(0x03, Domain("example.com"), 443));
            Assert.Equal(Socks5ParseKind.Request, result.Kind);
            Assert.Equal("example.com", result.Host);
            Assert.Equal(443, result.Port);
        }

        [Fact]
        public void ConnectIpv6ParsesLowercaseHexGroups()
        {
            var parser = new Socks5Parser();
            parser.Feed(Greeting(0x00));
            byte[] address =
            {
                0x20, 0x01, 0x0d, 0xb8, 0x00, 0x01, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01
            };
            Socks5ParseResult result = parser.Feed(Request(0x04, address, 8080));
            Assert.Equal(Socks5ParseKind.Request, result.Kind);
            // 与桌面端一致：逐组小写十六进制、不压缩零组。
            Assert.Equal("2001:db8:1:0:0:0:0:1", result.Host);
            Assert.Equal(8080, result.Port);
        }

        [Fact]
        public void ConnectIpv4FragmentsWaitForFullRequest()
        {
            var parser = new Socks5Parser();
            parser.Feed(Greeting(0x00));
            byte[] request = Request(0x01, new byte[] { 9, 8, 7, 6 }, 1234);
            for (int i = 0; i < 6; i++)
            {
                Assert.Equal(Socks5ParseKind.NeedMore, parser.Feed(new[] { request[i] }).Kind);
            }
            Socks5ParseResult result = parser.Feed(
                new ArraySegment<byte>(request, 6, request.Length - 6).ToArray());
            Assert.Equal(Socks5ParseKind.Request, result.Kind);
            Assert.Equal("9.8.7.6", result.Host);
            Assert.Equal(1234, result.Port);
        }

        [Fact]
        public void PipelinedApplicationDataReturnsAsLeftover()
        {
            var parser = new Socks5Parser();
            parser.Feed(Greeting(0x00));
            byte[] request = Request(0x01, new byte[] { 127, 0, 0, 1 }, 995);
            var combined = request.Concat(new byte[] { 0x16, 0x03, 0x01 }).ToArray();
            Socks5ParseResult result = parser.Feed(combined);
            Assert.Equal(Socks5ParseKind.Request, result.Kind);
            Assert.Equal(new byte[] { 0x16, 0x03, 0x01 }, result.Leftover);
        }

        [Fact]
        public void BindCommandIsRejectedWithReplyThenClose()
        {
            var parser = new Socks5Parser();
            parser.Feed(Greeting(0x00));
            var bytes = new List<byte> { 0x05, 0x02, 0x00, 0x01 };
            bytes.AddRange(new byte[] { 1, 2, 3, 4 });
            bytes.AddRange(new byte[] { 0, 80 });
            Socks5ParseResult result = parser.Feed(bytes.ToArray());
            Assert.Equal(Socks5ParseKind.Failure, result.Kind);
            Assert.Equal(Socks5Parser.BuildReply(SocksReplyCode.CommandNotSupported), result.Reply);
            Assert.Contains("CONNECT", result.Message);
            Assert.True(parser.Done);
        }

        [Fact]
        public void UdpAssociateCommandIsRejected()
        {
            var parser = new Socks5Parser();
            parser.Feed(Greeting(0x00));
            var bytes = new List<byte> { 0x05, 0x03, 0x00, 0x01 };
            bytes.AddRange(new byte[] { 0, 0, 0, 0 });
            bytes.AddRange(new byte[] { 0, 53 });
            Socks5ParseResult result = parser.Feed(bytes.ToArray());
            Assert.Equal(Socks5ParseKind.Failure, result.Kind);
            Assert.Equal(Socks5Parser.BuildReply(SocksReplyCode.CommandNotSupported), result.Reply);
        }

        [Fact]
        public void UnknownAddressTypeFailsWithGeneralFailure()
        {
            var parser = new Socks5Parser();
            parser.Feed(Greeting(0x00));
            var bytes = new List<byte> { 0x05, 0x01, 0x00, 0x05 };
            bytes.AddRange(new byte[] { 1, 2, 3, 4, 5, 6 });
            Socks5ParseResult result = parser.Feed(bytes.ToArray());
            Assert.Equal(Socks5ParseKind.Failure, result.Kind);
            Assert.Equal(Socks5Parser.BuildReply(SocksReplyCode.GeneralFailure), result.Reply);
            Assert.Contains("地址类型", result.Message);
        }

        [Fact]
        public void RequestWrongVersionFailsWithoutReply()
        {
            var parser = new Socks5Parser();
            parser.Feed(Greeting(0x00));
            var bytes = new List<byte> { 0x04, 0x01, 0x00, 0x01 };
            bytes.AddRange(new byte[] { 1, 2, 3, 4, 0, 80 });
            Socks5ParseResult result = parser.Feed(bytes.ToArray());
            Assert.Equal(Socks5ParseKind.Failure, result.Kind);
            Assert.Null(result.Reply);
            Assert.Contains("版本", result.Message);
        }

        [Fact]
        public void FeedAfterDoneReturnsNeedMore()
        {
            var parser = new Socks5Parser();
            parser.Feed(Greeting(0x00));
            Assert.Equal(Socks5ParseKind.Request, parser.Feed(
                Request(0x01, new byte[] { 1, 1, 1, 1 }, 22)).Kind);
            Assert.Equal(Socks5ParseKind.NeedMore, parser.Feed(new byte[] { 0x01 }).Kind);
            Assert.True(parser.Done);
        }

        [Fact]
        public void ReplyFrameShapeIsFixed()
        {
            byte[] success = Socks5Parser.BuildReply(SocksReplyCode.Success);
            Assert.Equal(10, success.Length);
            Assert.Equal(0x05, success[0]);
            Assert.Equal(0x00, success[1]);
            Assert.Equal(0x00, success[2]);
            Assert.Equal(0x01, success[3]);
            Assert.True(success.Skip(4).All(b => b == 0));
            byte[] refused = Socks5Parser.BuildReply(SocksReplyCode.ConnectionRefused);
            Assert.Equal(0x05, refused[0]);
            Assert.Equal(0x05, refused[1]);
            byte[] methodOk = Socks5Parser.BuildMethodSelection(true);
            Assert.Equal(new byte[] { 0x05, 0x00 }, methodOk);
            byte[] methodNo = Socks5Parser.BuildMethodSelection(false);
            Assert.Equal(new byte[] { 0x05, 0xFF }, methodNo);
        }

        [Fact]
        public void DomainWithMultibyteHostDecodesUtf8()
        {
            var parser = new Socks5Parser();
            parser.Feed(Greeting(0x00));
            Socks5ParseResult result = parser.Feed(Request(0x03, Domain("例え.jp"), 22));
            Assert.Equal(Socks5ParseKind.Request, result.Kind);
            Assert.Equal("例え.jp", result.Host);
            Assert.Equal(22, result.Port);
        }

        [Fact]
        public void MaxDomainLength254Accepted()
        {
            var parser = new Socks5Parser();
            parser.Feed(Greeting(0x00));
            string host = new string('a', 254);
            Socks5ParseResult result = parser.Feed(Request(0x03, Domain(host), 1));
            Assert.Equal(Socks5ParseKind.Request, result.Kind);
            Assert.Equal(host, result.Host);
        }
    }
}
