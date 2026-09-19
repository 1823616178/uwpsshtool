using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SshTool.Core.Storage;
using SshTool.Core.Sync.Api;
using SshTool.Core.Sync.Api.Dtos;
using SshTool.Core.Sync.Auth;
using SshTool.Core.Tests.Fakes;
using Xunit;

namespace SshTool.Core.Tests.Sync.Auth
{
    // S08 验收：持久化往返、uncertain 规则、ITokenStore 与 ApiClient 对接
    // （终端鉴权清 token、刷新轮换持久化，语义与 S07 一致）。
    public class AuthStoreTests
    {
        private static readonly DateTimeOffset FixedNow =
            new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);

        private static readonly DateTimeOffset UnixEpoch =
            new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

        private static long EpochMs(DateTimeOffset value)
        {
            return (long)(value.ToUniversalTime() - UnixEpoch).TotalMilliseconds;
        }

        private static AuthTokenResponse Tokens(
            string access = "a1",
            string refresh = "r1",
            int expiresIn = 3600,
            string userId = "u1",
            string email = "a@b.c",
            string deviceId = "d1",
            string deviceName = "Lumia")
        {
            return new AuthTokenResponse
            {
                AccessToken = access,
                RefreshToken = refresh,
                ExpiresIn = expiresIn,
                User = new AuthUserDto { Id = userId, Email = email },
                Device = new AuthDeviceDto { Id = deviceId, Name = deviceName }
            };
        }

        private static AuthStore NewStore(InMemorySecureFile file)
        {
            return new AuthStore(file, () => FixedNow);
        }

        private static HttpResponseData Json(int status, string body)
        {
            return new HttpResponseData(
                status,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                body);
        }

        [Fact]
        public async Task SaveAndLoad_RoundTrip()
        {
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            Assert.False(store.Session.Authenticated);
            Assert.Null(store.GetTokens());
            Assert.False(store.CanRefresh);

            await store.SaveAsync(Tokens());

            var session = store.Session;
            Assert.True(session.Authenticated);
            Assert.Equal("u1", session.UserId);
            Assert.Equal("a@b.c", session.UserEmail);
            Assert.Equal("d1", session.DeviceId);
            Assert.Equal("Lumia", session.DeviceName);

            var tokens = store.Tokens;
            Assert.Equal("a1", tokens.AccessToken);
            Assert.Equal("r1", tokens.RefreshToken);
            Assert.Equal(EpochMs(FixedNow) + 3600L * 1000L, tokens.ExpiresAt);
            Assert.True(store.CanRefresh);

            ITokenStore asTokens = store;
            Assert.Equal("a1", asTokens.GetTokens().AccessToken);
            Assert.Equal("r1", asTokens.GetTokens().RefreshToken);

            // 新实例从同一文件读回
            var reloaded = NewStore(file);
            var loaded = await reloaded.LoadAsync();
            Assert.True(loaded.Authenticated);
            Assert.Equal("u1", loaded.UserId);
            Assert.Equal("d1", loaded.DeviceId);
            Assert.Equal("a1", reloaded.Tokens.AccessToken);
            Assert.Equal(EpochMs(FixedNow) + 3600L * 1000L, reloaded.Tokens.ExpiresAt);
            Assert.True(reloaded.CanRefresh);
        }

        [Fact]
        public async Task PersistedJson_HasExpectedShape()
        {
            var file = new InMemorySecureFile();
            await NewStore(file).SaveAsync(Tokens());

            string text = Encoding.UTF8.GetString(await file.ReadAsync(), 0, (await file.ReadAsync()).Length);
            var root = JObject.Parse(text);
            Assert.Equal(1L, (long)root["version"]);
            Assert.Equal("u1", (string)root["user"]["id"]);
            Assert.Equal("a@b.c", (string)root["user"]["email"]);
            Assert.Equal("d1", (string)root["device"]["id"]);
            Assert.Equal("Lumia", (string)root["device"]["name"]);
            Assert.Equal("a1", (string)root["tokens"]["accessToken"]);
            Assert.Equal("r1", (string)root["tokens"]["refreshToken"]);
            Assert.Equal(EpochMs(FixedNow) + 3600L * 1000L, (long)root["tokens"]["expiresAt"]);
            Assert.False((bool)root["refreshUncertain"]);
        }

        [Fact]
        public async Task Uncertain_MarkBlocksRefresh_SaveResetsAndPersists()
        {
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            await store.SaveAsync(Tokens());
            Assert.True(store.CanRefresh);

            await store.MarkRefreshUncertainAsync();
            Assert.False(store.CanRefresh);

            // 跨重启保持 uncertain
            var reloaded = NewStore(file);
            await reloaded.LoadAsync();
            Assert.True(reloaded.Session.Authenticated);
            Assert.False(reloaded.CanRefresh);

            // 保存新 token 后复位
            await reloaded.SaveAsync(Tokens(access: "a2", refresh: "r2"));
            Assert.True(reloaded.CanRefresh);
            Assert.Equal("a2", reloaded.Tokens.AccessToken);

            var reloaded2 = NewStore(file);
            await reloaded2.LoadAsync();
            Assert.True(reloaded2.CanRefresh);
        }

