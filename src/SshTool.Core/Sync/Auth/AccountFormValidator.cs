namespace SshTool.Core.Sync.Auth
{
    // fix/auth-audit：修改登录密码 / 注销账号两页的表单纯校验（与 LoginFormValidator 同构）。
    // 此前两页在字段为空时把提交按钮静默禁用（软键盘弹出时底部操作条还会被收起），
    // 点了没有任何反馈；现在按钮始终可点，由这里给出具体提示。返回 null 表示通过，否则为 resw 键。
    public static class AccountFormValidator
    {
        public const string CurrentPasswordRequiredKey = "ChangeLogin_CurrentRequired";
        public const string NewPasswordRequiredKey = "ChangeLogin_NewRequired";
        public const string NewPasswordTooShortKey = "ChangeLogin_TooShort";
        public const string PasswordMismatchKey = LoginFormValidator.PasswordMismatchKey;
        public const string DeletePasswordRequiredKey = "DeleteAccount_PasswordRequired";
        public const string DeleteConfirmRequiredKey = "DeleteAccount_ConfirmRequired";

        // 注销确认词（与服务端 DELETE /me 的 confirmation 相同；客户端发送的始终是这个常量）。
        public const string DeleteConfirmWord = "DELETE";

        public static readonly string[] AllErrorKeys = new string[]
        {
            CurrentPasswordRequiredKey,
            NewPasswordRequiredKey,
            NewPasswordTooShortKey,
            PasswordMismatchKey,
            DeletePasswordRequiredKey,
            DeleteConfirmRequiredKey,
        };

        public static string ValidateChangePassword(string currentPassword, string newPassword, string confirmPassword)
        {
            if (string.IsNullOrEmpty(currentPassword))
            {
                return CurrentPasswordRequiredKey;
            }
            if (string.IsNullOrEmpty(newPassword))
            {
                return NewPasswordRequiredKey;
            }
            if (newPassword.Length < LoginFormValidator.MinRegisterPasswordLength)
            {
                return NewPasswordTooShortKey;
            }
            if (!string.Equals(newPassword, confirmPassword, System.StringComparison.Ordinal))
            {
                return PasswordMismatchKey;
            }
            return null;
        }

        public static string ValidateDeleteAccount(string password, string confirmText)
        {
            if (string.IsNullOrEmpty(password))
            {
                return DeletePasswordRequiredKey;
            }
            if (!IsDeleteConfirmed(confirmText))
            {
                return DeleteConfirmRequiredKey;
            }
            return null;
        }

        // 去首尾空白后区分大小写比较：手机输入法选词上屏常会自动补一个尾随空格，
        // 此前「DELETE 」被判为不匹配，按钮一直灰着且没有提示。
        public static bool IsDeleteConfirmed(string confirmText)
        {
            return confirmText != null
                && string.Equals(confirmText.Trim(), DeleteConfirmWord, System.StringComparison.Ordinal);
        }
    }
}
