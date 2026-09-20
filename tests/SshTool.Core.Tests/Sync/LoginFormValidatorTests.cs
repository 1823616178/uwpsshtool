using SshTool.Core.Sync.Auth;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // U15：登录/注册表单校验（02-UI-DESIGN.md §5.13：注册密码 ≥10 位 + 确认 + 邀请码可选）。
    public class LoginFormValidatorTests
    {
        [Theory]
        [InlineData(null, "secret")]
        [InlineData("", "secret")]
        [InlineData("   ", "secret")]
        public void ValidateLogin_EmptyEmail_RequiresEmail(string email, string password)
        {
            Assert.Equal(LoginFormValidator.EmailRequiredKey, LoginFormValidator.ValidateLogin(email, password));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ValidateLogin_EmptyPassword_RequiresPassword(string password)
        {
            Assert.Equal(LoginFormValidator.PasswordRequiredKey, LoginFormValidator.ValidateLogin("a@b.c", password));
        }

        [Fact]
        public void ValidateLogin_Valid_ReturnsNull()
        {
            Assert.Null(LoginFormValidator.ValidateLogin("  a@b.c ", "secret"));
        }

        [Fact]
        public void ValidateRegister_EmptyEmail_RequiresEmail()
        {
            Assert.Equal(
                LoginFormValidator.EmailRequiredKey,
                LoginFormValidator.ValidateRegister(" ", "0123456789", "0123456789"));
        }

        [Fact]
        public void ValidateRegister_EmptyPassword_RequiresPassword()
        {
            Assert.Equal(
                LoginFormValidator.PasswordRequiredKey,
                LoginFormValidator.ValidateRegister("a@b.c", string.Empty, string.Empty));
        }

        [Theory]
        [InlineData("123456789")]   // 9 位
        [InlineData("")]
        public void ValidateRegister_ShortPassword_TooShort(string password)
        {
            // 空串先被 PasswordRequired 拦截；此处只断言 9 位。
            if (password.Length == 0)
            {
                Assert.Equal(
                    LoginFormValidator.PasswordRequiredKey,
                    LoginFormValidator.ValidateRegister("a@b.c", password, password));
            }
            else
            {
                Assert.Equal(
                    LoginFormValidator.PasswordTooShortKey,
                    LoginFormValidator.ValidateRegister("a@b.c", password, password));
            }
        }

        [Fact]
        public void ValidateRegister_TenCharPassword_PassesLength()
        {
            Assert.Null(LoginFormValidator.ValidateRegister("a@b.c", "0123456789", "0123456789"));
        }

        [Fact]
        public void ValidateRegister_Mismatch_ReturnsMismatch()
        {
            Assert.Equal(
                LoginFormValidator.PasswordMismatchKey,
                LoginFormValidator.ValidateRegister("a@b.c", "0123456789", "0123456788"));
        }

        [Fact]
        public void ValidateRegister_ConfirmNull_ReturnsMismatch()
        {
            Assert.Equal(
                LoginFormValidator.PasswordMismatchKey,
                LoginFormValidator.ValidateRegister("a@b.c", "0123456789", null));
        }

        [Fact]
        public void MinRegisterPasswordLength_IsTen()
        {
            Assert.Equal(10, LoginFormValidator.MinRegisterPasswordLength);
        }
    }
}
