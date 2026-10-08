namespace SshTool.Core.Sessions
{
    // opt/full-pass 竖屏重设计：会话卡片状态色条/状态文字的语义画刷键（App 按当前主题解析）。
    // 颜色只表状态（05 §5.1 Semantic 角色），卡片上始终同时显示状态文字，不只靠颜色。
    // ui/fix-pass：会话卡片状态点形态（名称与 App 的 StatusDotState 一一对应，按名称转换）。
    public enum SessionDotKind
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting,
        Error
    }

    public static class SessionStateVisuals
    {
        public static SessionDotKind DotKind(SessionUiState state)
        {
            switch (state)
            {
                case SessionUiState.Connected:
                    return SessionDotKind.Connected;
                case SessionUiState.Connecting:
                case SessionUiState.Authenticating:
                    return SessionDotKind.Connecting;
                case SessionUiState.Reconnecting:
                    return SessionDotKind.Reconnecting;
                case SessionUiState.Error:
                    return SessionDotKind.Error;
                default:
                    return SessionDotKind.Disconnected;
            }
        }

        public const string SuccessBrushKey = "AppSuccessBrush";
        public const string WarningBrushKey = "AppWarningBrush";
        public const string DangerBrushKey = "AppDangerBrush";
        public const string NeutralBrushKey = "AppTextFaintBrush";

        public static string BrushKey(SessionUiState state)
        {
            switch (state)
            {
                case SessionUiState.Connected:
                    return SuccessBrushKey;
                case SessionUiState.Connecting:
                case SessionUiState.Authenticating:
                case SessionUiState.Reconnecting:
                    return WarningBrushKey;
                case SessionUiState.Error:
                    return DangerBrushKey;
                default:
                    return NeutralBrushKey;
            }
        }
    }
}
