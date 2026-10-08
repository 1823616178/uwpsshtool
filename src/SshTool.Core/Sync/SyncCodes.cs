using System;

namespace SshTool.Core.Sync
{
    // fix/functional-pass（P2-2）：同步状态卡 / 操作失败的面向用户文案改由 App 按码查 resw
    // （Sync_Msg_<code> / Sync_Err_<code>）。Core 侧 Message 只保留诊断文本（日志 / 单测），
    // 不再直接显示给用户。
    public enum SyncMessageCode
    {
        None = 0,
        // 失败：详情见 SyncState.MessageError（ApiError 走 Api_<CODE>；SyncOperationException 走 Sync_Err_<code>）。
        Error,
        VaultDeletedLocalKept,
        LoginPasswordChanged,
        RemoteVaultDeleted,
        RemoteKeyRotated,
        RotateResultUnknown,
        SensitiveCleared,
        SyncPasswordChanged,
        ConflictInitialImport,
        ConflictRemoteDeletion,
        ConflictGeneric
    }

    public enum SyncErrorCode
    {
        Unknown = 0,
        SyncPasswordWrong,
        RecoveryKeyInvalid,
        RotateRequired,
        SignInRequired,
        AlreadySignedIn,
        RemoteRevisionBehind,
        VaultLocked,
        EncryptFailed,
        DecryptFailed,
        DocumentTooNew,
        NoPendingConflict,
        RotateOnlyDisables,
        VaultCreateFailed,
        UnexpectedKeyVersion,
        EnvelopeInvalid
    }

    // 面向用户的同步操作失败：继承 InvalidOperationException 保持既有 catch 语义；
    // Message 为诊断文本，App 按 Code 显示本地化文案。
    public sealed class SyncOperationException : InvalidOperationException
    {
        public SyncOperationException(SyncErrorCode code, string diagnostic)
            : base(diagnostic)
        {
            Code = code;
        }

        public SyncErrorCode Code { get; private set; }
    }
}
