using System;
using SshTool.Core.Sessions;
using Xunit;

namespace SshTool.Core.Tests.Sessions
{
    public class SessionStateVisualsTests
    {
        [Theory]
        [InlineData(SessionUiState.Connected, SessionStateVisuals.SuccessBrushKey)]
        [InlineData(SessionUiState.Connecting, SessionStateVisuals.WarningBrushKey)]
        [InlineData(SessionUiState.Authenticating, SessionStateVisuals.WarningBrushKey)]
        [InlineData(SessionUiState.Reconnecting, SessionStateVisuals.WarningBrushKey)]
        [InlineData(SessionUiState.Error, SessionStateVisuals.DangerBrushKey)]
        [InlineData(SessionUiState.Disconnected, SessionStateVisuals.NeutralBrushKey)]
        [InlineData(SessionUiState.Closed, SessionStateVisuals.NeutralBrushKey)]
        public void MapsStateToSemanticBrush(SessionUiState state, string expected)
        {
            Assert.Equal(expected, SessionStateVisuals.BrushKey(state));
        }

        [Fact]
        public void EveryState_HasAKey()
        {
            foreach (SessionUiState state in Enum.GetValues(typeof(SessionUiState)))
            {
                Assert.False(string.IsNullOrEmpty(SessionStateVisuals.BrushKey(state)));
            }
        }
    }
}
