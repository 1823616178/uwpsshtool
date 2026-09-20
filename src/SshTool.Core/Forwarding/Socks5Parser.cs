using System;
using System.Globalization;
using System.Text;

namespace SshTool.Core.Forwarding
{
    // RFC 1928 回复码（与桌面端 socks5.ts 的 SocksReply 一致；BND.ADDR/BND.PORT
    // 恒填 0，绝大多数客户端不关心）。数值即线上字节，不做映射。
    public enum SocksReplyCode
    {
        Success = 0x00,
        GeneralFailure = 0x01,
        NetworkUnreachable = 0x03,
        HostUnreachable = 0x04,
        ConnectionRefused = 0x05,
        CommandNotSupported = 0x07
    }

    public enum Socks5ParseKind
    {
        NeedMore, // 数据不足：等下一片再喂
        Reply,    // 需要把 Reply 写回客户端（本连接继续读）
        Request,  // CONNECT 目标已解析；Host/Port/Leftover 有效（本连接停止喂）
        Failure   // 握手失败：Reply 非空 = 先回写再关闭；null = 直接关闭
    }

    public sealed class Socks5ParseResult
    {
        public Socks5ParseKind Kind { get; set; }
        public byte[] Reply { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public byte[] Leftover { get; set; }
        public string Message { get; set; }
    }

    // SOCKS5 服务端握手解析（RFC 1928 子集；01-DESIGN.md §11.2「Core 移植
    // 桌面端 socks5.ts 解析逻辑」）：仅无认证 + CONNECT，地址支持 IPv4/域名/IPv6。
    //
    // 纯逻辑、无 socket 依赖：调用方按到达顺序把字节喂给 Feed()，按结果回写/关闭。
    // 与桌面端行为逐字节一致（含「失败时先写回复再关」与「请求后已到达的应用
    // 数据从 Leftover 返还」）。每条连接一个实例；Request/Failure 之后实例终止。
    // 本机运行时（native fwd）以同源 C++ 移植实现握手（见 native/core/fwd/socks5），
    // 两份实现共用桌面端端口语义，由同套测试向量对拍（F05 偏差，进度日志已注明）。
    public sealed class Socks5Parser
    {
        private const byte Version = 0x05;
        private const byte CommandConnect = 0x01;
        private const byte AddressIpv4 = 0x01;
        private const byte AddressDomain = 0x03;
        private const byte AddressIpv6 = 0x04;
        private const byte MethodNoAuth = 0x00;

        private enum Stage
        {
            Greeting,
            Request,
            Done
        }

        private Stage _stage = Stage.Greeting;
        private readonly System.Collections.Generic.List<byte> _buffer =
            new System.Collections.Generic.List<byte>(256);

        public bool Done
        {
            get { return _stage == Stage.Done; }
        }

        // 喂入一片到达的数据（可任意分片）；返回本片的裁决。
        // Request/Failure 之后实例进入 Done：继续喂入只会得到 NeedMore（调用方
        // 应停止喂入并直接中继数据）。
        public Socks5ParseResult Feed(byte[] chunk, int offset, int count)
        {
            if (chunk == null)
            {
                throw new ArgumentNullException("chunk");
            }
            if (offset < 0 || count < 0 || offset + count > chunk.Length)
            {
                throw new ArgumentOutOfRangeException("count");
            }
            if (_stage == Stage.Done)
            {
                return new Socks5ParseResult { Kind = Socks5ParseKind.NeedMore };
            }
            for (int i = 0; i < count; i++)
            {
                _buffer.Add(chunk[offset + i]);
            }
            byte[] buffered = _buffer.ToArray();
            if (_stage == Stage.Greeting)
            {
                Socks5ParseResult greeting = ParseGreeting(buffered);
                if (greeting.Kind == Socks5ParseKind.Failure)
                {
                    MarkDone();
                }
                return greeting;
            }
            Socks5ParseResult request = ParseRequest(buffered);
            if (request.Kind == Socks5ParseKind.Failure)
            {
                MarkDone();
            }
            return request;
        }

        // 失败同样终止本连接的握手（桌面端 fail() 置 stage='done'）。
        private void MarkDone()
        {
            _stage = Stage.Done;
            _buffer.Clear();
        }

        public Socks5ParseResult Feed(byte[] chunk)
        {
            return Feed(chunk, 0, chunk.Length);
        }

