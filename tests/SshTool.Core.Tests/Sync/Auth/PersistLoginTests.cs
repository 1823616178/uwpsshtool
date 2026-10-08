using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.Core.Storage;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Api;
using SshTool.Core.Sync.Api.Dtos;
using SshTool.Core.Sync.Auth;
using SshTool.Core.Sync.Vault;
using SshTool.Core.Tests.Fakes;
using Xunit;

namespace SshTool.Core.Tests.Sync.Auth
{
    // fix/persist-login：「登录状态需要保存」——登录后除非显式退出或服务端确认吊销，
    // 否则跨重启保持登录；离线 / 暂时性网络或存储错误一律不清会话。
    public class PersistLoginTests
    {
        private const string ExpiredJson =
            @"{""statusCode"":401,""data"":{""code"":""AUTH_TOKEN_EXPIRED"",""message"":""过期""}}";
        private const string MeJson = @"{""user"":{""id"":""u1"",""email"":""a@b.c""},""deviceId"":""d1""}";
        private const string RotatedJson =
            @"{""accessToken"":""a-2"",""refreshToken"":""r-2"",""expiresIn"":900," +
            @"""user"":{""id"":""u1"",""email"":""a@b.c""},""device"":{""id"":""d1"",""name"":""Lumia""}}";
        private const string LoginJson =
            @"{""accessToken"":""a-login"",""refreshToken"":""r-login"",""expiresIn"":900," +
            @"""user"":{""id"":""u1"",""email"":""a@b.c""},""device"":{""id"":""d-new"",""name"":""Lumia""}}";

        // 可注入读/写失败的 ISecureFile（模拟 W10M 上暂时性的 DPAPI / 文件占用错误）。
        private sealed class FlakySecureFile : ISecureFile
        {
            private readonly InMemorySecureFile _inner = new InMemorySecureFile();
            public int FailReads;
            public int FailWrites;
            public int Writes;

            public Task<byte[]> ReadAsync()
            {
                if (FailReads > 0)
                {
                    FailReads--;
                    throw new InvalidOperationException("transient read failure");
                }
                return _inner.ReadAsync();
            }

            public Task WriteAsync(byte[] plaintext)
            {
                if (FailWrites > 0)
                {
                    FailWrites--;
                    throw new InvalidOperationException("transient write failure");
                }
                Writes++;
                return _inner.WriteAsync(plaintext);
            }

            public byte[] Raw()
            {
                return _inner.ReadAsync().GetAwaiter().GetResult();
            }
        }

        private sealed class StubDevices : IDeviceDescriptorProvider
        {
            public DeviceDescriptorData GetDescriptor(string nameOverride = null)
            {
                return new DeviceDescriptorData { Name = "Lumia", Platform = "windows-uwp-arm", AppVersion = "test" };
            }
        }

