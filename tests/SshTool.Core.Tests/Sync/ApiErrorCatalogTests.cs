using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SshTool.Core.Sync.Api;
using SshTool.Core.Sync.Auth;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // U15 验收：两份 resw 中 Api_<CODE> 覆盖 03-SYNC-PROTOCOL.md §2.3 全部码。
    // 码清单的唯一依据是 ApiErrorCatalog.AllCodes（29 业务码 + 7 客户端自产码）；
    // 本测试另断言清单本身与 §2.3 一致（逐字全集），防止实现与文档单边漂移。
    public class ApiErrorCatalogTests
    {
        // §2.3 原文逐字抄录（业务码 29 + 自产码 7）。与 AllCodes 不一致即失败。
        private static readonly string[] ExpectedCodes = new string[]
        {
            "VALIDATION_ERROR",
            "REGISTRATION_DISABLED",
            "AUTH_INVITATION_REQUIRED",
            "AUTH_INVITATION_INVALID",
            "AUTH_EMAIL_EXISTS",
            "AUTH_INVALID_CREDENTIALS",
            "DEVICE_QUOTA_EXCEEDED",
            "AUTH_TOKEN_EXPIRED",
            "AUTH_TOKEN_REUSED",
            "AUTH_DEVICE_REVOKED",
            "AUTH_CURRENT_PASSWORD_INVALID",
            "AUTH_PASSWORD_UNCHANGED",
            "ACCOUNT_DELETE_CONFLICT",
            "DEVICE_NOT_FOUND",
            "DEVICE_CURRENT",
            "VAULT_EXISTS",
            "VAULT_NOT_FOUND",
            "VAULT_KEY_VERSION_MISMATCH",
            "SYNC_DOCUMENT_NOT_FOUND",
            "SYNC_DOCUMENT_INVALID",
            "SYNC_DOCUMENT_TOO_LARGE",
            "SYNC_REVISION_CONFLICT",
            "SYNC_REVISION_LIMIT_REACHED",
            "SYNC_REVISION_REQUIRED",
            "SYNC_REVISION_NOT_FOUND",
            "IDEMPOTENCY_KEY_REQUIRED",
            "IDEMPOTENCY_KEY_REUSED",
            "RATE_LIMITED",
            "MAINTENANCE_MODE",
            "AUTH_REQUIRED",
            "AUTH_REFRESH_UNAVAILABLE",
            "NETWORK_ERROR",
            "REQUEST_TIMEOUT",
            "RESPONSE_INVALID",
            "SYNC_REVISION_INVALID",
            "SYNC_METADATA_INVALID",
        };

        [Fact]
        public void AllCodes_MatchesProtocolSection23()
        {
            var actual = new HashSet<string>(ApiErrorCatalog.AllCodes, StringComparer.Ordinal);
            var expected = new HashSet<string>(ExpectedCodes, StringComparer.Ordinal);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void AllCodes_NoDuplicates()
        {
            Assert.Equal(ApiErrorCatalog.AllCodes.Count, new HashSet<string>(ApiErrorCatalog.AllCodes).Count);
        }

        [Theory]
        [InlineData("AUTH_INVALID_CREDENTIALS", true)]
        [InlineData("RATE_LIMITED", true)]
        [InlineData("AUTH_REQUIRED", true)]
        [InlineData("HTTP_500", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsKnown_ClassifiesCodes(string code, bool expected)
        {
            Assert.Equal(expected, ApiErrorCatalog.IsKnown(code));
        }

        [Fact]
        public void ResourceKey_PrefixesWithApi()
        {
            Assert.Equal("Api_AUTH_INVALID_CREDENTIALS", ApiErrorCatalog.ResourceKey("AUTH_INVALID_CREDENTIALS"));
        }

        public static IEnumerable<object[]> ReswFiles()
        {
            yield return new object[] { "zh-CN" };
            yield return new object[] { "en-US" };
        }

        [Theory]
        [MemberData(nameof(ReswFiles))]
        public void EveryCatalogCode_HasReswEntry(string lang)
        {
            var names = LoadResw(lang);
            foreach (string code in ApiErrorCatalog.AllCodes)
            {
                string key = ApiErrorCatalog.ResourceKey(code);
                Assert.True(names.ContainsKey(key), lang + " 缺少 " + key);
                Assert.False(string.IsNullOrWhiteSpace(names[key]), lang + " 的 " + key + " 文案为空");
            }
        }

        [Theory]
        [MemberData(nameof(ReswFiles))]
        public void LoginValidatorKeys_HaveReswEntries(string lang)
        {
            var names = LoadResw(lang);
            foreach (string key in LoginFormValidator.AllErrorKeys)
            {
                Assert.True(names.ContainsKey(key), lang + " 缺少 " + key);
                Assert.False(string.IsNullOrWhiteSpace(names[key]), lang + " 的 " + key + " 文案为空");
            }
        }

        [Fact]
        public void RateLimitedTemplate_HasSecondPlaceholder()
        {
            // LoginViewModel 用 RetryAfterMs 秒数填充 {0}；模板缺占位符会导致文案丢失数字。
            foreach (string lang in new string[] { "zh-CN", "en-US" })
            {
                var names = LoadResw(lang);
                Assert.Contains("{0}", names[ApiErrorCatalog.ResourceKey(ApiErrorCatalog.RateLimited)]);
            }
        }

        private static Dictionary<string, string> LoadResw(string lang)
        {
            string path = FindResw(lang);
            var doc = XDocument.Load(path);
            return doc.Root.Elements("data")
                .Where(e => e.Attribute("name") != null)
                .ToDictionary(e => e.Attribute("name").Value, e => (string)e.Element("value"));
        }

        private static string FindResw(string lang)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "src", "SshTool.App", "Strings", lang, "Resources.resw");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
            throw new FileNotFoundException("找不到 " + lang + " Resources.resw");
        }
    }
}
