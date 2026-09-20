#pragma once

// F05：隧道转发 WinRT 桥（01-DESIGN.md §11.2；04-TASKS F05）。
//
// 分层：本类是薄句柄——core LocalListener/RemoteListener 与共享统计由所属
// SshSession 的转发挂载持有（Bridge/SshSession.h 的 Fwd* 挂载点），与 core
// SshSession 同寿命；Shutdown 先停监听再释会话，owner 先 Dispose 后本对象
// 再调用只会 fail-fast，不会悬空。
//
// 一条隧道一个实例：StartAsync 只受理一次（会话 Established 前提下挂载唯一
// 监听）；隧道重连由上层换新会话 + 新 Forwarder。线程：StartAsync 经
// concurrency::create_async 在后台线程等待 loop 线程的监听结果（绝不阻塞会话
// I/O 线程）；Stop 幂等且快速返回（只投递，拆除在 Shutdown 有界完成）。
// 失败语义：Code 为 N08 统一错误码（0 = 成功），Message 为诊断文本（端口/
// 目标地址非凭据，可记录）。

#include <atomic>

#include "Bridge/SshSession.h"

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            // 与 Core.TunnelType 对应（0=local 1=remote 2=dynamic）；relay 不入
            // native（Core TunnelManager 直接拒绝启动）。
            public enum class ForwardKind
            {
                Local = 0,
                Remote = 1,
                Dynamic = 2
            };

            // StartAsync 结果（Code 0 = 成功，BoundPort 为实际监听端口）。
            public ref class ForwardStartResult sealed
            {
            public:
                property int Code { int get(); }
                property Platform::String^ Message { Platform::String^ get(); }
                property int BoundPort { int get(); }

            internal:
                ForwardStartResult(int code, Platform::String^ message, int boundPort);

            private:
                int code_;
                Platform::String^ message_;
                int boundPort_;
            };

            // 只读统计快照（native fwd TunnelStatsSnapshot 同名字段；方向：
            // BytesUp = socket→通道（本机→隧道），BytesDown = 通道→socket）。
            public ref class ForwardStatsSnapshot sealed
            {
            public:
                property uint64 ActiveConnections { uint64 get(); }
                property uint64 TotalConnections { uint64 get(); }
                property uint64 BytesUp { uint64 get(); }
                property uint64 BytesDown { uint64 get(); }

            internal:
                ForwardStatsSnapshot(uint64 active, uint64 total, uint64 up, uint64 down);

            private:
                uint64 active_;
                uint64 total_;
                uint64 up_;
                uint64 down_;
            };

            public ref class Forwarder sealed
            {
            public:
                explicit Forwarder(SshSession^ owner);
                virtual ~Forwarder(); // C++/CX：public 析构必须 virtual（Dispose）

                property bool IsRunning { bool get(); }

                // 启动监听：local = listenHost:listenPort → destHost:destPort；
                // remote = 服务器 bindHost:bindPort → 本机 destHost:destPort；
                // dynamic = listenHost:listenPort 上的 SOCKS5 代理（destHost/destPort
                // 忽略）。仅 Established 会话受理；成功后 IsRunning = true。
                Windows::Foundation::IAsyncOperation<ForwardStartResult^>^ StartAsync(
                    ForwardKind kind, Platform::String^ listenHost, int listenPort,
                    Platform::String^ destHost, int destPort);
                void Stop();
                ForwardStatsSnapshot^ SnapshotStats();

            private:
                SshSession^ owner_;
                // StartAsync 一次性闸门（已启动即拒绝再次受理，见 cpp）。
                std::atomic<bool> started_{false};
            };
        }
    }
}
