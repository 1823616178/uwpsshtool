using System;
using SshTool.Core.Common;

namespace SshTool.Core.Sessions
{
    // N09b：StateChanged 事件参数。ErrorCode 为 None(0) 表示无错误；
    // 数值与 native error_codes.h / SshErrorCode 完全一致（N08 契约）。
    public sealed class SessionStateChangedEventArgs : EventArgs
    {
        public SessionStateChangedEventArgs(SessionStateKind state, SshErrorCode errorCode, string detail)
        {
            State = state;
            ErrorCode = errorCode;
            Detail = detail ?? string.Empty;
        }

        public SessionStateKind State { get; }
        public SshErrorCode ErrorCode { get; }
        public string Detail { get; }
    }
}
