using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.Core.Storage;
using SshTool.Core.Sync.Api;
using SshTool.Core.Sync.Api.Dtos;
using SshTool.Core.Sync.Auth;
using SshTool.Core.Tests.Fakes;
using Xunit;

namespace SshTool.Core.Tests.Sync.Auth
{
    // S08 验收：重复登录保护、登出（服务端失败也清本地）、改密后清本地、
    // 撤销本机被拒，以及注册/注销/设备列表/改名透传。
    public class AuthServiceTests
    {
        private static readonly DateTimeOffset FixedNow =
            new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero);

        private const string TokenResponseJson =
            @"{""accessToken"":""a1"",""refreshToken"":""r1"",""expiresIn"":3600,"
            + @"""user"":{""id"":""u1"",""email"":""a@b.c""},""device"":{""id"":""d1"",""name"":""TestDevice""}}";

        private sealed class FakeDevices : IDeviceDescriptorProvider
        {
            public string LastOverride;

            public DeviceDescriptorData GetDescriptor(string nameOverride = null)
            {
                LastOverride = nameOverride;
                return new DeviceDescriptorData
                {
                    Name = string.IsNullOrWhiteSpace(nameOverride) ? "TestDevice" : nameOverride,
                    Platform = "windows-uwp-x64",
                    AppVersion = "0.1.0.0"
                };
            }
        }

        private sealed class ServiceContext
        {
            public AuthStore Store;
            public FakeHttpTransport Transport;
            public FakeDevices Devices;
            public AuthService Service;
        }

        private static ServiceContext NewService()
        {
            var file = new InMemorySecureFile();
            var store = new AuthStore(file, () => FixedNow);
            var transport = new FakeHttpTransport();
            var devices = new FakeDevices();
            var api = new ApiClient("https://sync.example.com", store, transport);
            return new ServiceContext
            {
                Store = store,
                Transport = transport,
                Devices = devices,
                Service = new AuthService(store, api, devices)
            };
        }

        private static AuthTokenResponse SavedTokens()
        {
            return new AuthTokenResponse
            {
                AccessToken = "a1",
                RefreshToken = "r1",
                ExpiresIn = 3600,
                User = new AuthUserDto { Id = "u1", Email = "a@b.c" },
                Device = new AuthDeviceDto { Id = "d1", Name = "TestDevice" }
            };
        }

        private static HttpResponseData Json(int status, string body)
        {
            return new HttpResponseData(
                status,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                body);
        }

        private static string BodyOf(HttpRequestData request)
        {
            return request.Body ?? string.Empty;
        }

        [Fact]
        public async Task Register_SavesSessionAndSendsDeviceDescriptor()
        {
            var context = NewService();
            context.Transport.Enqueue(Json(200, TokenResponseJson));

            var session = await context.Service.RegisterAsync("a@b.c", "pw12345678", null, "我的Lumia");

            Assert.True(session.Authenticated);
            Assert.Equal("u1", session.UserId);
            Assert.Equal("d1", session.DeviceId);
            Assert.Equal("我的Lumia", context.Devices.LastOverride);
            Assert.Single(context.Transport.Requests);
            Assert.Contains("我的Lumia", BodyOf(context.Transport.Requests[0]));
            Assert.Contains("windows-uwp-x64", BodyOf(context.Transport.Requests[0]));
        }

        [Fact]
        public async Task Login_SavesSession()
        {
            var context = NewService();
            context.Transport.Enqueue(Json(200, TokenResponseJson));

            var session = await context.Service.LoginAsync("a@b.c", "pw12345678");

            Assert.True(session.Authenticated);
            Assert.Equal("u1", context.Store.Session.UserId);
            Assert.Equal("d1", context.Store.Session.DeviceId);
        }

        [Fact]
        public async Task Login_WhenAlreadySignedIn_RejectsWithoutNetworkCall()
        {
            var context = NewService();
            await context.Store.SaveAsync(SavedTokens());

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.Service.LoginAsync("a@b.c", "other"));
            Assert.Empty(context.Transport.Requests);
            Assert.True(context.Store.Session.Authenticated);
        }

        [Fact]
        public async Task Register_WhenAlreadySignedIn_RejectsWithoutNetworkCall()
        {
            var context = NewService();
            await context.Store.SaveAsync(SavedTokens());

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.Service.RegisterAsync("a@b.c", "pw12345678"));
            Assert.Empty(context.Transport.Requests);
        }

        [Fact]
        public async Task Logout_Success_ClearsLocal()
        {
            var context = NewService();
            await context.Store.SaveAsync(SavedTokens());
            context.Transport.Enqueue(Json(200, @"{""ok"":true}"));

            await context.Service.LogoutAsync();

            Assert.False(context.Store.Session.Authenticated);
            Assert.Single(context.Transport.Requests);
        }

        [Fact]
        public async Task Logout_ServerFailure_StillClearsLocal()
        {
            var context = NewService();
            await context.Store.SaveAsync(SavedTokens());
            context.Transport.Enqueue(Json(500, null));

            await Assert.ThrowsAsync<ApiError>(() => context.Service.LogoutAsync());
            Assert.False(context.Store.Session.Authenticated);
            Assert.Null(context.Store.GetTokens());
        }

        [Fact]
        public async Task Logout_WhenNotSignedIn_ClearsWithoutNetworkCall()
        {
            var context = NewService();
            await context.Service.LogoutAsync();
            Assert.Empty(context.Transport.Requests);
            Assert.False(context.Store.Session.Authenticated);
        }

        [Fact]
        public async Task LogoutAll_ServerFailure_StillClearsLocal()
        {
            var context = NewService();
            await context.Store.SaveAsync(SavedTokens());
            context.Transport.Enqueue(new TimeoutException("到时"));

            await Assert.ThrowsAsync<ApiError>(() => context.Service.LogoutAllAsync());
            Assert.False(context.Store.Session.Authenticated);
        }

        [Fact]
        public async Task ChangeLoginPassword_Success_ClearsLocal()
        {
            var context = NewService();
            await context.Store.SaveAsync(SavedTokens());
            context.Transport.Enqueue(Json(200, @"{}"));

            await context.Service.ChangeLoginPasswordAsync("old-pw", "new-pw");

            Assert.False(context.Store.Session.Authenticated);
            Assert.Null(context.Store.GetTokens());
        }

        [Fact]
        public async Task ChangeLoginPassword_Failure_KeepsSession()
        {
            var context = NewService();
            await context.Store.SaveAsync(SavedTokens());
            context.Transport.Enqueue(Json(401,
                @"{""statusCode"":401,""data"":{""code"":""AUTH_CURRENT_PASSWORD_INVALID"",""message"":""密码错误""}}"));

            var error = await Assert.ThrowsAsync<ApiError>(
                () => context.Service.ChangeLoginPasswordAsync("wrong", "new-pw"));
            Assert.Equal("AUTH_CURRENT_PASSWORD_INVALID", error.Code);
            Assert.True(context.Store.Session.Authenticated);
            Assert.Equal("a1", context.Store.GetTokens().AccessToken);
        }

        [Fact]
        public async Task DeleteAccount_Success_ClearsLocal()
        {
            var context = NewService();
            await context.Store.SaveAsync(SavedTokens());
            context.Transport.Enqueue(Json(200, @"{""deleted"":true,""sessionsInvalidated"":true}"));

            await context.Service.DeleteAccountAsync("login-pw");

            Assert.False(context.Store.Session.Authenticated);
            Assert.Contains(@"""DELETE""", BodyOf(context.Transport.Requests[0]));
        }

        [Fact]
        public async Task RevokeDevice_CurrentDevice_RejectedWithoutNetworkCall()
        {
            var context = NewService();
            await context.Store.SaveAsync(SavedTokens());

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.Service.RevokeDeviceAsync("d1"));
            Assert.Empty(context.Transport.Requests);
            Assert.True(context.Store.Session.Authenticated);
        }

        [Fact]
        public async Task RevokeDevice_OtherDevice_SendsDelete()
        {
            var context = NewService();
            await context.Store.SaveAsync(SavedTokens());
            context.Transport.Enqueue(Json(200, @"{""ok"":true}"));

            await context.Service.RevokeDeviceAsync("d2");

            Assert.Single(context.Transport.Requests);
            Assert.Equal("DELETE", context.Transport.Requests[0].Method);
            Assert.EndsWith("/api/v1/devices/d2", context.Transport.Requests[0].Url);
        }

        [Fact]
        public async Task RenameDevice_SendsPatch()
        {
            var context = NewService();
            await context.Store.SaveAsync(SavedTokens());
            context.Transport.Enqueue(Json(200, @"{""device"":{""id"":""d2"",""name"":""新名""}}"));

            await context.Service.RenameDeviceAsync("d2", "新名");

            Assert.Single(context.Transport.Requests);
            Assert.Equal("PATCH", context.Transport.Requests[0].Method);
            Assert.Contains("新名", BodyOf(context.Transport.Requests[0]));
        }

        [Fact]
        public async Task ListDevices_ReturnsItems()
        {
            var context = NewService();
            await context.Store.SaveAsync(SavedTokens());
            context.Transport.Enqueue(Json(200,
                @"{""items"":[{""id"":""d1"",""name"":""A"",""platform"":""windows-mobile-arm"","
                + @"""appVersion"":""0.1.0.0"",""createdAt"":""2026-09-18T00:00:00.000Z"","
                + @"""lastSeenAt"":""2026-09-19T00:00:00.000Z"",""current"":true}]}"));

            var list = await context.Service.ListDevicesAsync();

            Assert.Single(list.Items);
            Assert.Equal("d1", list.Items[0].Id);
            Assert.True(list.Items[0].Current);
        }
    }
}
