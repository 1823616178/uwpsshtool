#include "pch.h"
#include "Bridge/StateChangedEventArgs.h"

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            StateChangedEventArgs::StateChangedEventArgs(SessionState state, int errorCode,
                                                         Platform::String^ detail)
                : state_(state), errorCode_(errorCode), detail_(detail)
            {
            }

            SessionState StateChangedEventArgs::State::get() { return state_; }
            int StateChangedEventArgs::ErrorCode::get() { return errorCode_; }
            Platform::String^ StateChangedEventArgs::Detail::get() { return detail_; }
        }
    }
}
