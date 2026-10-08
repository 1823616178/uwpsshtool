using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
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
    // fix/auth-audit：登录认证流程审查的回归用例——
    // 设备记录不再无限堆积、「退出所有设备」失败不再假装成功、本机改名同步到会话、
    // 登录页的被动登出提示、VALIDATION_ERROR 的具体原因、改密 / 注销表单校验。
    public class AuthAuditTests
    {
        private sealed class MemoryLastDevice : ILastDeviceStore
        {
            public string Value;
            public int Writes;

            public string Read()
            {
                return Value;
            }

            public void Write(string value)
            {
                Writes++;
                Value = value;
            }
        }

        private sealed class ThrowingLastDevice : ILastDeviceStore
        {
            public string Read()
            {
                throw new InvalidOperationException("settings unavailable");
            }

            public void Write(string value)
            {
                throw new InvalidOperationException("settings unavailable");
            }
        }

        private sealed class StubDevices : IDeviceDescriptorProvider
        {
            public DeviceDescriptorData GetDescriptor(string nameOverride = null)
            {
                return new DeviceDescriptorData { Name = "Lumia", Platform = "windows-mobile-arm", AppVersion = "test" };
            }
        }

        // 最小服务端：登录 / 注册返回可配置的 user/device；保险库不存在；设备 / 退出 / 注销按开关失败。
        private sealed class Server
        {
            public string UserId = "u1";
            public string DeviceId = "d-new";
            public bool FailDeleteDevice;
            public bool OfflineLogoutAll;
            public bool ExpiredSession;

            public HttpResponseData Handle(HttpRequestData request)
            {
                string url = request.Url ?? string.Empty;
                string method = request.Method ?? string.Empty;
                if ((url.EndsWith("/api/v1/auth/login", StringComparison.Ordinal)
                    || url.EndsWith("/api/v1/auth/register", StringComparison.Ordinal)) && method == "POST")
                {
                    return Json(200, AuthJson("a-login", "r-login", UserId, DeviceId));
                }
                if (url.EndsWith("/api/v1/auth/refresh", StringComparison.Ordinal))
                {
                    return Error(401, "AUTH_TOKEN_EXPIRED");
                }
                if (ExpiredSession)
                {
                    return Error(401, "AUTH_TOKEN_EXPIRED");
                }
                if (url.EndsWith("/api/v1/vault/key-envelope", StringComparison.Ordinal))
                {
                    return Error(404, "VAULT_NOT_FOUND");
                }
                if (url.Contains("/api/v1/devices/") && method == "DELETE")
                {
                    return FailDeleteDevice ? Error(500, "INTERNAL_ERROR") : Json(200, @"{""ok"":true}");
                }
                if (url.Contains("/api/v1/devices/") && method == "PATCH")
                {
                    return Json(200, @"{""device"":{""id"":""x"",""name"":""y""}}");
                }
                if (url.EndsWith("/api/v1/auth/logout-all", StringComparison.Ordinal))
                {
                    if (OfflineLogoutAll)
                    {
                        throw new HttpConnectionFailedException("connect failed", new Exception("offline"));
                    }
                    return Json(200, @"{""revokedDevices"":2,""revokedRefreshTokens"":2}");
                }
                if (url.EndsWith("/api/v1/auth/logout", StringComparison.Ordinal))
                {
                    return Json(200, @"{""ok"":true}");
                }
                if (url.EndsWith("/api/v1/me", StringComparison.Ordinal) && method == "DELETE")
                {
                    return Json(200, @"{""deleted"":true,""sessionsInvalidated"":true}");
                }
                return Error(404, "NOT_FOUND");
            }
        }

        private sealed class Harness
        {
            public readonly Server Server = new Server();
            public readonly FakeHttpTransport Transport = new FakeHttpTransport();
            public readonly InMemorySecureFile AuthFile = new InMemorySecureFile();
            public readonly AuthStore Auth;
            public readonly ApiClient Api;
            public readonly SyncCoordinator Sync;
            public readonly AuthService Service;

            public Harness(ILastDeviceStore lastDevice)
            {
                Auth = new AuthStore(AuthFile);
                Transport.Handler = Server.Handle;
                Api = new ApiClient("https://sync.example.test", Auth, Transport, retryLimit: 0,
                    sleep: ms => Task.CompletedTask);
                Sync = new SyncCoordinator(
                    Auth, new VaultCacheStore(new InMemorySecureFile()), Api, new FakeVaultCrypto(),
                    new StubDevices(), sleep: ms => Task.CompletedTask, lastDevice: lastDevice);
                Service = new AuthService(Auth, Api, new StubDevices());
            }

            public List<HttpRequestData> Deletes()
            {
                lock (Transport.Requests)
                {
                    return Transport.Requests
                        .Where(r => r.Method == "DELETE" && (r.Url ?? string.Empty).Contains("/api/v1/devices/"))
                        .ToList();
                }
            }
        }

        private static string AuthJson(string access, string refresh, string userId, string deviceId)
        {
            return @"{""accessToken"":""" + access + @""",""refreshToken"":""" + refresh + @""",""expiresIn"":900," +
                @"""user"":{""id"":""" + userId + @""",""email"":""a@b.c""}," +
                @"""device"":{""id"":""" + deviceId + @""",""name"":""Lumia""}}";
        }

        private static HttpResponseData Json(int status, string body)
        {
            return new HttpResponseData(
                status, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), body);
        }

        private static HttpResponseData Error(int status, string code)
        {
            return Json(status, @"{""statusCode"":" + status + @",""statusMessage"":""x"",""message"":""x""," +
                @"""data"":{""code"":""" + code + @""",""message"":""x""}}");
        }

        private static string Record(string user, string device)
        {
            return SyncCoordinator.EncodeDevice(user, device);
        }

        // ---------- 设备记录不再堆积 ----------

        [Fact]
        public async Task Login_RetiresPreviousDeviceOfSameUser()
        {
            var memory = new MemoryLastDevice { Value = Record("u1", "d-old") };
            var h = new Harness(memory);
            await h.Sync.InitializeAsync();

            await h.Sync.LoginAsync("a@b.c", "login-password");

            var deletes = h.Deletes();
            Assert.Single(deletes);
            Assert.EndsWith("/api/v1/devices/d-old", deletes[0].Url);
            Assert.Equal(Record("u1", "d-new"), memory.Value);
            Assert.True(h.Auth.Session.Authenticated);
        }

        [Fact]
        public async Task Register_RetiresPreviousDeviceOfSameUser()
        {
            var memory = new MemoryLastDevice { Value = Record("u1", "d-old") };
            var h = new Harness(memory);
            await h.Sync.InitializeAsync();

            await h.Sync.RegisterAsync("a@b.c", "login-password");

            Assert.Single(h.Deletes());
            Assert.Equal(Record("u1", "d-new"), memory.Value);
        }

        [Fact]
        public async Task Login_DifferentUser_DoesNotTouchOtherAccountsDevice()
        {
            var memory = new MemoryLastDevice { Value = Record("someone-else", "d-old") };
            var h = new Harness(memory);
            await h.Sync.InitializeAsync();

            await h.Sync.LoginAsync("a@b.c", "login-password");

            Assert.Empty(h.Deletes());
            Assert.Equal(Record("u1", "d-new"), memory.Value);
        }

        [Fact]
        public async Task Login_SameDeviceId_NoDelete()
        {
            var memory = new MemoryLastDevice { Value = Record("u1", "d-new") };
            var h = new Harness(memory);
            await h.Sync.InitializeAsync();

            await h.Sync.LoginAsync("a@b.c", "login-password");

            Assert.Empty(h.Deletes());
        }

        [Fact]
        public async Task Login_NoPreviousRecord_NoDelete_ButRemembers()
        {
            var memory = new MemoryLastDevice();
            var h = new Harness(memory);
            await h.Sync.InitializeAsync();

            await h.Sync.LoginAsync("a@b.c", "login-password");

            Assert.Empty(h.Deletes());
            Assert.Equal(Record("u1", "d-new"), memory.Value);
        }

        [Fact]
        public async Task Login_RetireFails_LoginStillSucceeds_AndOldRecordDropped()
        {
            var memory = new MemoryLastDevice { Value = Record("u1", "d-old") };
            var h = new Harness(memory);
            h.Server.FailDeleteDevice = true;
            await h.Sync.InitializeAsync();

            await h.Sync.LoginAsync("a@b.c", "login-password");

            Assert.True(h.Auth.Session.Authenticated);
            Assert.Equal(SyncPhase.Disabled, h.Sync.State.Phase);
            Assert.Equal(Record("u1", "d-new"), memory.Value);
        }

        [Fact]
        public async Task Login_StoreThrows_LoginUnaffected()
        {
            var h = new Harness(new ThrowingLastDevice());
            await h.Sync.InitializeAsync();

            await h.Sync.LoginAsync("a@b.c", "login-password");

            Assert.True(h.Auth.Session.Authenticated);
            Assert.Empty(h.Deletes());
        }

        [Fact]
        public async Task Login_WithoutStore_NoDelete()
        {
            var h = new Harness(null);
            await h.Sync.InitializeAsync();

            await h.Sync.LoginAsync("a@b.c", "login-password");

            Assert.Empty(h.Deletes());
        }

        [Fact]
        public async Task Initialize_WithRestoredSession_RemembersDevice_WithoutDeleting()
        {
            var memory = new MemoryLastDevice { Value = Record("u1", "d-older") };
            var h = new Harness(memory);
            await h.Auth.SaveAsync(new AuthTokenResponse
            {
                AccessToken = "a1",
                RefreshToken = "r1",
                ExpiresIn = 900,
                User = new AuthUserDto { Id = "u1", Email = "a@b.c" },
                Device = new AuthDeviceDto { Id = "d-current", Name = "Lumia" }
            });

            await h.Sync.InitializeAsync();

            Assert.Empty(h.Deletes());
            Assert.Equal(Record("u1", "d-current"), memory.Value);
        }

        [Fact]
        public async Task LogoutThenLogin_RetiresTheLoggedOutDevice()
        {
            var memory = new MemoryLastDevice();
            var h = new Harness(memory);
            await h.Sync.InitializeAsync();
            h.Server.DeviceId = "d-1";
            await h.Sync.LoginAsync("a@b.c", "login-password");
            await h.Sync.LogoutAsync();

            h.Server.DeviceId = "d-2";
            await h.Sync.LoginAsync("a@b.c", "login-password");

            var deletes = h.Deletes();
            Assert.Single(deletes);
            Assert.EndsWith("/api/v1/devices/d-1", deletes[0].Url);
            Assert.Equal(Record("u1", "d-2"), memory.Value);
        }

        [Fact]
        public async Task DeleteAccount_ForgetsDevice()
        {
            var memory = new MemoryLastDevice();
            var h = new Harness(memory);
            await h.Sync.InitializeAsync();
            await h.Sync.LoginAsync("a@b.c", "login-password");

            await h.Sync.DeleteAccountAsync("login-password");

            Assert.Null(memory.Value);
        }

        private sealed class DictionarySettings : ISettingsStore
        {
            public readonly Dictionary<string, object> Values = new Dictionary<string, object>();
            public bool Fail;

            public bool TryGet(string key, out object value)
            {
                if (Fail)
                {
                    throw new InvalidOperationException("settings unavailable");
                }
                return Values.TryGetValue(key, out value);
            }

            public void Set(string key, object value)
            {
                if (Fail)
                {
                    throw new InvalidOperationException("settings unavailable");
                }
                Values[key] = value;
            }
        }

        [Fact]
        public void SettingsLastDeviceStore_RoundTripAndClear()
        {
            var settings = new DictionarySettings();
            var store = new SettingsLastDeviceStore(settings);
            Assert.Null(store.Read());

            store.Write(Record("u1", "d1"));
            Assert.Equal(Record("u1", "d1"), store.Read());

            store.Write(null);
            Assert.Null(store.Read());
            Assert.Equal(string.Empty, settings.Values[SettingsLastDeviceStore.Key]);
        }

        [Fact]
        public void SettingsLastDeviceStore_NeverThrows()
        {
            var store = new SettingsLastDeviceStore(new DictionarySettings { Fail = true });
            Assert.Null(store.Read());
            store.Write("x");
        }

        [Fact]
        public void SettingsLastDeviceStore_KeyIsNotASyncedSetting()
        {
            // 键若进了 SettingsRepository 的定义表，就会随同步文档发到别的设备，
            // 别的设备登录时会把本机的设备记录撤销掉。
            Assert.DoesNotContain(SettingDefinitions.All, d => d.Key == SettingsLastDeviceStore.Key);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("no-separator")]
        [InlineData("\nd1")]
        [InlineData("u1\n")]
        public void DecodeDevice_RejectsMalformed(string value)
        {
            string user;
            string device;
            Assert.False(SyncCoordinator.TryDecodeDevice(value, out user, out device));
        }

        [Fact]
        public void DecodeDevice_RoundTrips()
        {
            string user;
            string device;
            Assert.True(SyncCoordinator.TryDecodeDevice(Record("u1", "d1"), out user, out device));
            Assert.Equal("u1", user);
            Assert.Equal("d1", device);
        }

        // ---------- 退出所有设备 ----------

        [Fact]
        public async Task LogoutAll_NotDelivered_KeepsSessionAndThrows()
        {
            var memory = new MemoryLastDevice();
            var h = new Harness(memory);
            await h.Sync.InitializeAsync();
            await h.Sync.LoginAsync("a@b.c", "login-password");
            h.Server.OfflineLogoutAll = true;

            await Assert.ThrowsAnyAsync<Exception>(() => h.Sync.LogoutAsync(all: true));

            Assert.True(h.Auth.Session.Authenticated);
            Assert.NotEqual(SyncPhase.SignedOut, h.Sync.State.Phase);
            Assert.Equal(Record("u1", "d-new"), memory.Value);
        }

        [Fact]
        public async Task LogoutAll_SessionAlreadyInvalid_ClearsLocal()
        {
            var h = new Harness(new MemoryLastDevice());
            await h.Sync.InitializeAsync();
            await h.Sync.LoginAsync("a@b.c", "login-password");
            h.Server.ExpiredSession = true;

            await Assert.ThrowsAsync<ApiError>(() => h.Sync.LogoutAsync(all: true));

            Assert.False(h.Auth.Session.Authenticated);
            Assert.Equal(SyncPhase.SignedOut, h.Sync.State.Phase);
        }

        [Fact]
        public async Task LogoutAll_Success_ClearsLocal_AndForgetsDevice()
        {
            var memory = new MemoryLastDevice();
            var h = new Harness(memory);
            await h.Sync.InitializeAsync();
            await h.Sync.LoginAsync("a@b.c", "login-password");

            await h.Sync.LogoutAsync(all: true);

            Assert.False(h.Auth.Session.Authenticated);
            Assert.Equal(SyncPhase.SignedOut, h.Sync.State.Phase);
            Assert.Null(memory.Value);
        }

        [Fact]
        public async Task Logout_SingleDevice_NotDelivered_StillClearsLocal()
        {
            var h = new Harness(new MemoryLastDevice());
            await h.Sync.InitializeAsync();
            await h.Sync.LoginAsync("a@b.c", "login-password");
            h.Transport.Handler = r =>
            {
                throw new HttpConnectionFailedException("connect failed", new Exception("offline"));
            };

            await Assert.ThrowsAnyAsync<Exception>(() => h.Sync.LogoutAsync());

            Assert.False(h.Auth.Session.Authenticated);
            Assert.Equal(SyncPhase.SignedOut, h.Sync.State.Phase);
        }

        // ---------- 设备改名 ----------

        [Fact]
        public async Task RenameCurrentDevice_UpdatesSessionName_Trimmed_AndPersisted()
        {
            var h = new Harness(null);
            await h.Sync.InitializeAsync();
            await h.Sync.LoginAsync("a@b.c", "login-password");

            await h.Service.RenameDeviceAsync("d-new", "  My Lumia  ");

            Assert.Equal("My Lumia", h.Auth.Session.DeviceName);
            var patch = h.Transport.Requests.Last(r => r.Method == "PATCH");
            Assert.Contains("\"My Lumia\"", patch.Body);
            var reloaded = await new AuthStore(h.AuthFile).LoadAsync();
            Assert.Equal("My Lumia", reloaded.DeviceName);
        }

        [Fact]
        public async Task RenameOtherDevice_LeavesSessionName()
        {
            var h = new Harness(null);
            await h.Sync.InitializeAsync();
            await h.Sync.LoginAsync("a@b.c", "login-password");

            await h.Service.RenameDeviceAsync("d-other", "Desktop");

            Assert.Equal("Lumia", h.Auth.Session.DeviceName);
        }

        [Fact]
        public async Task RenameDevice_TooLong_TruncatedToServerLimit()
        {
            var h = new Harness(null);
            await h.Sync.InitializeAsync();
            await h.Sync.LoginAsync("a@b.c", "login-password");

            await h.Service.RenameDeviceAsync("d-new", new string('x', 300));

            Assert.Equal(AuthService.MaxDeviceNameLength, h.Auth.Session.DeviceName.Length);
        }

        [Fact]
        public async Task RenameDevice_TruncationDoesNotSplitSurrogatePair()
        {
            var h = new Harness(null);
            await h.Sync.InitializeAsync();
            await h.Sync.LoginAsync("a@b.c", "login-password");
            string name = new string('x', AuthService.MaxDeviceNameLength - 1) + "\uD83D\uDE00" + "tail";

            await h.Service.RenameDeviceAsync("d-new", name);

            string stored = h.Auth.Session.DeviceName;
            Assert.Equal(AuthService.MaxDeviceNameLength - 1, stored.Length);
            Assert.False(char.IsHighSurrogate(stored[stored.Length - 1]));
        }

        [Fact]
        public async Task UpdateDeviceName_OnlyCurrentDevice_KeepsTokens()
        {
            var store = new AuthStore(new InMemorySecureFile());
            await store.SaveAsync(new AuthTokenResponse
            {
                AccessToken = "a1",
                RefreshToken = "r1",
                ExpiresIn = 900,
                User = new AuthUserDto { Id = "u1", Email = "a@b.c" },
                Device = new AuthDeviceDto { Id = "d1", Name = "Lumia" }
            });

            Assert.True(await store.UpdateDeviceNameAsync("d1", "Renamed"));
            Assert.False(await store.UpdateDeviceNameAsync("d1", "Renamed"));
            Assert.False(await store.UpdateDeviceNameAsync("d2", "Other"));
            Assert.Equal("Renamed", store.Session.DeviceName);
            Assert.Equal("a1", store.GetTokens().AccessToken);
        }

        // ---------- 登录页的被动登出提示 ----------

        [Fact]
        public void SignedOutNotice_AuthErrorPhase()
        {
            Assert.True(SyncRouting.ShouldShowSignedOutNotice(new SyncState { Phase = SyncPhase.AuthError }));
        }

        [Fact]
        public void SignedOutNotice_LoginPasswordChanged()
        {
            Assert.True(SyncRouting.ShouldShowSignedOutNotice(new SyncState
            {
                Phase = SyncPhase.SignedOut,
                MessageCode = SyncMessageCode.LoginPasswordChanged
            }));
        }

        [Fact]
        public void SignedOutNotice_NotForExplicitLogoutOrFreshStart()
        {
            Assert.False(SyncRouting.ShouldShowSignedOutNotice(null));
            Assert.False(SyncRouting.ShouldShowSignedOutNotice(new SyncState { Phase = SyncPhase.SignedOut }));
            Assert.False(SyncRouting.ShouldShowSignedOutNotice(new SyncState
            {
                Phase = SyncPhase.SignedOut,
                MessageCode = SyncMessageCode.Error,
                MessageError = new InvalidOperationException("x")
            }));
        }

        [Fact]
        public async Task SignedOutNotice_AfterServerRevokedSession()
        {
            var h = new Harness(null);
            await h.Sync.InitializeAsync();
            await h.Sync.LoginAsync("a@b.c", "login-password");
            h.Server.ExpiredSession = true;

            // 回到前台 / 重启时的初始化探测撞上服务端已吊销的会话（刷新同样 401）。
            await h.Sync.InitializeAsync();

            Assert.False(h.Auth.Session.Authenticated);
            Assert.True(SyncRouting.ShouldShowSignedOutNotice(h.Sync.State));
        }

        [Fact]
        public async Task SignedOutNotice_NotAfterExplicitLogoutOrLogin()
        {
            var h = new Harness(null);
            await h.Sync.InitializeAsync();
            await h.Sync.LoginAsync("a@b.c", "login-password");
            await h.Sync.LogoutAsync();
            Assert.False(SyncRouting.ShouldShowSignedOutNotice(h.Sync.State));

            await h.Sync.LoginAsync("a@b.c", "login-password");
            Assert.False(SyncRouting.ShouldShowSignedOutNotice(h.Sync.State));
        }

        // ---------- VALIDATION_ERROR 具体原因 ----------

        private static ApiError Validation(string message, bool fromServer)
        {
            return new ApiError(ApiErrorKind.Http, "VALIDATION_ERROR", message, 400,
                messageFromServer: fromServer);
        }

        [Fact]
        public void ValidationDetail_UsesServerMessage()
        {
            Assert.Equal("请输入有效邮箱", LoginErrorKeys.ValidationDetail(Validation("  请输入有效邮箱 ", true)));
        }

        [Fact]
        public void ValidationDetail_IgnoresNonServerEmptyLongOrOtherCodes()
        {
            Assert.Null(LoginErrorKeys.ValidationDetail(null));
            Assert.Null(LoginErrorKeys.ValidationDetail(Validation("HTTP 400", false)));
            Assert.Null(LoginErrorKeys.ValidationDetail(Validation("   ", true)));
            Assert.Null(LoginErrorKeys.ValidationDetail(
                Validation(new string('x', LoginErrorKeys.MaxServerDetailLength + 1), true)));
            Assert.Null(LoginErrorKeys.ValidationDetail(
                new ApiError(ApiErrorKind.Http, "AUTH_INVALID_CREDENTIALS", "x", 401, messageFromServer: true)));
        }

        // ---------- 改密 / 注销表单 ----------

        [Fact]
        public void ChangePassword_Validation()
        {
            Assert.Equal(AccountFormValidator.CurrentPasswordRequiredKey,
                AccountFormValidator.ValidateChangePassword("", "0123456789", "0123456789"));
            Assert.Equal(AccountFormValidator.NewPasswordRequiredKey,
                AccountFormValidator.ValidateChangePassword("old", "", ""));
            Assert.Equal(AccountFormValidator.NewPasswordTooShortKey,
                AccountFormValidator.ValidateChangePassword("old", "short", "short"));
            Assert.Equal(AccountFormValidator.PasswordMismatchKey,
                AccountFormValidator.ValidateChangePassword("old", "0123456789", "0123456780"));
            Assert.Null(AccountFormValidator.ValidateChangePassword("old", "0123456789", "0123456789"));
        }

        [Theory]
        [InlineData("DELETE", true)]
        [InlineData("DELETE ", true)]
        [InlineData(" DELETE\t", true)]
        [InlineData("delete", false)]
        [InlineData("Delete", false)]
        [InlineData("DELET", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void DeleteConfirm_TrimmedCaseSensitive(string text, bool expected)
        {
            Assert.Equal(expected, AccountFormValidator.IsDeleteConfirmed(text));
        }

        [Fact]
        public void DeleteAccount_Validation()
        {
            Assert.Equal(AccountFormValidator.DeletePasswordRequiredKey,
                AccountFormValidator.ValidateDeleteAccount("", "DELETE"));
            Assert.Equal(AccountFormValidator.DeleteConfirmRequiredKey,
                AccountFormValidator.ValidateDeleteAccount("pw", "delete"));
            Assert.Null(AccountFormValidator.ValidateDeleteAccount("pw", "DELETE "));
        }

        public static IEnumerable<object[]> ReswFiles()
        {
            yield return new object[] { "zh-cn" };
            yield return new object[] { "en-us" };
        }

        [Theory]
        [MemberData(nameof(ReswFiles))]
        public void NewKeys_ExistInResw(string lang)
        {
            var names = LoadResw(lang);
            var keys = new List<string>(AccountFormValidator.AllErrorKeys)
            {
                LoginErrorKeys.ValidationDetailKey,
                "AccountSync_LoggingOut",
            };
            foreach (string key in keys)
            {
                Assert.True(names.ContainsKey(key), lang + " missing " + key);
                Assert.False(string.IsNullOrWhiteSpace(names[key]), lang + " empty " + key);
            }
            string detail = names[LoginErrorKeys.ValidationDetailKey];
            Assert.Contains("{0}", detail);
            Assert.DoesNotContain("{1}", detail);
        }

        private static Dictionary<string, string> LoadResw(string lang)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            string path = null;
            while (dir != null && path == null)
            {
                string candidate = Path.Combine(dir.FullName, "src", "SshTool.App", "Strings", lang, "Resources.resw");
                if (File.Exists(candidate))
                {
                    path = candidate;
                }
                dir = dir.Parent;
            }
            Assert.NotNull(path);
            return XDocument.Load(path).Root.Elements("data")
                .Where(e => e.Attribute("name") != null)
                .ToDictionary(e => e.Attribute("name").Value, e => (string)e.Element("value"));
        }
    }
}
