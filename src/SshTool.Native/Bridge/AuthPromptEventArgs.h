#pragma once

// N09a：AuthPrompt 事件参数（keyboard-interactive，01-DESIGN §6.2）。
// 事件在 I/O 线程触发；处理方就地 Respond/Cancel，或 GetDeferral 后转 UI
// 线程异步答复（答复是任意线程安全的）。未答复的兜底由 core 的
// authPromptTimeoutMs（默认 120 s）承担：到期按空答复处理（服务器拒绝
// 本轮）。注意 core 的等待窗口不因 Deferral 暂停——与 HostKeyCheck 的
// DecisionGate 语义不同，120 s 是硬上限（已回写 §6.2）。
//
// 注意：core 的 IAuthPromptSink 目前只上抛 prompts 本体，Name/Instruction
// 暂为空串（N05 接口未带这两字段；02-UI-DESIGN 的对话框只展示 prompts）。

#include <atomic>
#include <functional>
#include <string>
#include <vector>

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            public ref class AuthPromptEventArgs sealed
            {
            public:
                property Platform::String^ Name { Platform::String^ get(); }
                property Platform::String^ Instruction { Platform::String^ get(); }
                property Windows::Foundation::Collections::IVectorView<Platform::String^>^ Prompts
                {
                    Windows::Foundation::Collections::IVectorView<Platform::String^>^ get();
                }
                property Windows::Foundation::Collections::IVectorView<bool>^ Echo
                {
                    Windows::Foundation::Collections::IVectorView<bool>^ get();
                }

                // 答复（任意线程）：answers 长度应对齐 Prompts。二次调用忽略。
                void Respond(Windows::Foundation::Collections::IVectorView<Platform::String^>^ answers);
                // 取消（任意线程）：等价于空答复（服务器会拒绝本轮）。
                void Cancel();
                Windows::Foundation::Deferral^ GetDeferral();

            internal:
                // onDecision(false=取消/空答复, true=带 answers)，任意线程调用安全。
                AuthPromptEventArgs(
                    Windows::Foundation::Collections::IVectorView<Platform::String^>^ prompts,
                    Windows::Foundation::Collections::IVectorView<bool>^ echo,
                    std::function<void(bool answered, std::vector<std::string> answers)> onDecision);

            private:
                Platform::String^ name_ = ref new Platform::String(L"");
                Platform::String^ instruction_ = ref new Platform::String(L"");
                Windows::Foundation::Collections::IVectorView<Platform::String^>^ prompts_;
                Windows::Foundation::Collections::IVectorView<bool>^ echo_;
                std::function<void(bool, std::vector<std::string>)> onDecision_;
                std::atomic<bool> decided_{false};
            };
        }
    }
}
