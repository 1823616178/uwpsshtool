using System;
using SshTool.Core.Common;
using SshTool.Core.Hosts;
using SshTool.Core.Mvvm;

namespace SshTool.Core.Sessions
{
    public enum SessionUiState
    {
        Connecting = 0,
        Authenticating = 1,
        Connected = 2,
        Reconnecting = 3,
        Disconnected = 4,
        Error = 5,
        Closed = 6
    }

    public sealed class SessionOpenRequest
    {
        public string HostId { get; set; }
        public QuickConnectTarget QuickConnect { get; set; }
        public int Cols { get; set; }
        public int Rows { get; set; }

        public SessionOpenRequest()
        {
            Cols = 80;
            Rows = 24;
        }
    }

    // 01-DESIGN.md §9.1：可观察会话；SessionId 重连不变。
    public sealed class SessionInfo : ObservableObject
    {
        private SessionUiState _state;
        private SshErrorCode _errorCode;
        private string _errorMessage = string.Empty;
        private int _reconnectAttempt;
        private int _reconnectInSeconds;
        private ISshSession _nativeSession;
        private string _title = string.Empty;

        public string SessionId { get; set; }
        public string HostId { get; set; }

        public string Title
        {
            get { return _title; }
            set { SetProperty(ref _title, value ?? string.Empty); }
        }

        public SessionUiState State
        {
            get { return _state; }
            set { SetProperty(ref _state, value); }
        }

        public SshErrorCode ErrorCode
        {
            get { return _errorCode; }
            set { SetProperty(ref _errorCode, value); }
        }

        public string ErrorMessage
        {
            get { return _errorMessage; }
            set { SetProperty(ref _errorMessage, value ?? string.Empty); }
        }

        public int ReconnectAttempt
        {
            get { return _reconnectAttempt; }
            set { SetProperty(ref _reconnectAttempt, value); }
        }

        public int ReconnectInSeconds
        {
            get { return _reconnectInSeconds; }
            set { SetProperty(ref _reconnectInSeconds, value); }
        }

        public ISshSession NativeSession
        {
            get { return _nativeSession; }
            set { SetProperty(ref _nativeSession, value); }
        }

        public DateTime? ConnectedAt { get; set; }
        public HostKeyInfo AcceptedKey { get; set; }
        public bool WriteKnownHost { get; set; }
        public int Cols { get; set; }
        public int Rows { get; set; }
        public bool UserClosed { get; set; }
        public string HostName { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
    }
}
