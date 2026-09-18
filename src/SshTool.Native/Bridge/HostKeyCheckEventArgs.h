#pragma once

// N09a：HostKeyCheck 事件参数（01-DESIGN §6.2）。
// 事件在 I/O 线程触发；处理方可以就地 Accept/Reject，或 GetDeferral 后
// 转 UI 线程异步决定（deferral 挂起期间不计超时，最后一个 Complete 后
// 重新计一个完整窗口，见 native/core/bridge_logic/decision_gate.h）。
// 超时/取消 = Reject（fail-closed，错误码 303）。

#include "bridge_logic/decision_gate.h"

#include <memory>

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            ref class HostKeyInfo;

            public ref class HostKeyCheckEventArgs sealed
            {
            public:
                property HostKeyInfo^ Info { HostKeyInfo^ get(); }

                void Accept();
                void Reject();
                Windows::Foundation::Deferral^ GetDeferral();

            internal:
                HostKeyCheckEventArgs(
                    HostKeyInfo^ info,
                    std::shared_ptr<sshclient::bridge::DecisionGate<bool>> gate);

            private:
                HostKeyInfo^ info_;
                std::shared_ptr<sshclient::bridge::DecisionGate<bool>> gate_;
            };
        }
    }
}
