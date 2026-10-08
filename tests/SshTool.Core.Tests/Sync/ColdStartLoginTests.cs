using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SshTool.Core.Storage;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Api;
using SshTool.Core.Sync.Auth;
using SshTool.Core.Sync.Vault;
using SshTool.Core.Tests.Fakes;
using Xunit;
using Harness = SshTool.Core.Tests.Sync.SyncCoordinatorVaultTests.Harness;

namespace SshTool.Core.Tests.Sync
{
    // fix/cold-start-login：「怎么每次打开软件都要重新登录呢」。
    //  - 组合根漏传 local:（同步从未读写本机数据，首页一直空列表 +「登录同步」）；
    //  - accessToken 的 401 不止 AUTH_TOKEN_EXPIRED（实测还有 AUTH_TOKEN_INVALID_SIGNATURE /
    //    AUTH_TOKEN_REQUIRED），此前不刷新、直接当终端鉴权清空会话；
    //  - vault.bin 读失败让 InitializeAsync 在应用登录状态之前就抛出。
    public class ColdStartLoginTests
    {
        private const string MeJson = @"{""user"":{""id"":""u1"",""email"":""a@b.c""},""deviceId"":""d1""}";
        private const string RotatedTokensJson =
            @"{""accessToken"":""a-2"",""refreshToken"":""r-2"",""expiresIn"":3600," +
            @"""user"":{""id"":""u1"",""email"":""a@b.c""},""device"":{""id"":""d1"",""name"":""Lumia""}}";

        private static HttpResponseData Json(int status, string body)
        {
            return new HttpResponseData(
                status, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), body);
        }

        private static HttpResponseData Unauthorized(string code)
        {
            return Json(401, @"{""statusCode"":401,""data"":{""code"":""" + code + @""",""message"":""x""}}");
        }

        // ---------- ApiClient：accessToken 的各种 401 都先刷新 ----------

        [Theory]
        [InlineData("AUTH_TOKEN_INVALID_SIGNATURE")]
        [InlineData("AUTH_TOKEN_REQUIRED")]
        [InlineData("AUTH_TOKEN_EXPIRED")]
        public async Task AccessToken401_AnyTokenCode_RefreshesAndKeepsSession(string code)
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Unauthorized(code));
            transport.Enqueue(Json(200, RotatedTokensJson));
            transport.Enqueue(Json(200, MeJson));
            var tokens = FakeTokenStore.SignedIn();
            var client = new ApiClient("http://sync.example.com", tokens, transport, allowHttp: true);

            var me = await client.GetMeAsync();

            Assert.Equal("u1", me.User.Id);
            Assert.Equal(3, transport.Requests.Count);
            Assert.EndsWith("/auth/refresh", transport.Requests[1].Url);
            Assert.Equal("a-2", tokens.Saved.AccessToken);
            Assert.False(tokens.Cleared);
        }

        [Fact]
        public async Task TokenReused_DoesNotRefresh_ClearsAndReportsCode()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Unauthorized("AUTH_TOKEN_REUSED"));
            var tokens = FakeTokenStore.SignedIn();
            var client = new ApiClient("http://sync.example.com", tokens, transport, allowHttp: true);
            var cleared = new List<string>();
            client.TokensCleared += c => cleared.Add(c);

            var error = await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());

            Assert.Equal("AUTH_TOKEN_REUSED", error.Code);
            Assert.Single(transport.Requests);
            Assert.True(tokens.Cleared);
            Assert.Equal(new[] { "AUTH_TOKEN_REUSED" }, cleared);
        }

        [Fact]
        public async Task RefreshRejected_ClearsAndReportsCode()
        {
            var transport = new FakeHttpTransport();
            transport.Enqueue(Unauthorized("AUTH_TOKEN_INVALID_SIGNATURE"));
            transport.Enqueue(Unauthorized("AUTH_TOKEN_EXPIRED"));
            var tokens = FakeTokenStore.SignedIn();
            var client = new ApiClient("http://sync.example.com", tokens, transport, allowHttp: true);
            var cleared = new List<string>();
            client.TokensCleared += c => cleared.Add(c);

            await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());

            Assert.True(tokens.Cleared);
            Assert.Contains("AUTH_TOKEN_EXPIRED", cleared);
        }

        [Fact]
        public void ShouldRefresh_OnlyForTokenProblems()
        {
            Assert.True(ApiClient.ShouldRefresh(Err(401, "AUTH_TOKEN_INVALID_SIGNATURE")));
            Assert.True(ApiClient.ShouldRefresh(Err(401, "AUTH_TOKEN_EXPIRED")));
            Assert.False(ApiClient.ShouldRefresh(Err(401, "AUTH_TOKEN_REUSED")));
            Assert.False(ApiClient.ShouldRefresh(Err(401, "AUTH_CURRENT_PASSWORD_INVALID")));
            Assert.False(ApiClient.ShouldRefresh(Err(401, "AUTH_INVALID_CREDENTIALS")));
            Assert.False(ApiClient.ShouldRefresh(Err(403, "AUTH_TOKEN_EXPIRED")));
            Assert.False(ApiClient.ShouldRefresh(null));
        }

        private static ApiError Err(int status, string code)
        {
            return new ApiError(
                status == 401 ? ApiErrorKind.Authentication : ApiErrorKind.Http, code, "x", status: status);
        }

        // ---------- 本地适配器缺失 ----------

        [Fact]
        public async Task CoordinatorWithoutLocalPort_ReportsCodedError_KeepsSession()
        {
            var h = new Harness();
            await h.Coordinator.InitializeAsync();
            await h.Coordinator.RegisterAsync("a@b.c", "login-password");
            await h.Coordinator.SetupVaultAsync("sync-password");
            Assert.False(h.Coordinator.HasLocalPort);

            var error = await Assert.ThrowsAsync<SyncOperationException>(() => h.Coordinator.SyncNowAsync());

            Assert.Equal(SyncErrorCode.LocalAdapterMissing, error.Code);
            Assert.True(h.Auth.Session.Authenticated);
            Assert.Equal(SyncPhase.Error, h.Coordinator.State.Phase);
            Assert.IsType<SyncOperationException>(h.Coordinator.State.MessageError);
            Assert.Contains(h.Logger.Lines, l => l.Contains("本地适配器缺失"));
        }

        // ---------- vault.bin 读失败不再吞掉登录状态 ----------

        private sealed class FlakySecureFile : ISecureFile
        {
            private readonly ISecureFile _inner;
            public bool FailReads;

            public FlakySecureFile(ISecureFile inner)
            {
                _inner = inner;
            }

            public Task<byte[]> ReadAsync()
            {
                if (FailReads)
                {
                    throw new IOException("transient read failure");
                }
                return _inner.ReadAsync();
            }

            public Task WriteAsync(byte[] plaintext)
            {
                return _inner.WriteAsync(plaintext);
            }
        }

        [Fact]
        public async Task VaultCacheReadFails_StillSignedIn_RecoversOnRetry()
        {
            var owner = new Harness();
            await owner.Coordinator.InitializeAsync();
            await owner.Coordinator.RegisterAsync("a@b.c", "login-password");
            await owner.Coordinator.SetupVaultAsync("sync-password");

            var flaky = new FlakySecureFile(owner.VaultFile) { FailReads = true };
            var logger = new SyncCoordinatorVaultTests.CapturingLogger();
            var auth = new AuthStore(owner.AuthFile, logger: logger);
            var vault = new VaultCacheStore(flaky, logger);
            var transport = new FakeHttpTransport { Handler = owner.Server.Handle };
            var api = new ApiClient("https://sync.example.test", auth, transport, retryLimit: 0);
            var coordinator = new SyncCoordinator(auth, vault, api, owner.Crypto,
                new SyncCoordinatorVaultTests.StubDevices(), logger);

            await coordinator.InitializeAsync();

            Assert.True(auth.Session.Authenticated);
            Assert.Equal(SyncPhase.Error, coordinator.State.Phase);
            Assert.Equal(VaultStatus.Locked, coordinator.State.Vault);
            Assert.True(coordinator.SessionLoadFailed);
            Assert.NotEqual(SyncScreenKind.Login,
                new SyncStatePresenter().DetermineScreen(auth.Session, coordinator.State));
            Assert.Contains(logger.Lines, l => l.Contains("启动恢复登录状态：已登录"));

            flaky.FailReads = false;
            bool restored = await coordinator.RecoverSessionIfLoadFailedAsync();

            Assert.True(restored);
            Assert.False(coordinator.SessionLoadFailed);
            Assert.Equal(VaultStatus.Ready, coordinator.State.Vault);
        }

        // ---------- 诊断日志 ----------

        [Fact]
        public async Task AuthStore_LogsLoadAndClearReason_WithoutSecrets()
        {
            var h = new Harness();
            await h.Coordinator.InitializeAsync();
            await h.Coordinator.RegisterAsync("a@b.c", "login-password");
            var logger = new SyncCoordinatorVaultTests.CapturingLogger();
            var store = new AuthStore(h.AuthFile, logger: logger);

            await store.LoadAsync();
            await store.ClearAsync("logout");
            await store.LoadAsync();

            Assert.Contains(logger.Lines, l => l.Contains("已登录"));
            Assert.Contains(logger.Lines, l => l.Contains("清除登录状态（logout）"));
            Assert.Contains(logger.Lines, l => l.Contains("auth.bin 不存在或为空"));
            Assert.DoesNotContain(logger.Lines, l => l.Contains("a@b.c") || l.Contains("r-login") || l.Contains("a-login"));
        }

        // ---------- App 组合根接线自检（UWP 工程在此无法编译，按源码文本检查） ----------

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
            throw new FileNotFoundException(string.Join("/", parts));
        }

        private static string Block(string text, string start)
        {
            int i = text.IndexOf(start, StringComparison.Ordinal);
            Assert.True(i >= 0, start + " not found");
            int end = text.IndexOf(");", i, StringComparison.Ordinal);
            return text.Substring(i, end - i);
        }

        [Fact]
        public void AppServices_WiresLocalAdapterAndKeyInspector()
        {
            string text = File.ReadAllText(
                RepoFile("src", "SshTool.App", "Infrastructure", "AppServices.cs"), Encoding.UTF8);

            string coordinator = Block(text, "Sync = new SyncCoordinator(");
            Assert.Contains("local: SyncLocal", coordinator);
            Assert.Contains("lastDevice:", coordinator);
            Assert.Contains("deviceOptions:", coordinator);
            Assert.Contains("timers:", coordinator);

            string adapter = Block(text, "SyncLocal = new SyncLocalAdapter(");
            Assert.Contains("KeyToolPrivateKeyInspector", adapter);
            Assert.Contains("TunnelManager", adapter);
        }

        [Theory]
        [InlineData("zh-cn")]
        [InlineData("en-us")]
        public void EverySyncErrorCode_HasLocalizedText(string lang)
        {
            string resw = File.ReadAllText(
                RepoFile("src", "SshTool.App", "Strings", lang, "Resources.resw"), Encoding.UTF8);
            foreach (SyncErrorCode code in Enum.GetValues(typeof(SyncErrorCode)))
            {
                if (code == SyncErrorCode.Unknown)
                {
                    continue;
                }
                Assert.Contains("name=\"Sync_Err_" + code + "\"", resw);
            }
            Assert.Contains("name=\"Sync_Err_Local\"", resw);
            Assert.Contains("name=\"Sync_Err_Apply\"", resw);
            Assert.Contains("name=\"Hosts_EmptySignedIn\"", resw);
        }
    }
}
