using System;
using SshTool.Core.Sessions;
using Xunit;

namespace SshTool.Core.Tests.Sessions
{
    public class SessionStateVisualsTests
    {
        private static string AppDir()
        {
            var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = System.IO.Path.Combine(dir.FullName, "src", "SshTool.App");
                if (System.IO.Directory.Exists(candidate))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
            throw new System.IO.DirectoryNotFoundException("src/SshTool.App");
        }

        [Theory]
        [InlineData(SessionUiState.Connected, SessionDotKind.Connected)]
        [InlineData(SessionUiState.Connecting, SessionDotKind.Connecting)]
        [InlineData(SessionUiState.Authenticating, SessionDotKind.Connecting)]
        [InlineData(SessionUiState.Reconnecting, SessionDotKind.Reconnecting)]
        [InlineData(SessionUiState.Error, SessionDotKind.Error)]
        [InlineData(SessionUiState.Disconnected, SessionDotKind.Disconnected)]
        [InlineData(SessionUiState.Closed, SessionDotKind.Disconnected)]
        public void DotKind_MapsEveryState(SessionUiState state, SessionDotKind expected)
        {
            Assert.Equal(expected, SessionStateVisuals.DotKind(state));
        }

        // App 端按名称把 SessionDotKind 转成 StatusDotState（Controls/StatusDot.xaml.cs），两边名字必须一致。
        [Fact]
        public void DotKindNames_MatchAppStatusDotStates()
        {
            string path = System.IO.Path.Combine(AppDir(), "Controls", "StatusDot.xaml.cs");
            string source = System.IO.File.ReadAllText(path);
            foreach (string name in System.Enum.GetNames(typeof(SessionDotKind)))
            {
                Assert.Matches(@"enum StatusDotState\s*\{[^}]*\b" + name + @"\b", source);
            }
        }

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
