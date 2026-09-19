namespace SshTool.Core.Common
{
    // 01-DESIGN.md §6.3：数值必须与 native/core/ssh/error_codes.h 完全一致（N01 时建立对拍门禁）。
    public enum SshErrorCode
    {
        None = 0,

        // 1xx 连接与网络
        DnsResolutionFailed = 101,
        ConnectTimeout = 102,
        ConnectionRefused = 103,
        NetworkUnreachable = 104,

        // 2xx 认证
        AuthPasswordFailed = 201,
        AuthPublicKeyFailed = 202,
        AuthKeyboardInteractiveFailed = 203,
        PrivateKeyLoadFailed = 204,
        AuthTimeout = 205,
        NoLocalCredential = 206,

        // 3xx 协商与主机密钥
        AlgorithmNegotiationFailed = 301,
        UnknownHostKey = 302,
        HostKeyMismatch = 303,
        HandshakeFailed = 304,
        HandshakeTimeout = 305,

        // 4xx 会话
        RemoteClosed = 401,
        SessionTimeout = 402,
        SocketError = 403,
        KeepaliveTimeout = 404,
        PolicyDisconnect = 405,

        // 5xx 内部
        InternalError = 500,

        // 6xx SFTP（F01；01-DESIGN.md §6.3 定稿）
        SftpInitFailed = 601,
        SftpNoSuchFile = 602,
        SftpPermissionDenied = 603,
        SftpAlreadyExists = 604,
        SftpTransferFailed = 605,
        SftpCancelled = 606,

        Unknown = 999
    }
}
