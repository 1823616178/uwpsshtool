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

        // F03：SFTP 等无终端用途置 false——连接 + 认证后不开 shell、不发 AutoRun
        //（01-DESIGN.md §11.1：SFTP 会话建议独占一条连接；避免 tmux 附着/初始命令副作用）。
        public bool OpenShell { get; set; }

        public SessionOpenRequest()
        {
            Cols = 80;
            Rows = 24;
            OpenShell = true;
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

        // F03：本会话是否已开 shell（SessionOpenRequest.OpenShell 的落地标记；
        // 重连保持原样）。终端侧（TerminalViewModel/SessionsPane）只复用 true 的会话，
        // 免得把无 shell 的 SFTP 连接附到终端上得到黑屏。
        public bool ShellOpened { get; set; }

        // P01：被后台策略挂起（405 策略性断开，面孔与窗格绑定保留，回前台原地重连）。
        // 与 UserClosed 一样是普通字段（State/ErrorMessage 的可观察通知照常走）。
        public bool PolicySuspended { get; set; }

        // P01：Suspending 时标记「挂起前在线」（Connected/Reconnecting），供
        // Resuming 时区分「已不可用→重连 / 仍 Established→ProbeNow」（01-DESIGN §10）。
        // 纯内存标记，不进 sessions.json（冷启动走 U09 的恢复卡片）。
        public bool WasOnlineBeforeSuspend { get; set; }
        public string HostName { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
    }
}
