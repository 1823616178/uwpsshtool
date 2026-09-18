#include "pch.h"
#include "Bridge/AuthPromptEventArgs.h"

#include "Bridge/BridgeUtil.h"

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            using Windows::Foundation::Collections::IVectorView;

            AuthPromptEventArgs::AuthPromptEventArgs(
                IVectorView<Platform::String^>^ prompts,
                IVectorView<bool>^ echo,
                std::function<void(bool, std::vector<std::string>)> onDecision)
                : prompts_(prompts), echo_(echo), onDecision_(std::move(onDecision))
            {
            }

            Platform::String^ AuthPromptEventArgs::Name::get() { return name_; }
            Platform::String^ AuthPromptEventArgs::Instruction::get() { return instruction_; }
            IVectorView<Platform::String^>^ AuthPromptEventArgs::Prompts::get() { return prompts_; }
            IVectorView<bool>^ AuthPromptEventArgs::Echo::get() { return echo_; }

            void AuthPromptEventArgs::Respond(IVectorView<Platform::String^>^ answers)
            {
                bool expected = false;
                if (!decided_.compare_exchange_strong(expected, true))
                {
                    return; // 二次答复忽略
                }
                onDecision_(true, ToUtf8Vector(answers));
            }

            void AuthPromptEventArgs::Cancel()
            {
                bool expected = false;
                if (!decided_.compare_exchange_strong(expected, true))
                {
                    return;
                }
                onDecision_(false, {});
            }

            Windows::Foundation::Deferral^ AuthPromptEventArgs::GetDeferral()
            {
                // 答复由 core 的 120 s 窗口兜底，Deferral 只是给 C# 处理方的
                // 异步编排语法，桥侧无额外记账。
                return ref new Windows::Foundation::Deferral(
                    ref new Windows::Foundation::DeferralCompletedHandler([]() {}));
            }
        }
    }
}
