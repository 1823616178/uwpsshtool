#include "pch.h"
#include "Bridge/Forwarder.h"

#include "Bridge/BridgeUtil.h"
#include "fwd/pump.h"  // TunnelStatsSnapshot 完整类型（SshSession.h 只前置声明）
#include "ssh/error_codes.h"

using namespace concurrency;
using namespace Windows::Foundation;

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            ForwardStartResult::ForwardStartResult(int code, Platform::String^ message,
                                                   int boundPort)
                : code_(code), message_(message), boundPort_(boundPort)
            {
            }

            int ForwardStartResult::Code::get() { return code_; }
            Platform::String^ ForwardStartResult::Message::get() { return message_; }
            int ForwardStartResult::BoundPort::get() { return boundPort_; }

            ForwardStatsSnapshot::ForwardStatsSnapshot(uint64 active, uint64 total,
                                                       uint64 up, uint64 down)
                : active_(active), total_(total), up_(up), down_(down)
            {
            }

            uint64 ForwardStatsSnapshot::ActiveConnections::get() { return active_; }
            uint64 ForwardStatsSnapshot::TotalConnections::get() { return total_; }
            uint64 ForwardStatsSnapshot::BytesUp::get() { return up_; }
            uint64 ForwardStatsSnapshot::BytesDown::get() { return down_; }

            Forwarder::Forwarder(SshSession^ owner)
                : owner_(owner), started_(false)
            {
                if (owner == nullptr)
                {
                    throw ref new Platform::InvalidArgumentException(L"owner");
                }
            }

            Forwarder::~Forwarder() { Stop(); }

            bool Forwarder::IsRunning::get()
            {
                SshSession^ owner = owner_;
                return owner != nullptr && owner->FwdIsRunning();
            }

            IAsyncOperation<ForwardStartResult^>^ Forwarder::StartAsync(
                ForwardKind kind, Platform::String^ listenHost, int listenPort,
                Platform::String^ destHost, int destPort)
            {
                SshSession^ owner = owner_;
                if (owner == nullptr || started_.exchange(true))
                {
                    // 一次性对象：已启动过的句柄拒绝再次启动（Stop 仍可调用）。
                    return create_async([]
                    {
                        return ref new ForwardStartResult(
                            sshclient::ssh::kSshErrorCodeInternalError, L"转发句柄已失效", 0);
                    });
                }
                const std::string host = Bridge::ToUtf8(listenHost);
                const std::string dest = Bridge::ToUtf8(destHost);
                const int kindValue = static_cast<int>(kind);
                return create_async([owner, kindValue, host, listenPort, dest, destPort]()
                {
                    unsigned boundPort = 0;
                    std::string message;
                    const int code = owner->FwdStart(kindValue, host,
                                                     static_cast<unsigned>(listenPort),
                                                     dest, static_cast<unsigned>(destPort),
                                                     boundPort, message);
                    return ref new ForwardStartResult(code, Bridge::ToPlatform(message),
                                                      static_cast<int>(boundPort));
                });
            }

            void Forwarder::Stop()
            {
                // FwdStop 幂等；Stopped 后 IsRunning 变 false，Session 关闭时拆除。
                SshSession^ owner = owner_;
                if (owner != nullptr)
                {
                    owner->FwdStop();
                }
            }

            ForwardStatsSnapshot^ Forwarder::SnapshotStats()
            {
                SshSession^ owner = owner_;
                if (owner == nullptr)
                {
                    return ref new ForwardStatsSnapshot(0, 0, 0, 0);
                }
                sshclient::fwd::TunnelStatsSnapshot stats;
                if (!owner->FwdStats(stats))
                {
                    return ref new ForwardStatsSnapshot(0, 0, 0, 0);
                }
                return ref new ForwardStatsSnapshot(
                    stats.activeConnections, stats.totalConnections,
                    stats.bytesUp, stats.bytesDown);
            }
        }
    }
}
