using SshTool.Core.Common;

namespace SshTool.Core.Sessions
{
    public enum OverlayKind
    {
        None = 0,
        Connecting = 1,
        Reconnecting = 2,
        Error = 3,
        Closed = 4,
        PolicyDisconnected = 5
    }

    public sealed class OverlayModel
    {
        public OverlayKind Kind { get; set; }
        public string MessageKey { get; set; }
        public bool ShowCancel { get; set; }
        public bool ShowReconnectNow { get; set; }
        public bool ShowStop { get; set; }
        public bool ShowRetry { get; set; }
        public bool ShowEditHost { get; set; }
        public bool ShowClose { get; set; }
        public int ReconnectInSeconds { get; set; }
        public int ReconnectAttempt { get; set; }
        public SshErrorCode ErrorCode { get; set; }
    }

    // 02-UI-DESIGN.md §5.5 蒙层表。
    public static class OverlayStateDeriver
    {
        public static OverlayModel Derive(SessionInfo info)
        {
            var model = new OverlayModel { Kind = OverlayKind.None, MessageKey = string.Empty };
            if (info == null)
            {
                return model;
            }
            model.ReconnectInSeconds = info.ReconnectInSeconds;
            model.ReconnectAttempt = info.ReconnectAttempt;
            model.ErrorCode = info.ErrorCode;
            switch (info.State)
            {
                case SessionUiState.Connecting:
                case SessionUiState.Authenticating:
                    model.Kind = OverlayKind.Connecting;
                    model.MessageKey = "Overlay_Connecting";
                    model.ShowCancel = true;
                    break;
                case SessionUiState.Reconnecting:
                    model.Kind = OverlayKind.Reconnecting;
                    model.MessageKey = "Overlay_Reconnecting";
                    model.ShowReconnectNow = true;
                    model.ShowStop = true;
                    break;
                case SessionUiState.Error:
                    if (info.ErrorCode == SshErrorCode.PolicyDisconnect)
                    {
                        model.Kind = OverlayKind.PolicyDisconnected;
                        model.MessageKey = "Overlay_PolicyDisconnected";
                        model.ShowRetry = true;
                    }
                    else
                    {
                        model.Kind = OverlayKind.Error;
                        model.MessageKey = "Error_" + ((int)info.ErrorCode).ToString();
                        model.ShowRetry = true;
                        model.ShowEditHost = !string.IsNullOrEmpty(info.HostId);
                        model.ShowClose = true;
                    }
                    break;
                case SessionUiState.Disconnected:
                    model.Kind = OverlayKind.Closed;
                    model.MessageKey = "Overlay_Closed";
                    model.ShowRetry = true;
                    model.ShowClose = true;
                    break;
                case SessionUiState.Closed:
                    model.Kind = OverlayKind.Closed;
                    model.MessageKey = "Overlay_Closed";
                    model.ShowClose = true;
                    break;
                default:
                    model.Kind = OverlayKind.None;
                    break;
            }
            return model;
        }
    }
}
