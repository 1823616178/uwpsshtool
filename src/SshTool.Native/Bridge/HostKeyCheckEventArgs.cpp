#include "pch.h"
#include "Bridge/HostKeyCheckEventArgs.h"

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            HostKeyCheckEventArgs::HostKeyCheckEventArgs(
                HostKeyInfo^ info,
                std::shared_ptr<sshclient::bridge::DecisionGate<bool>> gate)
                : info_(info), gate_(std::move(gate))
            {
            }

            HostKeyInfo^ HostKeyCheckEventArgs::Info::get() { return info_; }

            void HostKeyCheckEventArgs::Accept() { gate_->submit(true); }

            void HostKeyCheckEventArgs::Reject() { gate_->submit(false); }

            Windows::Foundation::Deferral^ HostKeyCheckEventArgs::GetDeferral()
            {
                gate_->defer();
                auto gate = gate_;
                return ref new Windows::Foundation::Deferral(
                    ref new Windows::Foundation::DeferralCompletedHandler(
                        [gate]() { gate->completeDeferral(); }));
            }
        }
    }
}
