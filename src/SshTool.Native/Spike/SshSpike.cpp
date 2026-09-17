#include "pch.h"
#include "Spike/SshSpike.h"

#include <winsock2.h>
#include <ws2tcpip.h>
#include <libssh2.h>

#include <chrono>
#include <sstream>
#include <string>
#include <vector>

using namespace SshTool::Native;
using namespace Platform;
using namespace Windows::Foundation;
using namespace concurrency;

namespace
{
    std::string ToUtf8(String^ value)
    {
        if (value == nullptr || value->IsEmpty()) { return std::string(); }
        const wchar_t* data = value->Data();
        int needed = ::WideCharToMultiByte(CP_UTF8, 0, data, (int)value->Length(), nullptr, 0, nullptr, nullptr);
        std::string out((size_t)needed, '\0');
        if (needed > 0)
        {
            ::WideCharToMultiByte(CP_UTF8, 0, data, (int)value->Length(), &out[0], needed, nullptr, nullptr);
        }
        return out;
    }

    String^ ToPlatform(const std::string& value)
    {
        if (value.empty()) { return ref new String(L""); }
        int needed = ::MultiByteToWideChar(CP_UTF8, 0, value.c_str(), (int)value.size(), nullptr, 0);
        std::wstring out((size_t)needed, L'\0');
        if (needed > 0)
        {
            ::MultiByteToWideChar(CP_UTF8, 0, value.c_str(), (int)value.size(), &out[0], needed);
        }
        return ref new String(out.c_str(), (unsigned int)out.size());
    }

    long long NowMs()
    {
        using namespace std::chrono;
        return duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count();
    }

    // 协商结果：libssh2_session_methods 在握手后才有值
    void AppendMethods(std::ostringstream& out, LIBSSH2_SESSION* session)
    {
        struct { int id; const char* label; } items[] = {
            { LIBSSH2_METHOD_KEX,      "kex      " },
            { LIBSSH2_METHOD_HOSTKEY,  "hostkey  " },
            { LIBSSH2_METHOD_CRYPT_CS, "cipher   " },
            { LIBSSH2_METHOD_MAC_CS,   "mac      " },
            { LIBSSH2_METHOD_COMP_CS,  "compress " },
        };
        for (size_t i = 0; i < sizeof(items) / sizeof(items[0]); ++i)
        {
            const char* value = libssh2_session_methods(session, items[i].id);
            out << "  " << items[i].label << (value ? value : "(null)") << "\n";
        }
    }

    std::string LastSshError(LIBSSH2_SESSION* session, int rc)
    {
        char* msg = nullptr;
        int len = 0;
        libssh2_session_last_error(session, &msg, &len, 0);
        std::ostringstream out;
        out << "rc=" << rc << " msg=" << (msg ? msg : "(null)");
        return out.str();
    }

