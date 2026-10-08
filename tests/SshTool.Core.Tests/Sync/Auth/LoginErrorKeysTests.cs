using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Api;
using SshTool.Core.Sync.Auth;
using Xunit;

namespace SshTool.Core.Tests.Sync.Auth
{
    // fix/login-feedback：登录失败文案判定。核心约束：任何异常都映射到一条存在于两份 resw 的键，
    // 登录页不会再出现「失败了但什么也不显示」。
    public class LoginErrorKeysTests
    {
        [Fact]
        public void ApiError_DefersToCatalog()
        {
            var api = new ApiError(ApiErrorKind.Authentication, ApiErrorCatalog.AuthInvalidCredentials, "bad", status: 401);
            Assert.Null(LoginErrorKeys.OverrideKey(api));
        }

        [Fact]
        public void PlainNetworkError_DefersToCatalog()
        {
            var api = new ApiError(ApiErrorKind.Network, ApiError.CodeNetworkError, "x", ambiguous: true);
            Assert.Null(LoginErrorKeys.OverrideKey(api));
        }

        [Fact]
        public void FallbackPendingNetworkError_AsksToRetry()
        {
            var api = new ApiError(ApiErrorKind.Network, ApiError.CodeNetworkError, "x",
                ambiguous: true, httpFallbackActivated: true);
            Assert.Equal(LoginErrorKeys.HttpFallbackRetryKey, LoginErrorKeys.OverrideKey(api));
        }

        [Fact]
        public void SyncOperationException_DefersToSyncText()
        {
            var ex = new SyncOperationException(SyncErrorCode.SyncPasswordWrong, "x");
            Assert.Null(LoginErrorKeys.OverrideKey(ex));
        }

        [Fact]
        public void TimeoutAndCancel_MapToRequestTimeout()
        {
            string expected = ApiErrorCatalog.ResourceKey(ApiError.CodeRequestTimeout);
            Assert.Equal(expected, LoginErrorKeys.OverrideKey(new TimeoutException()));
            Assert.Equal(expected, LoginErrorKeys.OverrideKey(new TaskCanceledException()));
            Assert.Equal(expected, LoginErrorKeys.OverrideKey(new OperationCanceledException()));
        }

        [Fact]
        public void ConnectionFailed_MapsToNetworkError()
        {
            Assert.Equal(ApiErrorCatalog.ResourceKey(ApiError.CodeNetworkError),
                LoginErrorKeys.OverrideKey(new HttpConnectionFailedException("x", null)));
        }

        // 旧实现把任何 InvalidOperationException 都显示成「已登录，请先退出」，其余异常直接显示 ex.Message
        //（可能为空 → 错误区折叠 = 静默）。现在一律走通用文案。
        [Fact]
        public void UnexpectedExceptions_MapToGeneric()
        {
            Assert.Equal(LoginErrorKeys.UnexpectedKey, LoginErrorKeys.OverrideKey(new InvalidOperationException("x")));
            Assert.Equal(LoginErrorKeys.UnexpectedKey, LoginErrorKeys.OverrideKey(new NullReferenceException()));
            Assert.Equal(LoginErrorKeys.UnexpectedKey, LoginErrorKeys.OverrideKey(new Exception(string.Empty)));
            Assert.Equal(LoginErrorKeys.UnexpectedKey, LoginErrorKeys.OverrideKey(null));
        }

        [Fact]
        public void AggregateException_IsUnwrapped()
        {
            var wrapped = new AggregateException(new TimeoutException());
            Assert.Equal(ApiErrorCatalog.ResourceKey(ApiError.CodeRequestTimeout), LoginErrorKeys.OverrideKey(wrapped));
            var api = new AggregateException(new ApiError(ApiErrorKind.Network, ApiError.CodeNetworkError, "x"));
            Assert.Null(LoginErrorKeys.OverrideKey(api));
        }

        [Fact]
        public void Diagnostic_HasTypeAndHResult_NoMessage()
        {
            var ex = new InvalidOperationException("secret-ish message");
            string diag = LoginErrorKeys.Diagnostic(ex);
            Assert.StartsWith("InvalidOperationException 0x", diag);
            Assert.DoesNotContain("secret", diag);
            Assert.Equal("unknown", LoginErrorKeys.Diagnostic(null));
        }

        public static IEnumerable<object[]> ReswFiles()
        {
            yield return new object[] { "zh-cn" };
            yield return new object[] { "en-us" };
        }

        [Theory]
        [MemberData(nameof(ReswFiles))]
        public void EveryReturnedKey_ExistsInResw(string lang)
        {
            var names = Load(lang);
            var keys = new List<string>(LoginErrorKeys.AllKeys)
            {
                ApiErrorCatalog.ResourceKey(ApiError.CodeRequestTimeout),
                ApiErrorCatalog.ResourceKey(ApiError.CodeNetworkError),
            };
            foreach (string key in keys)
            {
                Assert.True(names.ContainsKey(key), lang + " missing " + key);
                Assert.False(string.IsNullOrWhiteSpace(names[key]), lang + " empty " + key);
            }
            // 通用文案必须带 {0}（异常类型名 + HRESULT），且只有这一个占位符。
            string generic = names[LoginErrorKeys.UnexpectedKey];
            Assert.Contains("{0}", generic);
            Assert.DoesNotContain("{1}", generic);
        }

        private static Dictionary<string, string> Load(string lang)
        {
            var doc = XDocument.Load(FindResw(lang));
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
            throw new FileNotFoundException("Resources.resw not found: " + lang);
        }
    }
}
