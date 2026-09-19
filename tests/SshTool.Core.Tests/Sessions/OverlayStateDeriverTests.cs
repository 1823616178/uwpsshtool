using SshTool.Core.Common;
using SshTool.Core.Sessions;
using Xunit;

namespace SshTool.Core.Tests.Sessions
{
    public class OverlayStateDeriverTests
    {
        private static OverlayModel D(SessionUiState state, SshErrorCode code = SshErrorCode.None)
        {
            return OverlayStateDeriver.Derive(new SessionInfo { State = state, ErrorCode = code, HostId = "h" });
        }

        [Fact]
        public void Null_None()
        {
            Assert.Equal(OverlayKind.None, OverlayStateDeriver.Derive(null).Kind);
        }

        [Fact]
        public void Connected_None()
        {
            Assert.Equal(OverlayKind.None, D(SessionUiState.Connected).Kind);
        }

        [Fact]
        public void Connecting_Cancel()
        {
            OverlayModel m = D(SessionUiState.Connecting);
            Assert.Equal(OverlayKind.Connecting, m.Kind);
            Assert.True(m.ShowCancel);
        }

        [Fact]
        public void Authenticating_ConnectingKind()
        {
            Assert.Equal(OverlayKind.Connecting, D(SessionUiState.Authenticating).Kind);
        }

        [Fact]
        public void Reconnecting_Buttons()
        {
            OverlayModel m = D(SessionUiState.Reconnecting);
            Assert.True(m.ShowReconnectNow);
            Assert.True(m.ShowStop);
        }

        [Fact]
        public void Error_ShowsRetryEditClose()
        {
            OverlayModel m = D(SessionUiState.Error, SshErrorCode.AuthPasswordFailed);
            Assert.Equal(OverlayKind.Error, m.Kind);
            Assert.Equal("Error_201", m.MessageKey);
            Assert.True(m.ShowRetry);
            Assert.True(m.ShowEditHost);
            Assert.True(m.ShowClose);
        }

        [Fact]
        public void PolicyDisconnect_Kind()
        {
            OverlayModel m = D(SessionUiState.Error, SshErrorCode.PolicyDisconnect);
            Assert.Equal(OverlayKind.PolicyDisconnected, m.Kind);
            Assert.True(m.ShowRetry);
        }

        [Fact]
        public void Disconnected_ClosedKind()
        {
            OverlayModel m = D(SessionUiState.Disconnected);
            Assert.Equal(OverlayKind.Closed, m.Kind);
            Assert.True(m.ShowRetry);
        }

        [Fact]
        public void Closed_CloseOnly()
        {
            OverlayModel m = D(SessionUiState.Closed);
            Assert.Equal(OverlayKind.Closed, m.Kind);
            Assert.True(m.ShowClose);
            Assert.False(m.ShowRetry);
        }
    }
}