    std::string RunExec(const std::string& host, int port, const std::string& user,
                        const std::string& password, const std::string& command)
    {
        std::ostringstream out;
        long long t0 = NowMs();

        WSADATA wsa;
        int rc = ::WSAStartup(MAKEWORD(2, 2), &wsa);
        if (rc != 0) { out << "FAIL WSAStartup rc=" << rc << "\n"; return out.str(); }
        out << "[ok] WSAStartup\n";

        if (libssh2_init(0) != 0)
        {
            out << "FAIL libssh2_init\n";
            ::WSACleanup();
            return out.str();
        }
        out << "[ok] libssh2_init " << libssh2_version(0) << "\n";

        SOCKET sock = INVALID_SOCKET;
        LIBSSH2_SESSION* session = nullptr;
        LIBSSH2_CHANNEL* channel = nullptr;
        addrinfo* resolved = nullptr;

        do
        {
            addrinfo hints;
            ZeroMemory(&hints, sizeof(hints));
            hints.ai_family = AF_UNSPEC;
            hints.ai_socktype = SOCK_STREAM;
            hints.ai_protocol = IPPROTO_TCP;

            std::ostringstream portText;
            portText << port;
            long long tDns = NowMs();
            rc = ::getaddrinfo(host.c_str(), portText.str().c_str(), &hints, &resolved);
            if (rc != 0 || resolved == nullptr)
            {
                out << "FAIL getaddrinfo rc=" << rc << " wsa=" << ::WSAGetLastError() << "\n";
                break;
            }
            out << "[ok] getaddrinfo " << (NowMs() - tDns) << "ms family="
                << (resolved->ai_family == AF_INET6 ? "IPv6" : "IPv4") << "\n";

            sock = ::socket(resolved->ai_family, resolved->ai_socktype, resolved->ai_protocol);
            if (sock == INVALID_SOCKET)
            {
                out << "FAIL socket wsa=" << ::WSAGetLastError() << "\n";
                break;
            }

            long long tConn = NowMs();
            if (::connect(sock, resolved->ai_addr, (int)resolved->ai_addrlen) != 0)
            {
                out << "FAIL connect wsa=" << ::WSAGetLastError()
                    << "（10013=权限/能力缺失，10060=超时，10061=拒绝）\n";
                break;
            }
            out << "[ok] connect " << (NowMs() - tConn) << "ms\n";

            session = libssh2_session_init();
            if (session == nullptr) { out << "FAIL libssh2_session_init\n"; break; }
            libssh2_session_set_blocking(session, 1);

            long long tHs = NowMs();
            rc = libssh2_session_handshake(session, (libssh2_socket_t)sock);
            if (rc != 0)
            {
                out << "FAIL handshake " << LastSshError(session, rc) << "\n";
                break;
            }
            out << "[ok] handshake " << (NowMs() - tHs) << "ms\n";
            AppendMethods(out, session);

            const char* fingerprint = libssh2_hostkey_hash(session, LIBSSH2_HOSTKEY_HASH_SHA256);
            if (fingerprint != nullptr)
            {
                out << "  hostkey-sha256(hex) ";
                for (int i = 0; i < 32; ++i)
                {
                    char buf[4];
                    sprintf_s(buf, sizeof(buf), "%02x", (unsigned char)fingerprint[i]);
                    out << buf;
                }
                out << "\n";
            }

            long long tAuth = NowMs();
            rc = libssh2_userauth_password(session, user.c_str(), password.c_str());
            if (rc != 0)
            {
                out << "FAIL userauth_password " << LastSshError(session, rc) << "\n";
                break;
            }
            out << "[ok] userauth_password " << (NowMs() - tAuth) << "ms\n";

            channel = libssh2_channel_open_session(session);
            if (channel == nullptr)
            {
                out << "FAIL channel_open " << LastSshError(session, -1) << "\n";
                break;
            }
            out << "[ok] channel_open\n";

            rc = libssh2_channel_exec(channel, command.c_str());
            if (rc != 0)
            {
                out << "FAIL channel_exec " << LastSshError(session, rc) << "\n";
                break;
            }
            out << "[ok] channel_exec \"" << command << "\"\n";

            std::string stdoutText;
            char buffer[1024];
            for (;;)
            {
                ssize_t got = libssh2_channel_read(channel, buffer, sizeof(buffer));
                if (got > 0) { stdoutText.append(buffer, (size_t)got); }
                else { break; }
            }
            int exitCode = libssh2_channel_get_exit_status(channel);
            out << "[ok] stdout " << stdoutText.size() << " 字节，exit=" << exitCode << "\n";
            out << "---- stdout ----\n" << stdoutText;
            if (!stdoutText.empty() && stdoutText[stdoutText.size() - 1] != '\n') { out << "\n"; }
            out << "----------------\n";
        } while (false);

        if (channel != nullptr) { libssh2_channel_free(channel); }
        if (session != nullptr)
        {
            libssh2_session_disconnect(session, "spike done");
            libssh2_session_free(session);
        }
        if (resolved != nullptr) { ::freeaddrinfo(resolved); }
        if (sock != INVALID_SOCKET) { ::closesocket(sock); }
        libssh2_exit();
        ::WSACleanup();

        out << "总耗时 " << (NowMs() - t0) << "ms\n";
        return out.str();
    }
}

