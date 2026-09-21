using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SshTool.Core.Sync.Vault;
using Xunit;

namespace SshTool.Core.Tests.Sync.Vault
{
    // U16：建库/解锁表单校验（02-UI-DESIGN.md §5.13 VaultSetupPage）。
    // 同步密码 ≥8 位（客户端规则，区别于注册密码 ≥10）；确认一致；解锁密码段只判空。
    public class VaultFormValidatorTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ValidateSetup_EmptyPassword_Requires(string password)
        {
            Assert.Equal(
                VaultFormValidator.SyncPasswordRequiredKey,
                VaultFormValidator.ValidateSetup(password, password));
        }

        [Theory]
        [InlineData("1234567")]        // 7 位
        [InlineData("123456")]         // 6 位
        public void ValidateSetup_ShortPassword_TooShort(string password)
        {
            Assert.Equal(
                VaultFormValidator.SyncPasswordTooShortKey,
                VaultFormValidator.ValidateSetup(password, password));
        }

        [Fact]
        public void ValidateSetup_EightCharPassword_Passes()
        {
            Assert.Null(VaultFormValidator.ValidateSetup("12345678", "12345678"));
        }

        [Fact]
        public void ValidateSetup_Mismatch_ReturnsMismatch()
        {
            Assert.Equal(
                VaultFormValidator.SyncPasswordMismatchKey,
                VaultFormValidator.ValidateSetup("12345678", "12345679"));
        }

        [Fact]
        public void ValidateSetup_ConfirmNull_ReturnsMismatch()
        {
            Assert.Equal(
                VaultFormValidator.SyncPasswordMismatchKey,
                VaultFormValidator.ValidateSetup("12345678", null));
        }

        [Fact]
        public void ValidateUnlockPassword_Empty_Requires()
        {
            Assert.Equal(VaultFormValidator.SyncPasswordRequiredKey, VaultFormValidator.ValidateUnlockPassword(null));
            Assert.Equal(VaultFormValidator.SyncPasswordRequiredKey, VaultFormValidator.ValidateUnlockPassword(string.Empty));
        }

        [Fact]
        public void ValidateUnlockPassword_NonEmpty_Passes()
        {
            Assert.Null(VaultFormValidator.ValidateUnlockPassword("x"));
        }

        [Fact]
        public void MinSyncPasswordLength_IsEight()
        {
            // 04-TASKS.md U16 / 02-UI-DESIGN.md §5.13：同步密码至少 8 位（区别于注册密码 10 位）。
            Assert.Equal(8, VaultFormValidator.MinSyncPasswordLength);
        }

        [Fact]
        public void AllErrorKeys_AreTheThreeDocumentedKeys()
        {
            Assert.Equal(
                new string[]
                {
                    "Vault_SyncPasswordRequired",
                    "Vault_SyncPasswordTooShort",
                    "Vault_SyncPasswordMismatch",
                },
                VaultFormValidator.AllErrorKeys);
        }

        // resw 键覆盖（U15 ApiErrorCatalogTests 同构）：两份 resw 都必须有全部 Vault_* 文案键
        //（表单校验 + 恢复密钥 + 页面/对话框代码加载的固定键）。
        private static readonly string[] VaultReswKeys = new string[]
        {
            VaultFormValidator.SyncPasswordRequiredKey,
            VaultFormValidator.SyncPasswordTooShortKey,
            VaultFormValidator.SyncPasswordMismatchKey,
            RecoveryKeyInput.FormatInvalidKey,
            RecoveryKeyInput.ChecksumFailedKey,
            "Vault_SyncUnavailable",
            "Vault_RecoveryRequired",
            "Vault_WorkingSetup",
            "Vault_WorkingUnlock",
            "Vault_WorkingSync",
        };

        public static IEnumerable<object[]> ReswFiles()
        {
            yield return new object[] { "zh-cn" };
            yield return new object[] { "en-us" };
        }

        [Theory]
        [MemberData(nameof(ReswFiles))]
        public void VaultKeys_HaveReswEntries(string lang)
        {
            var names = LoadResw(lang);
            foreach (string key in VaultReswKeys)
            {
                Assert.True(names.ContainsKey(key), lang + " 缺少 " + key);
                Assert.False(string.IsNullOrWhiteSpace(names[key]), lang + " 的 " + key + " 文案为空");
            }
        }

        private static Dictionary<string, string> LoadResw(string lang)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "src", "SshTool.App", "Strings", lang, "Resources.resw");
                if (File.Exists(candidate))
                {
                    var doc = XDocument.Load(candidate);
                    return doc.Root.Elements("data")
                        .Where(e => e.Attribute("name") != null)
                        .ToDictionary(e => e.Attribute("name").Value, e => (string)e.Element("value"));
                }
                dir = dir.Parent;
            }
            throw new FileNotFoundException("找不到 " + lang + " Resources.resw");
        }
    }
}
