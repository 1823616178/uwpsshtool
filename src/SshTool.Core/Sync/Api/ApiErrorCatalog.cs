using System;
using System.Collections.Generic;

namespace SshTool.Core.Sync.Api
{
    // U15：03-SYNC-PROTOCOL.md §2.3「需要处理的业务码」+ 客户端自产码的唯一清单。
    // 登录/注册页按 Api_<CODE> 查 resw 文案（02-UI-DESIGN.md §5.13、§7）；
    // resw 覆盖情况由 Core 单测 ApiErrorCatalogTests 钉住（两份 resw 全码覆盖）。
    public static class ApiErrorCatalog
    {
        public const string ResourcePrefix = "Api_";

        // §2.3 业务码（服务端 data.code）。
        public const string ValidationError = "VALIDATION_ERROR";
        public const string RegistrationDisabled = "REGISTRATION_DISABLED";
        public const string AuthInvitationRequired = "AUTH_INVITATION_REQUIRED";
        public const string AuthInvitationInvalid = "AUTH_INVITATION_INVALID";
        public const string AuthEmailExists = "AUTH_EMAIL_EXISTS";
        public const string AuthInvalidCredentials = "AUTH_INVALID_CREDENTIALS";
        public const string DeviceQuotaExceeded = "DEVICE_QUOTA_EXCEEDED";
        public const string AuthTokenExpired = "AUTH_TOKEN_EXPIRED";
        public const string AuthTokenReused = "AUTH_TOKEN_REUSED";
        public const string AuthDeviceRevoked = "AUTH_DEVICE_REVOKED";
        public const string AuthCurrentPasswordInvalid = "AUTH_CURRENT_PASSWORD_INVALID";
        public const string AuthPasswordUnchanged = "AUTH_PASSWORD_UNCHANGED";
        public const string AccountDeleteConflict = "ACCOUNT_DELETE_CONFLICT";
        public const string DeviceNotFound = "DEVICE_NOT_FOUND";
        public const string DeviceCurrent = "DEVICE_CURRENT";
        public const string VaultExists = "VAULT_EXISTS";
        public const string VaultNotFound = "VAULT_NOT_FOUND";
        public const string VaultKeyVersionMismatch = "VAULT_KEY_VERSION_MISMATCH";
        public const string SyncDocumentNotFound = "SYNC_DOCUMENT_NOT_FOUND";
        public const string SyncDocumentInvalid = "SYNC_DOCUMENT_INVALID";
        public const string SyncDocumentTooLarge = "SYNC_DOCUMENT_TOO_LARGE";
        public const string SyncRevisionConflict = "SYNC_REVISION_CONFLICT";
        public const string SyncRevisionLimitReached = "SYNC_REVISION_LIMIT_REACHED";
        public const string SyncRevisionRequired = "SYNC_REVISION_REQUIRED";
        public const string SyncRevisionNotFound = "SYNC_REVISION_NOT_FOUND";
        public const string IdempotencyKeyRequired = "IDEMPOTENCY_KEY_REQUIRED";
        public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";
        public const string RateLimited = "RATE_LIMITED";
        public const string MaintenanceMode = "MAINTENANCE_MODE";

        public static readonly IReadOnlyList<string> AllCodes = new string[]
        {
            ValidationError,
            RegistrationDisabled,
            AuthInvitationRequired,
            AuthInvitationInvalid,
            AuthEmailExists,
            AuthInvalidCredentials,
            DeviceQuotaExceeded,
            AuthTokenExpired,
            AuthTokenReused,
            AuthDeviceRevoked,
            AuthCurrentPasswordInvalid,
            AuthPasswordUnchanged,
            AccountDeleteConflict,
            DeviceNotFound,
            DeviceCurrent,
            VaultExists,
            VaultNotFound,
            VaultKeyVersionMismatch,
            SyncDocumentNotFound,
            SyncDocumentInvalid,
            SyncDocumentTooLarge,
            SyncRevisionConflict,
            SyncRevisionLimitReached,
            SyncRevisionRequired,
            SyncRevisionNotFound,
            IdempotencyKeyRequired,
            IdempotencyKeyReused,
            RateLimited,
            MaintenanceMode,
            ApiError.CodeAuthRequired,
            ApiError.CodeAuthRefreshUnavailable,
            ApiError.CodeNetworkError,
            ApiError.CodeRequestTimeout,
            ApiError.CodeResponseInvalid,
            ApiError.CodeSyncRevisionInvalid,
            ApiError.CodeSyncMetadataInvalid,
        };

        // resw 键名：Api_<CODE>。CodeUnknown（如 HTTP_<status> 推断码）无对应键，
        // 调用方应回退到 ApiError.Message（服务端原文）。
        public static string ResourceKey(string code)
        {
            return ResourcePrefix + code;
        }

        public static bool IsKnown(string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                return false;
            }
            for (int i = 0; i < AllCodes.Count; i++)
            {
                if (string.Equals(AllCodes[i], code, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