String^ SshSpike::Version()
{
    return ToPlatform(std::string(libssh2_version(0) ? libssh2_version(0) : "(null)"));
}

IAsyncOperation<String^>^ SshSpike::ExecAsync(String^ host, int port, String^ user,
                                              String^ password, String^ command)
{
    std::string h = ToUtf8(host);
    std::string u = ToUtf8(user);
    std::string p = ToUtf8(password);
    std::string c = ToUtf8(command);
    int portValue = port;

    return create_async([h, portValue, u, p, c]() -> String^
    {
        // 阻塞式 socket 全程在后台线程（01-DESIGN §4.2：UI 线程禁止阻塞 IO）
        return ToPlatform(RunExec(h, portValue, u, p, c));
    });
}

String^ SshSpike::LoopbackUdpProbe()
{
    std::ostringstream out;
    WSADATA wsa;
    int rc = ::WSAStartup(MAKEWORD(2, 2), &wsa);
    if (rc != 0) { out << "FAIL WSAStartup rc=" << rc << "\n"; return ToPlatform(out.str()); }

    SOCKET a = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    SOCKET b = ::socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    do
    {
        if (a == INVALID_SOCKET || b == INVALID_SOCKET)
        {
            out << "FAIL socket wsa=" << ::WSAGetLastError() << "\n";
            break;
        }

        sockaddr_in addr;
        ZeroMemory(&addr, sizeof(addr));
        addr.sin_family = AF_INET;
        addr.sin_port = 0;                                   // 让系统分配端口
        addr.sin_addr.s_addr = ::htonl(INADDR_LOOPBACK);     // 127.0.0.1

        if (::bind(a, (sockaddr*)&addr, sizeof(addr)) != 0 ||
            ::bind(b, (sockaddr*)&addr, sizeof(addr)) != 0)
        {
            // UWP 的 AppContainer 默认禁止本机回环访问，这里很可能就是 10013
            out << "FAIL bind wsa=" << ::WSAGetLastError() << "（10013 = AppContainer 回环受限）\n";
            break;
        }

        sockaddr_in aAddr, bAddr;
        int len = sizeof(aAddr);
        if (::getsockname(a, (sockaddr*)&aAddr, &len) != 0) { out << "FAIL getsockname(a)\n"; break; }
        len = sizeof(bAddr);
        if (::getsockname(b, (sockaddr*)&bAddr, &len) != 0) { out << "FAIL getsockname(b)\n"; break; }
        out << "[ok] bind a=127.0.0.1:" << ::ntohs(aAddr.sin_port)
            << " b=127.0.0.1:" << ::ntohs(bAddr.sin_port) << "\n";

        char payload = 'W';
        if (::sendto(a, &payload, 1, 0, (sockaddr*)&bAddr, sizeof(bAddr)) != 1)
        {
            out << "FAIL sendto wsa=" << ::WSAGetLastError() << "\n";
            break;
        }

        // 只等 500ms：唤醒机制必须是立即可用的
        timeval tv;
        tv.tv_sec = 0;
        tv.tv_usec = 500 * 1000;
        fd_set readable;
        FD_ZERO(&readable);
        FD_SET(b, &readable);
        int ready = ::select(0, &readable, nullptr, nullptr, &tv);
        if (ready <= 0)
        {
            out << "FAIL select ready=" << ready << " wsa=" << ::WSAGetLastError() << "\n";
            break;
        }

        char received = 0;
        sockaddr_in from;
        int fromLen = sizeof(from);
        int got = ::recvfrom(b, &received, 1, 0, (sockaddr*)&from, &fromLen);
        out << (got == 1 && received == 'W'
                ? "[ok] 回环 UDP 唤醒可用（sendto → select → recvfrom 全通）\n"
                : "FAIL recvfrom\n");
    } while (false);

    if (a != INVALID_SOCKET) { ::closesocket(a); }
    if (b != INVALID_SOCKET) { ::closesocket(b); }
    ::WSACleanup();
    return ToPlatform(out.str());
}
