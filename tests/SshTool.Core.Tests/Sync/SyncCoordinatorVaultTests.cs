using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SshTool.Core.Common;
using SshTool.Core.Storage;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Api;
using SshTool.Core.Sync.Api.Dtos;
using SshTool.Core.Sync.Auth;
using SshTool.Core.Sync.Vault;
using SshTool.Core.Tests.Fakes;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // S11 验收：§10.1 Coordinator 1–5（第一部分范围：账号与保险库流程，不含 S12a/b、S13、S14）。
    // Setup/Unlock 成功后只记 dirty/就绪并转 idle/ready，实际上传由 S12a 的 SyncNow 完成。
    public class SyncCoordinatorVaultTests
    {
        private sealed class CapturingLogger : ILogger
        {
            public readonly List<string> Lines = new List<string>();

            public void Log(LogLevel level, string tag, string message)
            {
                Lines.Add(level + " [" + tag + "] " + message);
            }
        }

        private sealed class StubDevices : IDeviceDescriptorProvider
        {
            public DeviceDescriptorData GetDescriptor(string nameOverride = null)
            {
                return new DeviceDescriptorData
                {
                    Name = string.IsNullOrWhiteSpace(nameOverride) ? "Lumia-Test" : nameOverride,
                    Platform = "windows-uwp-x64",
                    AppVersion = "0.1.0-test"
                };
            }
        }

        // 桌面端 MockSyncService 的 Lumia 版：走真实 ApiClient + FakeHttpTransport，
        // 在传输层实现最小服务端（认证/保险库分组），加密与幂等语义与桌面 mock 一致。
        private sealed class MockSyncServer
        {
            public const string VaultId = "vault-test-1";
            public const string UserId = "u1";

            // GET/POST/DELETE 响应中的保险库 id（默认与 VaultId 一致；
            // ProbeVault 换库测试可改为不同 id）。
            public string ActiveVaultId = VaultId;

            public bool Offline;
            public bool FailLogout;
            public int LoseVaultResponses;
            public int VaultCreateCommits;
            public JObject VaultEnvelope;
            public readonly List<HttpRequestData> Requests = new List<HttpRequestData>();

            private readonly Dictionary<string, VaultCreateRecord> _vaultCreates =
                new Dictionary<string, VaultCreateRecord>(StringComparer.Ordinal);

            private sealed class VaultCreateRecord
            {
                public string Body;
                public string Response;
            }

            public HttpResponseData Handle(HttpRequestData request)
            {
                lock (Requests)
                {
                    Requests.Add(request);
                }
                if (Offline)
                {
                    throw new InvalidOperationException("mock offline");
                }
                string url = request.Url ?? "";
                string method = request.Method ?? "";
                if (url.EndsWith("/api/v1/auth/register", StringComparison.Ordinal) && method == "POST")
                {
                    return Json(200, AuthJson("a-reg", "r-reg"));
                }
                if (url.EndsWith("/api/v1/auth/login", StringComparison.Ordinal) && method == "POST")
                {
                    return Json(200, AuthJson("a-login", "r-login"));
                }
                if ((url.EndsWith("/api/v1/auth/logout-all", StringComparison.Ordinal)
                    || url.EndsWith("/api/v1/auth/logout", StringComparison.Ordinal)) && method == "POST")
                {
                    if (FailLogout)
                    {
                        return ApiErrorJson(500, "LOGOUT_FAILED", "mock logout failure");
                    }
                    if (url.EndsWith("/logout-all", StringComparison.Ordinal))
                    {
                        return Json(200, @"{""revokedDevices"":1,""revokedRefreshTokens"":1}");
                    }
                    return Json(200, @"{""ok"":true}");
                }
                if (url.EndsWith("/api/v1/auth/change-password", StringComparison.Ordinal) && method == "POST")
                {
                    return Json(200, @"{""ok"":true,""reauthenticationRequired"":true}");
                }
                if (url.EndsWith("/api/v1/me", StringComparison.Ordinal) && method == "DELETE")
                {
                    return Json(200, @"{""deleted"":true,""sessionsInvalidated"":true}");
                }
                if (url.EndsWith("/api/v1/vault", StringComparison.Ordinal) && method == "POST")
                {
                    string key = HeaderOf(request, "Idempotency-Key");
                    string body = request.Body ?? "";
                    VaultCreateRecord previous;
                    string response;
                    if (key != null && _vaultCreates.TryGetValue(key, out previous))
                    {
                        if (!string.Equals(previous.Body, body, StringComparison.Ordinal))
                        {
                            return ApiErrorJson(409, "IDEMPOTENCY_KEY_REUSED", "vault body changed");
                        }
                        response = previous.Response;
                    }
                    else
                    {
                        var envelope = JObject.Parse(body);
                        VaultEnvelope = envelope;
                        response = new JObject
                        {
                            ["id"] = ActiveVaultId,
                            ["keyVersion"] = envelope["keyVersion"]
                        }.ToString();
                        if (key != null)
                        {
                            _vaultCreates[key] = new VaultCreateRecord { Body = body, Response = response };
                        }
                        VaultCreateCommits++;
                    }
                    if (LoseVaultResponses > 0)
                    {
                        LoseVaultResponses--;
                        throw new InvalidOperationException("mock vault response lost");
                    }
                    return Json(200, response);
                }
                if (url.EndsWith("/api/v1/vault/key-envelope", StringComparison.Ordinal) && method == "GET")
                {
                    if (VaultEnvelope == null)
                    {
                        return ApiErrorJson(404, "VAULT_NOT_FOUND", "vault not found");
                    }
                    var merged = new JObject { ["id"] = ActiveVaultId };
                    foreach (var property in VaultEnvelope.Properties())
                    {
                        merged[property.Name] = property.Value.DeepClone();
                    }
                    return Json(200, merged.ToString());
                }
                if (url.EndsWith("/api/v1/vault", StringComparison.Ordinal) && method == "DELETE")
                {
                    VaultEnvelope = null;
                    return Json(200, @"{""deleted"":true,""vaultId"":""" + ActiveVaultId + @"""}");
                }
                return ApiErrorJson(404, "NOT_FOUND", "not implemented: " + method + " " + url);
            }

            public void ServeEnvelope(JObject envelope)
            {
                VaultEnvelope = envelope;
            }

            public int VaultPosts()
            {
                return Requests.Count(r => r.Method == "POST" && r.Url.EndsWith("/vault", StringComparison.Ordinal));
            }

            private static string AuthJson(string access, string refresh)
            {
                return @"{""accessToken"":""" + access + @""",""refreshToken"":""" + refresh
                    + @""",""expiresIn"":3600,""user"":{""id"":""" + UserId
                    + @""",""email"":""a@b.c""},""device"":{""id"":""d-1"",""name"":""Lumia""}}";
            }

            private static HttpResponseData Json(int status, string body)
            {
                return new HttpResponseData(
                    status,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "x-request-id", "mock-ok" }
                    },
                    body);
            }

            private static HttpResponseData ApiErrorJson(int status, string code, string message)
            {
                return new HttpResponseData(
                    status,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "x-request-id", "mock-" + code.ToLowerInvariant() }
                    },
                    @"{""statusCode"":" + status + @",""data"":{""code"":""" + code
                    + @""",""message"":""" + message + @"""}}");
            }

            private static string HeaderOf(HttpRequestData request, string name)
            {
                foreach (var header in request.Headers)
                {
                    if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return header.Value;
                    }
                }
                return null;
            }
        }

        private sealed class Harness
        {
            public readonly MockSyncServer Server = new MockSyncServer();
            public readonly FakeHttpTransport Transport = new FakeHttpTransport();
            public readonly InMemorySecureFile AuthFile = new InMemorySecureFile();
            public readonly InMemorySecureFile VaultFile = new InMemorySecureFile();
            public readonly AuthStore Auth;
            public readonly VaultCacheStore Vault;
            public readonly ApiClient Api;
            public readonly FakeVaultCrypto Crypto = new FakeVaultCrypto();
            public readonly CapturingLogger Logger = new CapturingLogger();
            public readonly SyncCoordinator Coordinator;
            public readonly List<SyncState> States = new List<SyncState>();

            public Harness()
            {
                Auth = new AuthStore(AuthFile);
                Vault = new VaultCacheStore(VaultFile, Logger);
                Transport.Handler = Server.Handle;
                // retryLimit 0：本类只测单次语义，重试退避是 S13 范围；同时让离线用例无延迟。
                Api = new ApiClient("https://sync.example.test", Auth, Transport, retryLimit: 0);
                Coordinator = new SyncCoordinator(Auth, Vault, Api, Crypto, new StubDevices(), Logger);
                Coordinator.StateChanged += s => States.Add(s);
            }
        }

        private static async Task<Harness> RegisteredAsync(Harness h)
        {
            await h.Coordinator.InitializeAsync();
            Assert.Equal(SyncPhase.SignedOut, h.Coordinator.State.Phase);
            await h.Coordinator.RegisterAsync("a@b.c", "login-password");
            return h;
        }

        // ---------- §10.1 Coordinator 1：新设备发现已有保险库并解锁 ----------

        [Fact]
        public async Task Coordinator01_NewDeviceDiscoversVaultAndUnlocks()
        {
            var owner = new Harness();
            await RegisteredAsync(owner);
            // 新账号无云端保险库时保持 missing（用例 2 的一部分，此处是前置条件）。
            Assert.Equal(VaultStatus.Missing, owner.Coordinator.State.Vault);
            string recoveryKey = await owner.Coordinator.SetupVaultAsync("sync-password");
            Assert.Matches("^SPM1-", recoveryKey);
            string ownerKey = owner.Vault.State.VaultKeyBase64;

            // 新设备：全新文件 + 同一服务端 + 同一用户注册 → 探测到保险库并锁定。
            var fresh = new Harness();
            fresh.Server.VaultEnvelope = (JObject)owner.Server.VaultEnvelope.DeepClone();
            await RegisteredAsync(fresh);
            Assert.Equal(SyncPhase.Locked, fresh.Coordinator.State.Phase);
            Assert.Equal(VaultStatus.Locked, fresh.Coordinator.State.Vault);
            Assert.Equal(1, fresh.Coordinator.State.KeyVersion);
            Assert.Equal(MockSyncServer.VaultId, fresh.Vault.State.VaultId);
            Assert.Null(fresh.Vault.State.VaultKeyBase64);

            await fresh.Coordinator.UnlockVaultAsync("sync-password", VaultUnlockMethod.Password);
            Assert.Equal(SyncPhase.Idle, fresh.Coordinator.State.Phase);
            Assert.Equal(VaultStatus.Ready, fresh.Coordinator.State.Vault);
            Assert.Equal(ownerKey, fresh.Vault.State.VaultKeyBase64);
        }

        [Fact]
        public async Task Coordinator01_UnlockWithRecoveryKey()
        {
            var owner = new Harness();
            await RegisteredAsync(owner);
            string recoveryKey = await owner.Coordinator.SetupVaultAsync("sync-password");

            var fresh = new Harness();
            fresh.Server.VaultEnvelope = (JObject)owner.Server.VaultEnvelope.DeepClone();
            await RegisteredAsync(fresh);
            await fresh.Coordinator.UnlockVaultAsync(recoveryKey, VaultUnlockMethod.Recovery);
            Assert.Equal(VaultStatus.Ready, fresh.Coordinator.State.Vault);
            Assert.Equal(owner.Vault.State.VaultKeyBase64, fresh.Vault.State.VaultKeyBase64);
        }

        [Fact]
        public async Task Coordinator01_UnlockWrongPassword_ThrowsAndStaysLocked()
        {
            var owner = new Harness();
            await RegisteredAsync(owner);
            await owner.Coordinator.SetupVaultAsync("sync-password");

            var fresh = new Harness();
            fresh.Server.VaultEnvelope = (JObject)owner.Server.VaultEnvelope.DeepClone();
            await RegisteredAsync(fresh);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fresh.Coordinator.UnlockVaultAsync("wrong-password", VaultUnlockMethod.Password));
            Assert.Equal("同步密码不正确", error.Message);
            Assert.Equal(VaultStatus.Locked, fresh.Coordinator.State.Vault);
            Assert.Null(fresh.Vault.State.VaultKeyBase64);
        }

        // ---------- §10.1 Coordinator 2：新账号无云端保险库时保持 missing ----------

        [Fact]
        public async Task Coordinator02_NoRemoteVault_StaysMissing()
        {
            var h = new Harness();
            await RegisteredAsync(h);
            Assert.Equal(SyncPhase.Disabled, h.Coordinator.State.Phase);
            Assert.Equal(VaultStatus.Missing, h.Coordinator.State.Vault);
            Assert.Null(h.Vault.State.VaultId);
        }

        // ---------- §10.1 Coordinator 3：探测离线不影响登录/初始化 ----------

        [Fact]
        public async Task Coordinator03_ProbeOffline_DoesNotRejectInitialize()
        {
            var h = new Harness();
            await RegisteredAsync(h);
            Assert.Equal(VaultStatus.Missing, h.Coordinator.State.Vault);

            h.Server.Offline = true;
            var restarted = new SyncCoordinator(h.Auth, h.Vault, h.Api, h.Crypto, new StubDevices(), h.Logger);
            await restarted.InitializeAsync();

            Assert.True(restarted.State.Phase == SyncPhase.Offline || restarted.State.Phase == SyncPhase.Error);
            Assert.Contains("无法连接同步服务器", restarted.State.Message);
            // 登录会话不受影响。
            Assert.True(h.Auth.Session.Authenticated);
            Assert.Equal(MockSyncServer.UserId, h.Auth.Session.UserId);
        }

        [Fact]
        public async Task Coordinator03_ProbeVault_ThrowsButSafelySwallows()
        {
            var h = new Harness();
            await RegisteredAsync(h);
            h.Server.Offline = true;
            await Assert.ThrowsAsync<ApiError>(() => h.Coordinator.ProbeVaultAsync());
            // Initialize 路径（Safely）则不抛。
            var restarted = new SyncCoordinator(h.Auth, h.Vault, h.Api, h.Crypto, new StubDevices(), h.Logger);
            await restarted.InitializeAsync();
            Assert.True(h.Auth.Session.Authenticated);
        }

        // ---------- §10.1 Coordinator 4：创建保险库并就绪（上传由 S12a 接管） ----------

        [Fact]
        public async Task Coordinator04_SetupVault_CreatesAndMarksDirty()
        {
            var h = new Harness();
            await RegisteredAsync(h);
            h.States.Clear();

            string recoveryKey = await h.Coordinator.SetupVaultAsync("sync-password");

            Assert.Matches("^SPM1-", recoveryKey);
            Assert.NotNull(h.Server.VaultEnvelope);
            Assert.Equal(1, h.Server.VaultPosts());
            var post = h.Server.Requests.First(r => r.Method == "POST" && r.Url.EndsWith("/vault", StringComparison.Ordinal));
            Assert.NotNull(post.Body);
            Assert.Contains("\"keyVersion\":1", post.Body);
            bool hasIdempotency = false;
            foreach (var header in post.Headers)
            {
                if (string.Equals(header.Key, "Idempotency-Key", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(header.Value))
                {
                    hasIdempotency = true;
                }
            }
            Assert.True(hasIdempotency);

            var cache = h.Vault.State;
            Assert.Equal(MockSyncServer.VaultId, cache.VaultId);
            Assert.NotNull(cache.VaultKeyBase64);
            Assert.Equal(1, cache.KeyVersion);
            Assert.Equal("0", cache.Revision);
            Assert.True(cache.Preferences.Enabled);
            Assert.True(cache.Dirty);
            Assert.Null(cache.PendingVaultSetup);

            var state = h.Coordinator.State;
            Assert.Equal(SyncPhase.Idle, state.Phase);
            Assert.Equal(VaultStatus.Ready, state.Vault);
            Assert.Equal("0", state.Revision);
            Assert.True(state.Dirty);
            Assert.True(h.States.Count > 0);
            Assert.Equal(SyncPhase.Idle, h.States[h.States.Count - 1].Phase);
        }

        // ---------- §10.1 Coordinator 5：响应丢失 + 重启 + 重试复用同一材料 ----------

        [Fact]
        public async Task Coordinator05_LostResponse_Restart_RetryReusesSameMaterials()
        {
            var h = new Harness();
            await RegisteredAsync(h);
            h.Server.LoseVaultResponses = 1;

            var error = await Assert.ThrowsAsync<ApiError>(
                () => h.Coordinator.SetupVaultAsync("original-sync-password"));
            Assert.True(error.Ambiguous);

            // pending 已落盘（先落盘再发请求，踩坑 #12）。
            var persisted = await new VaultCacheStore(h.VaultFile).LoadAsync();
            Assert.NotNull(persisted.PendingVaultSetup);
            Assert.NotNull(persisted.PendingVaultSetup.IdempotencyKey);
            Assert.NotNull(persisted.PendingVaultSetup.VaultKeyBase64);
            Assert.Matches("^SPM1-", persisted.PendingVaultSetup.RecoveryKey);
            Assert.Equal(1, h.Server.VaultCreateCommits);
            Assert.Null(h.Vault.State.VaultId);

            // 重启（同一文件）后 pending 仍在。
            var restartedVault = new VaultCacheStore(h.VaultFile);
            var reloaded = await restartedVault.LoadAsync();
            Assert.Equal(persisted.PendingVaultSetup.IdempotencyKey, reloaded.PendingVaultSetup.IdempotencyKey);

            var restarted = new SyncCoordinator(h.Auth, restartedVault, h.Api, h.Crypto, new StubDevices(), h.Logger);
            // 换一个密码重试：必须复用 journal，而不是重新生成。
            string recoveryKey = await restarted.SetupVaultAsync("ignored-new-password");

            Assert.Equal(persisted.PendingVaultSetup.RecoveryKey, recoveryKey);
            Assert.Equal(1, h.Server.VaultCreateCommits);
            var posts = h.Server.Requests
                .Where(r => r.Method == "POST" && r.Url.EndsWith("/vault", StringComparison.Ordinal)).ToList();
            Assert.True(posts.Count >= 2);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var bodies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var post in posts)
            {
                foreach (var header in post.Headers)
                {
                    if (string.Equals(header.Key, "Idempotency-Key", StringComparison.OrdinalIgnoreCase))
                    {
                        keys.Add(header.Value);
                    }
                }
                bodies.Add(post.Body);
            }
            Assert.Single(keys);
            Assert.Single(bodies);
            var done = restartedVault.State;
            Assert.Equal(MockSyncServer.VaultId, done.VaultId);
            Assert.Equal(persisted.PendingVaultSetup.VaultKeyBase64, done.VaultKeyBase64);
            Assert.Null(done.PendingVaultSetup);
        }

        // ---------- S11 其他第一部分流程 ----------

        [Fact]
        public async Task ProbeVault_VaultIdChanged_ResetsKeyAndBaseline()
        {
            var h = new Harness();
            await RegisteredAsync(h);
            await h.Coordinator.SetupVaultAsync("sync-password");
            Assert.Equal(SyncPhase.Idle, h.Coordinator.State.Phase);

            // 云端换了另一个保险库（不同 id）：旧 key 与基线失效。
            var crypto2 = new FakeVaultCrypto();
            var setup2 = await crypto2.CreateAsync("other-password", 1);
            h.Server.ActiveVaultId = "vault-test-2";
            h.Server.ServeEnvelope(setup2.Envelope.ToJson());
            await h.Coordinator.ProbeVaultAsync();

            Assert.Equal("vault-test-2", h.Vault.State.VaultId);

            Assert.Null(h.Vault.State.VaultKeyBase64);
            Assert.Equal("0", h.Vault.State.Revision);
            Assert.Null(h.Vault.State.BaseDocument);
            Assert.False(h.Vault.State.Dirty);
            Assert.Equal(SyncPhase.Locked, h.Coordinator.State.Phase);
            Assert.Equal(VaultStatus.Locked, h.Coordinator.State.Vault);
        }

        [Fact]
        public async Task Unlock_InvalidEnvelope_Throws()
        {
            // 踩坑 #1：KDF 参数必须读信封并校验范围，越界信封直接拒绝。
            var h = new Harness();
            await RegisteredAsync(h);
            var bad = new VaultKeyEnvelope
            {
                KeyVersion = 1,
                PasswordWrappedKey = "cHcx",
                PasswordWrapNonce = "bm9uY2U",
                RecoveryWrappedKey = "cmMx",
                RecoveryWrapNonce = "bm9uY2Uy",
                KdfSalt = "c2FsdA",
                KdfAlgorithm = "argon2id",
                KdfMemory = 100,
                KdfIterations = 3,
                KdfParallelism = 1
            };
            h.Server.ServeEnvelope(bad.ToJson());
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => h.Coordinator.UnlockVaultAsync("sync-password", VaultUnlockMethod.Password));
            Assert.Equal("保险库信封无效", error.Message);
        }

        [Fact]
        public async Task LockVault_ClearsKeyKeepsId()
        {
            var h = new Harness();
            await RegisteredAsync(h);
            await h.Coordinator.SetupVaultAsync("sync-password");
            await h.Coordinator.LockVaultAsync();
            Assert.Null(h.Vault.State.VaultKeyBase64);
            Assert.Equal(MockSyncServer.VaultId, h.Vault.State.VaultId);
            Assert.Equal(SyncPhase.Locked, h.Coordinator.State.Phase);
            Assert.Equal(VaultStatus.Locked, h.Coordinator.State.Vault);
        }

        [Fact]
        public async Task DeleteVault_ClearsCacheKeepsUser()
        {
            var h = new Harness();
            await RegisteredAsync(h);
            await h.Coordinator.SetupVaultAsync("sync-password");
            await h.Coordinator.DeleteVaultAsync("login-password");
            var cache = h.Vault.State;
            Assert.Null(cache.VaultId);
            Assert.Null(cache.VaultKeyBase64);
            Assert.Equal(MockSyncServer.UserId, cache.UserId);
            Assert.Equal(SyncPhase.Disabled, h.Coordinator.State.Phase);
            Assert.Equal(VaultStatus.Missing, h.Coordinator.State.Vault);
            Assert.Equal("云端保险库和历史版本已删除，本机配置仍保留", h.Coordinator.State.Message);
            Assert.Null(h.Server.VaultEnvelope);
        }

        [Fact]
        public async Task SetPreferences_RejectsDisablingSensitiveSwitch()
        {
            var h = new Harness();
            await RegisteredAsync(h);
            await h.Coordinator.SetupVaultAsync("sync-password");
            await h.Coordinator.SetPreferencesAsync(new SyncPreferences
            {
                Enabled = true,
                AutoSync = true,
                SyncPasswords = true,
                SyncPrivateKeys = false
            });
            Assert.True(h.Vault.State.Preferences.SyncPasswords);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => h.Coordinator.SetPreferencesAsync(new SyncPreferences
                {
                    Enabled = true,
                    AutoSync = true,
                    SyncPasswords = false,
                    SyncPrivateKeys = false
                }));
            Assert.Contains("密钥轮换", error.Message);
            Assert.True(h.Vault.State.Preferences.SyncPasswords);
        }

        [Fact]
        public async Task SetPreferences_DisableAll_GoesDisabled()
        {
            var h = new Harness();
            await RegisteredAsync(h);
            await h.Coordinator.SetupVaultAsync("sync-password");
            await h.Coordinator.SetPreferencesAsync(SyncPreferences.Defaults());
            Assert.Equal(SyncPhase.Disabled, h.Coordinator.State.Phase);
            Assert.True(h.Vault.State.Dirty);
        }

        [Fact]
        public async Task Logout_ClearsAuthAndLocks()
        {
            var h = new Harness();
            await RegisteredAsync(h);
            await h.Coordinator.SetupVaultAsync("sync-password");
            await h.Coordinator.LogoutAsync();
            Assert.False(h.Auth.Session.Authenticated);
            Assert.Null(h.Vault.State.VaultKeyBase64);
            Assert.Equal(MockSyncServer.VaultId, h.Vault.State.VaultId);
            Assert.Equal(SyncPhase.SignedOut, h.Coordinator.State.Phase);
        }

        [Fact]
        public async Task Logout_ServerFailure_StillClearsLocal()
        {
            var h = new Harness();
            await RegisteredAsync(h);
            await h.Coordinator.SetupVaultAsync("sync-password");
            h.Server.FailLogout = true;
            await Assert.ThrowsAsync<ApiError>(() => h.Coordinator.LogoutAsync());
            Assert.False(h.Auth.Session.Authenticated);
            Assert.Null(h.Vault.State.VaultKeyBase64);
            Assert.Equal(SyncPhase.SignedOut, h.Coordinator.State.Phase);
        }

        [Fact]
        public async Task ChangeAccountPassword_ClearsAuthAndLocks()
        {
            var h = new Harness();
            await RegisteredAsync(h);
            await h.Coordinator.SetupVaultAsync("sync-password");
            await h.Coordinator.ChangeAccountPasswordAsync("login-password", "new-login-password");
            Assert.False(h.Auth.Session.Authenticated);
            Assert.Null(h.Vault.State.VaultKeyBase64);
            Assert.Equal(SyncPhase.SignedOut, h.Coordinator.State.Phase);
            Assert.Equal("密码已修改，请使用新密码重新登录", h.Coordinator.State.Message);
        }

        [Fact]
        public async Task DeleteAccount_ClearsEverything()
        {
            var h = new Harness();
            await RegisteredAsync(h);
            await h.Coordinator.SetupVaultAsync("sync-password");
            await h.Coordinator.DeleteAccountAsync("login-password");
            Assert.False(h.Auth.Session.Authenticated);
            Assert.Null(h.Vault.State.VaultId);
            Assert.Null(h.Vault.State.UserId);
            Assert.Equal(SyncPhase.SignedOut, h.Coordinator.State.Phase);
            Assert.Equal(VaultStatus.Missing, h.Coordinator.State.Vault);
        }

        [Fact]
        public async Task DuplicateLogin_Rejected()
        {
            // 踩坑 #10：已登录不再重复登录（每次登录新建设备）。
            var h = new Harness();
            await RegisteredAsync(h);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => h.Coordinator.LoginAsync("a@b.c", "login-password"));
        }

        [Fact]
        public async Task UserBinding_DifferentUserResetsCache()
        {
            var h = new Harness();
            await RegisteredAsync(h);
            await h.Coordinator.SetupVaultAsync("sync-password");
            Assert.Equal(MockSyncServer.UserId, h.Vault.State.UserId);

            // 同一保险库文件换另一个用户（u2）：绑定时整体重置，随后探测重新发现云端保险库。
            var authB = new AuthStore(new InMemorySecureFile());
            await authB.SaveAsync(new AuthTokenResponse
            {
                AccessToken = "a-u2",
                RefreshToken = "r-u2",
                ExpiresIn = 3600,
                User = new AuthUserDto { Id = "u2", Email = "u2@b.c" },
                Device = new AuthDeviceDto { Id = "d-2", Name = "B" }
            });
            var vaultB = new VaultCacheStore(h.VaultFile);
            var apiB = new ApiClient("https://sync.example.test", authB, h.Transport, retryLimit: 0);
            var coordB = new SyncCoordinator(authB, vaultB, apiB, h.Crypto, new StubDevices(), h.Logger);
            await coordB.InitializeAsync();

            Assert.Equal("u2", vaultB.State.UserId);
            Assert.Null(vaultB.State.VaultKeyBase64);
            Assert.Equal(MockSyncServer.VaultId, vaultB.State.VaultId);
            Assert.Equal(SyncPhase.Locked, coordB.State.Phase);
            Assert.Equal(VaultStatus.Locked, coordB.State.Vault);
        }

        [Fact]
        public async Task Logs_DoNotContainSecrets()
        {
            var h = new Harness();
            await h.Coordinator.InitializeAsync();
            await h.Coordinator.RegisterAsync("a@b.c", "login-password-SECRET");
            string recoveryKey = await h.Coordinator.SetupVaultAsync("sync-password-SECRET");
            await h.Coordinator.LockVaultAsync();
            await h.Coordinator.UnlockVaultAsync("sync-password-SECRET", VaultUnlockMethod.Password);
            string vaultKey = h.Vault.State.VaultKeyBase64;
            string all = string.Join("\n", h.Logger.Lines);
            Assert.DoesNotContain("login-password-SECRET", all);
            Assert.DoesNotContain("sync-password-SECRET", all);
            Assert.DoesNotContain(recoveryKey, all);
            Assert.DoesNotContain(vaultKey, all);
        }
    }
}