        [Fact]
        public async Task MarkUncertain_WithoutSession_NoThrowAndCannotRefresh()
        {
            var store = NewStore(new InMemorySecureFile());
            await store.MarkRefreshUncertainAsync();
            Assert.False(store.Session.Authenticated);
            Assert.False(store.CanRefresh);
            Assert.Null(store.GetTokens());
        }

        [Fact]
        public async Task Clear_RemovesSessionAndFile()
        {
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            await store.SaveAsync(Tokens());
            await store.ClearAsync();

            Assert.False(store.Session.Authenticated);
            Assert.Null(store.GetTokens());
            Assert.False(store.CanRefresh);

            var reloaded = NewStore(file);
            await reloaded.LoadAsync();
            Assert.False(reloaded.Session.Authenticated);
        }

        [Fact]
        public async Task Load_EmptyFile_Unauthenticated()
        {
            var store = NewStore(new InMemorySecureFile());
            var session = await store.LoadAsync();
            Assert.False(session.Authenticated);
            Assert.False(store.CanRefresh);
        }

        [Fact]
        public async Task Load_CorruptFile_UnauthenticatedAndClearsFile()
        {
            var file = new InMemorySecureFile();
            await file.WriteAsync(Encoding.UTF8.GetBytes("{不是json"));
            var store = NewStore(file);
            var session = await store.LoadAsync();
            Assert.False(session.Authenticated);
            Assert.False(store.CanRefresh);
            Assert.Empty(await file.ReadAsync());
        }

        [Fact]
        public async Task Load_WrongVersion_Unauthenticated()
        {
            var file = new InMemorySecureFile();
            await file.WriteAsync(Encoding.UTF8.GetBytes(
                @"{""version"":2,""user"":{""id"":""u"",""email"":""e""},"
                + @"""device"":{""id"":""d"",""name"":""n""},"
                + @"""tokens"":{""accessToken"":""a"",""refreshToken"":""r"",""expiresAt"":1},"
                + @"""refreshUncertain"":false}"));
            var store = NewStore(file);
            Assert.False((await store.LoadAsync()).Authenticated);
        }

        // ITokenStore 与 ApiClient 对接：终端鉴权错误清 token（与 S07 一致），且落盘。
        [Fact]
        public async Task TokenStore_ApiClient_TerminalAuthError_ClearsPersistedSession()
        {
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            await store.SaveAsync(Tokens());
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(401,
                @"{""statusCode"":401,""data"":{""code"":""AUTH_DEVICE_REVOKED"",""message"":""设备已撤销""}}"));
            var client = new ApiClient("https://sync.example.com", store, transport);

            var error = await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());
            Assert.Equal("AUTH_DEVICE_REVOKED", error.Code);
            Assert.False(store.Session.Authenticated);
            Assert.Null(store.GetTokens());

            var reloaded = NewStore(file);
            Assert.False((await reloaded.LoadAsync()).Authenticated);
        }

        // 刷新成功后轮换的 token 经 ITokenStore.Save 落盘。
        [Fact]
        public async Task TokenStore_ApiClient_RefreshSuccess_PersistsRotatedTokens()
        {
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            await store.SaveAsync(Tokens());
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(401,
                @"{""statusCode"":401,""data"":{""code"":""AUTH_TOKEN_EXPIRED"",""message"":""过期""}}"));
            transport.Enqueue(Json(200,
                @"{""accessToken"":""a-2"",""refreshToken"":""r-2"",""expiresIn"":3600,"
                + @"""user"":{""id"":""u1"",""email"":""a@b.c""},""device"":{""id"":""d1"",""name"":""Lumia""}}"));
            transport.Enqueue(Json(200,
                @"{""user"":{""id"":""u1"",""email"":""a@b.c""},""deviceId"":""d1""}"));
            var client = new ApiClient("https://sync.example.com", store, transport);

            await client.GetMeAsync();

            Assert.Equal("a-2", store.Tokens.AccessToken);
            Assert.Equal("r-2", store.Tokens.RefreshToken);
            Assert.True(store.CanRefresh);
            var reloaded = NewStore(file);
            await reloaded.LoadAsync();
            Assert.Equal("a-2", reloaded.Tokens.AccessToken);
        }

        // 刷新网络失败 → uncertain 落盘（下次刷新直接不可用）。
        [Fact]
        public async Task TokenStore_ApiClient_RefreshNetworkFailure_MarksUncertainPersisted()
        {
            var file = new InMemorySecureFile();
            var store = NewStore(file);
            await store.SaveAsync(Tokens());
            var transport = new FakeHttpTransport();
            transport.Enqueue(Json(401,
                @"{""statusCode"":401,""data"":{""code"":""AUTH_TOKEN_EXPIRED"",""message"":""过期""}}"));
            transport.Enqueue(new TimeoutException("刷新超时"));
            var client = new ApiClient("https://sync.example.com", store, transport);

            var error = await Assert.ThrowsAsync<ApiError>(() => client.GetMeAsync());
            Assert.True(error.Ambiguous);
            Assert.False(store.CanRefresh);

            var reloaded = NewStore(file);
            await reloaded.LoadAsync();
            Assert.False(reloaded.CanRefresh);
        }
    }
}
