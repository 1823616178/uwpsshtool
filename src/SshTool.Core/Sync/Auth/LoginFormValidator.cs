namespace SshTool.Core.Sync.Auth
{
    // U15 登录/注册表单纯校验（02-UI-DESIGN.md §5.13）：
    // 登录只判空；注册密码 ≥10 位（服务端规则）+ 确认一致；邀请码可选（为空也合法，
    // 未填写且服务端要求时由 AUTH_INVITATION_REQUIRED 错误码回显）。
    // 返回 null 表示通过，否则为 resw 错误文案键（Login_*，中英由单测钉住存在性）。
    // 邮箱只做去空白判空，不做格式校验（格式由服务端 VALIDATION_ERROR 判定）。
    public static class LoginFormValidator
    {
        public const int MinRegisterPasswordLength = 10;

        public const string EmailRequiredKey = "Login_EmailRequired";
        public const string PasswordRequiredKey = "Login_PasswordRequired";
        public const string PasswordTooShortKey = "Login_PasswordTooShort";
        public const string PasswordMismatchKey = "Login_PasswordMismatch";

        public static readonly string[] AllErrorKeys = new string[]
        {
            EmailRequiredKey,
            PasswordRequiredKey,
            PasswordTooShortKey,
            PasswordMismatchKey,
        };

        public static string ValidateLogin(string email, string password)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return EmailRequiredKey;
            }
            if (string.IsNullOrEmpty(password))
            {
                return PasswordRequiredKey;
            }
            return null;
        }

        public static string ValidateRegister(string email, string password, string confirmPassword)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return EmailRequiredKey;
            }
            if (string.IsNullOrEmpty(password))
            {
                return PasswordRequiredKey;
            }
            if (password.Length < MinRegisterPasswordLength)
            {
                return PasswordTooShortKey;
            }
            if (!string.Equals(password, confirmPassword))
            {
                return PasswordMismatchKey;
            }
            return null;
        }
    }
}