        private static HttpResponseData Json(int status, string body)
        {
            return new HttpResponseData(
                status, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), body);
        }

        private static AuthTokenResponse Tokens(string access = "a1", string refresh = "r1")
        {
            return new AuthTokenResponse
            {
                AccessToken = access,
                RefreshToken = refresh,
                ExpiresIn = 900,
                User = new AuthUserDto { Id = "u1", Email = "a@b.c" },
                Device = new AuthDeviceDto { Id = "d1", Name = "Lumia" }
            };
        }

        private static async Task<AuthStore> SignedInStore(ISecureFile file)
        {
            var store = new AuthStore(file);
            await store.SaveAsync(Tokens());
            return store;
        }

        private static Task NoSleep(long ms)
        {
            return Task.CompletedTask;
        }

        private static SyncCoordinator NewCoordinator(AuthStore auth, ApiClient api, VaultCacheStore vault = null)
        {
            return new SyncCoordinator(
                auth, vault ?? new VaultCacheStore(new InMemorySecureFile()), api, new FakeVaultCrypto(),
                new StubDevices(), sleep: ms => Task.CompletedTask);
        }

        // ---------- 跨重启保持 ----------

        [Fact]
        public async Task SessionSurvivesRestart_OfflineStartupDoesNotSignOut()
        {
            var file = new FlakySecureFile();
            await SignedInStore(file);

            // 「重启」：全新的 AuthStore / ApiClient / Coordinator，启动时完全离线。
            var auth = new AuthStore(file);
            var transport = new FakeHttpTransport { Handler = r => { throw new InvalidOperationException("offline"); } };
            var api = new ApiClient("https://sync.example.com", auth, transport, sleep: NoSleep);
            var sync = NewCoordinator(auth, api);

            await sync.InitializeAsync(); // 无 vaultId → 探测失败（离线）也不能丢会话

            Assert.True(auth.Session.Authenticated);
            Assert.Equal("a@b.c", auth.Session.UserEmail);
            Assert.NotEqual(SyncPhase.SignedOut, sync.State.Phase);
            Assert.NotEqual(SyncPhase.AuthError, sync.State.Phase);
        }

        // ---------- 刷新：暂时性失败不登出 ----------

        [Fact]
        public async Task Refresh_ConnectPhaseFailure_NotUncertainNotCleared()
        {
            var store = await SignedInStore(new FlakySecureFile());
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(401, ExpiredJson));
            transport.Enqueue(new HttpConnectionFailedException("connect failed", new Exception("x")));
            var client = new ApiClient("http://sync.example.com", store, transport, allowHttp: true);

            var error = await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());

            Assert.Equal(ApiErrorKind.Network, error.Kind);
            Assert.False(error.Ambiguous); // 请求一定没发出
            Assert.True(store.CanRefresh);  // 连 uncertain 都不标
            Assert.True(store.Session.Authenticated);
        }

        [Fact]
        public async Task Refresh_UncertainThenServerSaysReused_ClearsSession()
        {
            var store = await SignedInStore(new FlakySecureFile());
            await store.MarkRefreshUncertainAsync();
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(401, ExpiredJson));
            transport.Enqueue(Json(401,
                @"{""statusCode"":401,""data"":{""code"":""AUTH_TOKEN_REUSED"",""message"":""重用""}}"));
            var client = new ApiClient("https://sync.example.com", store, transport);

            var error = await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());

            Assert.Equal("AUTH_TOKEN_REUSED", error.Code);
            Assert.False(store.Session.Authenticated); // 服务端确认吊销才登出
            Assert.Equal(2, transport.Requests.Count);
        }

        [Fact]
        public async Task Refresh_UncertainPersistedAcrossRestart_StillRefreshes()
        {
            var file = new FlakySecureFile();
            var first = await SignedInStore(file);
            await first.MarkRefreshUncertainAsync();

            var store = new AuthStore(file);
            await store.LoadAsync();
            Assert.False(store.CanRefresh);
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(401, ExpiredJson));
            transport.Enqueue(Json(200, RotatedJson));
            transport.Enqueue(Json(200, MeJson));
            var client = new ApiClient("https://sync.example.com", store, transport);

            await client.GetMeAsync();

            Assert.True(store.Session.Authenticated);
            Assert.True(store.CanRefresh);
            Assert.Equal("r-2", store.Tokens.RefreshToken);
        }

        [Fact]
        public async Task Refresh_RepeatedTimeouts_NeverClearSession()
        {
            var store = await SignedInStore(new FlakySecureFile());
            var transport = new FakeHttpTransport();
            var client = new ApiClient("https://sync.example.com", store, transport);
            for (int i = 0; i < 3; i++)
            {
                transport.Enqueue(Json(401, ExpiredJson));
                transport.Enqueue(new TimeoutException("slow"));
                await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());
                Assert.True(store.Session.Authenticated);
            }
        }

        // ---------- Coordinator：只有带服务端业务码的 401 才登出 ----------

        [Fact]
        public void TerminalAuthError_Requires401WithServerCode()
        {
            var coded = new ApiError(ApiErrorKind.Authentication, "AUTH_TOKEN_EXPIRED", "x", status: 401);
            var uncoded = new ApiError(ApiErrorKind.Authentication, "HTTP_401", "x", status: 401, codeUnknown: true);
            var reused = new ApiError(ApiErrorKind.Authentication, "AUTH_TOKEN_REUSED", "x", status: 401);
            var revoked = new ApiError(ApiErrorKind.Authentication, "AUTH_DEVICE_REVOKED", "x", status: 401);
            var unavailable = new ApiError(ApiErrorKind.Authentication, ApiError.CodeAuthRefreshUnavailable, "x");
            var network = new ApiError(ApiErrorKind.Network, ApiError.CodeNetworkError, "x");

            Assert.True(SyncCoordinator.IsTerminalAuthError(coded));
            Assert.False(SyncCoordinator.IsTerminalAuthError(uncoded));
            Assert.True(SyncCoordinator.IsTerminalAuthError(reused));
            Assert.True(SyncCoordinator.IsTerminalAuthError(revoked));
            Assert.True(SyncCoordinator.IsTerminalAuthError(unavailable));
            Assert.False(SyncCoordinator.IsTerminalAuthError(network));
            Assert.False(SyncCoordinator.IsTerminalAuthError(null));
        }

        [Fact]
        public async Task Probe_Uncoded401AfterRefresh_KeepsSession()
        {
            var store = await SignedInStore(new FlakySecureFile());
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(401, null));            // 无 data.code（代理页等）
            transport.Enqueue(Json(200, RotatedJson));      // 刷新成功
            transport.Enqueue(Json(401, "<html>401</html>")); // 重放仍是无码 401
            var client = new ApiClient("https://sync.example.com", store, transport, retryLimit: 0);
            var sync = NewCoordinator(store, client);

            await sync.InitializeAsync();

            Assert.True(store.Session.Authenticated);
            Assert.NotEqual(SyncPhase.AuthError, sync.State.Phase);
        }

        // ---------- 启动读 auth.bin 的暂时性失败 ----------

        [Fact]
        public async Task Initialize_TransientReadFailure_RetriesAndRestores()
        {
            var file = new FlakySecureFile();
            await SignedInStore(file);
            file.FailReads = 2; // 前两次读失败，第三次成功（重试表 2 次）

            var auth = new AuthStore(file);
            var transport = new FakeHttpTransport { Handler = r => { throw new InvalidOperationException("offline"); } };
            var sync = NewCoordinator(auth, new ApiClient("https://sync.example.com", auth, transport, sleep: NoSleep));

            await sync.InitializeAsync();

            Assert.True(auth.Session.Authenticated);
            Assert.False(auth.LastLoadFailed);
            Assert.False(sync.SessionLoadFailed);
        }

        [Fact]
        public async Task Initialize_PersistentReadFailure_KeepsFileAndRecoversLater()
        {
            var file = new FlakySecureFile();
            await SignedInStore(file);
            file.FailReads = 10;

            var auth = new AuthStore(file);
            var transport = new FakeHttpTransport { Handler = r => { throw new InvalidOperationException("offline"); } };
            var sync = NewCoordinator(auth, new ApiClient("https://sync.example.com", auth, transport, sleep: NoSleep));

            await Assert.ThrowsAnyAsync<Exception>(() => sync.InitializeAsync());
            Assert.True(sync.SessionLoadFailed);
            Assert.False(auth.Session.Authenticated);
            Assert.NotEmpty(file.Raw()); // 文件没被当成损坏清掉

            file.FailReads = 0; // 例如回到前台时 DPAPI 已恢复
            bool restored = await sync.RecoverSessionIfLoadFailedAsync();

            Assert.True(restored);
            Assert.True(auth.Session.Authenticated);
            Assert.False(sync.SessionLoadFailed);
            // 已登录（探测离线 → offline），不是 signed_out / auth_error
            Assert.NotEqual(SyncPhase.SignedOut, sync.State.Phase);
            Assert.NotEqual(SyncPhase.AuthError, sync.State.Phase);
        }

        [Fact]
        public async Task Login_AfterFailedStartupRead_RestoresInsteadOfCreatingNewDevice()
        {
            var file = new FlakySecureFile();
            await SignedInStore(file);
            file.FailReads = 10;

            var auth = new AuthStore(file);
            var transport = new FakeHttpTransport { Handler = r => { throw new InvalidOperationException("offline"); } };
            var sync = NewCoordinator(auth, new ApiClient("https://sync.example.com", auth, transport, sleep: NoSleep));
            await Assert.ThrowsAnyAsync<Exception>(() => sync.InitializeAsync());

            file.FailReads = 0;
            int before = transport.Requests.Count;
            // 已有会话：登录被拒（与 EnsureNotSignedIn 一致，LoginViewModel 据此直接路由），不发 auth/login
            await Assert.ThrowsAsync<SyncOperationException>(() => sync.LoginAsync("a@b.c", "pw"));
            Assert.True(auth.Session.Authenticated);
            foreach (var request in transport.Requests.GetRange(before, transport.Requests.Count - before))
            {
                Assert.DoesNotContain("/auth/login", request.Url);
            }
        }

        [Fact]
        public async Task Recover_NoopWhenLoadDidNotFail()
        {
            var auth = new AuthStore(new FlakySecureFile());
            await auth.LoadAsync();
            var transport = new FakeHttpTransport();
            var sync = NewCoordinator(auth, new ApiClient("https://sync.example.com", auth, transport, sleep: NoSleep));

            Assert.False(await sync.RecoverSessionIfLoadFailedAsync());
            Assert.Empty(transport.Requests);
        }

        // ---------- 写盘失败：不丢新签发的 token ----------

        [Fact]
        public async Task Save_WriteFailsTwice_AdoptsSessionAndFlushPersistsLater()
        {
            var file = new FlakySecureFile { FailWrites = 2 };
            var store = new AuthStore(file);

            var state = await store.SaveAsync(Tokens("a-x", "r-x"));

            Assert.True(state.Authenticated);
            Assert.True(store.PersistPending);
            Assert.Equal("r-x", store.Tokens.RefreshToken);
            Assert.Empty(file.Raw());

            Assert.True(await store.FlushAsync());
            Assert.False(store.PersistPending);
            var reloaded = new AuthStore(file);
            await reloaded.LoadAsync();
            Assert.Equal("r-x", reloaded.Tokens.RefreshToken);
        }

        [Fact]
        public async Task Save_WriteFailsOnce_RetriesImmediately()
        {
            var file = new FlakySecureFile { FailWrites = 1 };
            var store = new AuthStore(file);

            await store.SaveAsync(Tokens());

            Assert.False(store.PersistPending);
            Assert.NotEmpty(file.Raw());
        }

        [Fact]
        public async Task Refresh_RotatedTokenWriteFails_KeepsRotatedTokenInMemory()
        {
            var file = new FlakySecureFile();
            var store = await SignedInStore(file);
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(401, ExpiredJson));
            transport.Enqueue(Json(200, RotatedJson));
            transport.Enqueue(Json(200, MeJson));
            var client = new ApiClient("https://sync.example.com", store, transport);
            file.FailWrites = 2;

            await client.GetMeAsync();

            // 服务端已作废 r1：内存必须换成 r-2，否则下次刷新撞重用检测被强制登出
            Assert.Equal("r-2", store.Tokens.RefreshToken);
            Assert.True(store.PersistPending);
        }

        [Fact]
        public async Task Load_WhilePersistPending_DoesNotOverwriteNewerMemory()
        {
            var file = new FlakySecureFile();
            var store = await SignedInStore(file);
            file.FailWrites = 2;
            await store.SaveAsync(Tokens("a-new", "r-new"));
            Assert.True(store.PersistPending);

            await store.LoadAsync();

            Assert.Equal("r-new", store.Tokens.RefreshToken);
            Assert.False(store.PersistPending);
        }

        [Fact]
        public async Task Clear_WriteFails_StillSignsOutInMemoryAndFlushWipesFile()
        {
            var file = new FlakySecureFile();
            var store = await SignedInStore(file);
            file.FailWrites = 2;

            await store.ClearAsync(); // 不抛

            Assert.False(store.Session.Authenticated);
            Assert.True(store.PersistPending);
            Assert.True(await store.FlushAsync());
            Assert.Empty(file.Raw());
        }

        [Fact]
        public async Task Load_ReadThrows_SetsFlagAndKeepsFile()
        {
            var file = new FlakySecureFile();
            await SignedInStore(file);
            file.FailReads = 1;
            var store = new AuthStore(file);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.LoadAsync());
            Assert.True(store.LastLoadFailed);
            Assert.NotEmpty(file.Raw());

            await store.LoadAsync();
            Assert.False(store.LastLoadFailed);
            Assert.True(store.Session.Authenticated);
        }

        [Fact]
        public void IsRequestNotSent_WalksInnerChain()
        {
            var connect = new HttpConnectionFailedException("c", new Exception("inner"));
            var wrapped = new ApiError(ApiErrorKind.Network, ApiError.CodeNetworkError, "x", inner: connect);
            var other = new ApiError(ApiErrorKind.Network, ApiError.CodeNetworkError, "x",
                inner: new InvalidOperationException("aborted"));

            Assert.True(ApiClient.IsRequestNotSent(wrapped));
            Assert.False(ApiClient.IsRequestNotSent(other));
            Assert.False(ApiClient.IsRequestNotSent(null));
        }
    }
}