        private Socks5ParseResult ParseGreeting(byte[] buffered)
        {
            if (buffered.Length < 2)
            {
                return NeedMore();
            }
            if (buffered[0] != Version)
            {
                return Fail(null, "不支持的 SOCKS 版本 " + buffered[0].ToString(
                    CultureInfo.InvariantCulture));
            }
            int methodCount = buffered[1];
            if (buffered.Length < 2 + methodCount)
            {
                return NeedMore();
            }
            bool hasNoAuth = false;
            for (int i = 2; i < 2 + methodCount; i++)
            {
                if (buffered[i] == MethodNoAuth)
                {
                    hasNoAuth = true;
                    break;
                }
            }
            _buffer.RemoveRange(0, 2 + methodCount);
            if (!hasNoAuth)
            {
                return Fail(BuildMethodSelection(false), "客户端要求认证，本代理仅支持无认证");
            }
            _stage = Stage.Request;
            return new Socks5ParseResult
            {
                Kind = Socks5ParseKind.Reply,
                Reply = BuildMethodSelection(true)
            };
        }

        private Socks5ParseResult ParseRequest(byte[] buffered)
        {
            if (buffered.Length < 4)
            {
                return NeedMore();
            }
            if (buffered[0] != Version)
            {
                return Fail(null, "不支持的 SOCKS 版本 " + buffered[0].ToString(
                    CultureInfo.InvariantCulture));
            }
            if (buffered[1] != CommandConnect)
            {
                return Fail(BuildReply(SocksReplyCode.CommandNotSupported),
                    "仅支持 CONNECT 命令（不支持 BIND / UDP）");
            }

            byte addressType = buffered[3];
            string host;
            int offset;
            if (addressType == AddressIpv4)
            {
                if (buffered.Length < 10)
                {
                    return NeedMore();
                }
                host = buffered[4].ToString(CultureInfo.InvariantCulture) + "." +
                       buffered[5].ToString(CultureInfo.InvariantCulture) + "." +
                       buffered[6].ToString(CultureInfo.InvariantCulture) + "." +
                       buffered[7].ToString(CultureInfo.InvariantCulture);
                offset = 8;
            }
            else if (addressType == AddressDomain)
            {
                int domainLength = buffered[4];
                if (buffered.Length < 5 + domainLength + 2)
                {
                    return NeedMore();
                }
                host = Encoding.UTF8.GetString(buffered, 5, domainLength);
                offset = 5 + domainLength;
            }
            else if (addressType == AddressIpv6)
            {
                if (buffered.Length < 22)
                {
                    return NeedMore();
                }
                var parts = new System.Collections.Generic.List<string>(8);
                for (int i = 0; i < 16; i += 2)
                {
                    int value = (buffered[4 + i] << 8) | buffered[5 + i];
                    parts.Add(value.ToString("x", CultureInfo.InvariantCulture));
                }
                host = string.Join(":", parts.ToArray());
                offset = 20;
            }
            else
            {
                return Fail(BuildReply(SocksReplyCode.GeneralFailure),
                    "不支持的地址类型 " + addressType.ToString(CultureInfo.InvariantCulture));
            }

            int port = (buffered[offset] << 8) | buffered[offset + 1];
            _stage = Stage.Done;
            byte[] leftover = new byte[buffered.Length - (offset + 2)];
            Array.Copy(buffered, offset + 2, leftover, 0, leftover.Length);
            _buffer.Clear();
            return new Socks5ParseResult
            {
                Kind = Socks5ParseKind.Request,
                Host = host,
                Port = port,
                Leftover = leftover
            };
        }

        private static Socks5ParseResult NeedMore()
        {
            return new Socks5ParseResult { Kind = Socks5ParseKind.NeedMore };
        }

        // 失败（回不回复、回复字节、诊断文本）；reply 为 null 表示直接关闭不回复
        //（与桌面端 fail() 的 sock destroy 一致）。
        private static Socks5ParseResult Fail(byte[] reply, string message)
        {
            return new Socks5ParseResult
            {
                Kind = Socks5ParseKind.Failure,
                Reply = reply,
                Message = message
            };
        }

        // BND.ADDR/BND.PORT 填 0 的固定回复帧（VER, REP, RSV, ATYP=IPv4, 0,0,0,0, port=0）。
        public static byte[] BuildReply(SocksReplyCode code)
        {
            return new byte[]
            {
                Version, (byte)code, 0x00, AddressIpv4, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
            };
        }

        // 方法选择帧：ok=false 即「无可接受方法」（0xFF）。
        public static byte[] BuildMethodSelection(bool ok)
        {
            return new byte[] { Version, ok ? MethodNoAuth : (byte)0xFF };
        }
    }
}
