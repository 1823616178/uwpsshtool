#pragma once

// N09a：会话状态与 StateChanged 事件参数（01-DESIGN §6.2）。
// 与 core SshSessionState 的映射差异（已回写 §6.2）：
//   - core 的 Closing 是瞬态，不上抛；
//   - core 的 Closed 映射为 Disconnected，ErrorCode 带 lastError 的映射码
//    （本地主动 Close 时为 0）；core 的 Error 原样映射为 Error。

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            public enum class SessionState
            {
                Idle,
                Connecting,
                Handshaking,
                Authenticating,
                Established,
                Disconnected,
                Error,
            };

            public ref class StateChangedEventArgs sealed
            {
            public:
                property SessionState State { SessionState get(); }
                // 0 = 无错误；非 0 时数值与 C# SshErrorCode 完全一致（N08 契约）
                property int ErrorCode { int get(); }
                property Platform::String^ Detail { Platform::String^ get(); }

            internal:
                StateChangedEventArgs(SessionState state, int errorCode, Platform::String^ detail);

            private:
                SessionState state_;
                int errorCode_;
                Platform::String^ detail_;
            };
        }
    }
}
