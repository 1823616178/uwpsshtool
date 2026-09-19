using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Sessions;
using SshTool.Core.Storage;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Api;
using SshTool.Core.Sync.Api.Dtos;
using SshTool.Core.Sync.Auth;
using SshTool.Core.Sync.Protocol;
using SshTool.Core.Sync.Vault;
using SshTool.Core.Tests.Fakes;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // S13 验收：§10.1 Coordinator 8、10、11、12、13、19（单测名称对齐 §10.1），
    // 另加：退避序列 1/2/5/10/30/60/300 s、Retry-After 优先、MarkDirty 防抖 3000 ms、
    // Restore/Clear/List 透传、轮换失败 ambiguous 文案。
    // 范围：RotateVaultKey（敏感关闭/改同步密码，含回滚与 ambiguous）、
    // RestoreRevision→SyncNow(use-remote)、ClearRevisions、List 透传、
    // HandleSyncError（终端鉴权/signed_out/offline/error/退避/ITimerFactory）、MarkDirty。
    // S14 范围（触发器接线与轮询）不在此文件。
    public class SyncCoordinatorRotateTests
    {
        private static readonly DateTimeOffset FixedNow =
            new DateTimeOffset(2026, 9, 19, 8, 0, 0, TimeSpan.Zero);

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

        // 假本地适配器：与 S12a/S12b 同构，另按 preferences 开关输出 secrets
        //（模拟真实 SyncLocalAdapter §5.1：关闭的开关不输出对应凭据）。
        private sealed class FakeLocalAdapter : ISyncLocalPort
        {
            public SyncDocumentV1 Current;
            public int BuildCount;
            public int ApplyCount;

            public Task<SyncDocumentV1> BuildLocalDocumentAsync(SyncPreferencesV1 preferences)
            {
                BuildCount++;
                var snapshot = Current.Clone();
                snapshot.Preferences = preferences.Clone();
                if (!preferences.SyncPasswords)
                {
                    foreach (var server in snapshot.Servers)
                    {
                        if (server == null || server.Secrets == null)
                        {
                            continue;
                        }
                        server.Secrets.Password = null;
                        server.Secrets.Passphrase = null;
                        if (server.Secrets.PrivateKey == null)
                        {
                            server.Secrets = null;
                        }
                    }
                }
                if (!preferences.SyncPrivateKeys)
                {
                    foreach (var server in snapshot.Servers)
                    {
                        if (server == null || server.Secrets == null)
                        {
                            continue;
                        }
                        server.Secrets.PrivateKey = null;
                        server.Secrets.PrivateKeyEncoding = null;
                        server.Secrets.PrivateKeyFormat = null;
                        server.Secrets.PrivateKeyFingerprint = null;
                        if (server.Secrets.Password == null && server.Secrets.Passphrase == null)
                        {
                            server.Secrets = null;
                        }
                    }
                }
                return Task.FromResult(snapshot);
            }

            public Task ApplyDocumentAsync(SyncDocumentV1 document)
            {
                ApplyCount++;
                var next = document.Clone();
                // §5.2「出现即覆盖、缺失即保留」最小模拟（完整语义由 S10 覆盖）。
                var previousById = new Dictionary<string, ServerRecord>(StringComparer.Ordinal);
                foreach (var server in Current.Servers)
                {
                    previousById[server.Profile.Id] = server;
                }
                foreach (var server in next.Servers)
                {
                    ServerRecord previous;
                    if (!previousById.TryGetValue(server.Profile.Id, out previous)
                        || previous.Secrets == null)
                    {
                        continue;
                    }
                    if (server.Secrets == null)
                    {
                        server.Secrets = previous.Secrets.Clone();
                        continue;
                    }
                    if (server.Secrets.Password == null)
                    {
                        server.Secrets.Password = previous.Secrets.Password;
                    }
                    if (server.Secrets.Passphrase == null)
                    {
                        server.Secrets.Passphrase = previous.Secrets.Passphrase;
                    }
                }
                Current = next;
                return Task.CompletedTask;
            }
        }

        // 桌面端 MockSyncService 的 S13 分组：HEAD/GET/PUT（S12a 同构）+
        // POST vault/rotate、POST restore、DELETE revisions、GET revisions/devices。
        private sealed class MockSyncServer
        {
            public const string VaultId = "vault-test-1";

            public string DocumentBody;
            public int Revision;
            public int DocumentKeyVersion = 1;
            public bool Offline;
            public Func<HttpResponseData> HeadOverride;
            public bool ThrowOnRotate;
            public int FailRotateResponses;
            public int DeletedRevisionsToReport = 5;
            public JObject VaultEnvelope;
            public readonly List<HttpRequestData> Requests = new List<HttpRequestData>();
            public readonly List<HttpRequestData> RotateRequests = new List<HttpRequestData>();
            public HttpRequestData RestoreRequest;
            public HttpRequestData ClearRequest;
            public string LastRotateCurrentPassword;

            private readonly Dictionary<string, PutRecord> _puts =
                new Dictionary<string, PutRecord>(StringComparer.Ordinal);

            private sealed class PutRecord
            {
                public string Body;
                public string Response;
            }

            public int PutCount()
            {
                lock (Requests)
                {
                    return Requests.Count(r => r.Method == "PUT"
                        && PathOf(r.Url).EndsWith("/sync/document", StringComparison.Ordinal));
                }
            }

            public async Task<HttpResponseData> HandleAsync(HttpRequestData request)
            {
                lock (Requests)
                {
                    Requests.Add(request);
                }
                if (Offline)
                {
                    throw new InvalidOperationException("mock offline");
                }
                string path = PathOf(request.Url ?? "");
                string method = request.Method ?? "";
                if (path.EndsWith("/api/v1/sync/document", StringComparison.Ordinal))
                {
                    if (method == "HEAD")
                    {
                        return Head();
                    }
                    if (method == "GET")
                    {
                        return Get();
                    }
                    if (method == "PUT")
                    {
                        return Put(request);
                    }
                }
                if (path.EndsWith("/api/v1/vault/rotate", StringComparison.Ordinal) && method == "POST")
                {
                    return Rotate(request);
                }
                if (path.EndsWith("/api/v1/vault/key-envelope", StringComparison.Ordinal) && method == "GET")
                {
                    if (VaultEnvelope == null)
                    {
                        return ApiErrorJson(404, "VAULT_NOT_FOUND", "vault not found");
                    }
                    var merged = new JObject { ["id"] = VaultId };
                    foreach (var property in VaultEnvelope.Properties())
                    {
                        merged[property.Name] = property.Value.DeepClone();
                    }
                    return Json(200, merged.ToString(Formatting.None));
                }
                if (path.EndsWith("/restore", StringComparison.Ordinal) && method == "POST")
                {
                    return Restore(request, path);
                }
                if (path.EndsWith("/api/v1/sync/revisions", StringComparison.Ordinal) && method == "DELETE")
                {
                    ClearRequest = request;
                    return Json(200, new JObject
                    {
                        ["ok"] = true,
                        ["currentRevision"] = Revision.ToString(CultureInfo.InvariantCulture),
                        ["deletedRevisions"] = DeletedRevisionsToReport
                    }.ToString(Formatting.None));
                }
                if (path.EndsWith("/api/v1/sync/revisions", StringComparison.Ordinal) && method == "GET")
                {
                    return Json(200, new JObject
                    {
                        ["items"] = new JArray
                        {
                            new JObject
                            {
                                ["revision"] = Revision.ToString(CultureInfo.InvariantCulture),
                                ["schemaVersion"] = SyncConstants.SchemaVersion,
                                ["keyVersion"] = DocumentKeyVersion,
                                ["algorithm"] = SyncConstants.AesAlgorithm,
                                ["ciphertextHash"] = "aa",
                                ["createdByDevice"] = new JObject
                                {
                                    ["id"] = "d-1",
                                    ["name"] = "Lumia",
                                    ["platform"] = "windows-uwp-x64",
                                    ["appVersion"] = "0.1.0-test"
                                },
                                ["createdAt"] = "2026-09-19T08:00:00.000Z"
                            }
                        },
                        ["pagination"] = new JObject { ["hasMore"] = false }
                    }.ToString(Formatting.None));
                }
                if (path.EndsWith("/api/v1/devices", StringComparison.Ordinal) && method == "GET")
                {
                    return Json(200, new JObject
                    {
                        ["items"] = new JArray
                        {
                            new JObject
                            {
                                ["id"] = "d-1",
                                ["name"] = "Lumia",
                                ["platform"] = "windows-uwp-x64",
                                ["appVersion"] = "0.1.0-test",
                                ["createdAt"] = "2026-09-19T07:00:00.000Z",
                                ["lastSeenAt"] = "2026-09-19T08:00:00.000Z",
                                ["current"] = true
                            }
                        }
                    }.ToString(Formatting.None));
                }
                return ApiErrorJson(404, "NOT_FOUND", "not implemented: " + method + " " + path);
            }

            private HttpResponseData Head()
            {
                if (HeadOverride != null)
                {
                    return HeadOverride();
                }
                if (DocumentBody == null)
                {
                    return new HttpResponseData(
                        404,
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "x-request-id", "mock-head-404" }
                        },
                        null);
                }
                return new HttpResponseData(
                    200,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "x-request-id", "mock-head-ok" },
                        { "x-sync-revision", Revision.ToString(CultureInfo.InvariantCulture) },
                        { "x-key-version", DocumentKeyVersion.ToString(CultureInfo.InvariantCulture) },
                        { "etag", "\"revision-" + Revision + "\"" },
                        { "last-modified", "Fri, 19 Sep 2026 08:00:00 GMT" }
                    },
                    null);
            }

            private HttpResponseData Get()
            {
                if (DocumentBody == null)
                {
                    return ApiErrorJson(404, "SYNC_DOCUMENT_NOT_FOUND", "document not found");
                }
                var envelope = JObject.Parse(DocumentBody);
                envelope["revision"] = Revision.ToString(CultureInfo.InvariantCulture);
                envelope["updatedByDeviceId"] = "remote-device";
                envelope["updatedAt"] = "2026-09-19T08:00:00.000Z";
                return Json(200, envelope.ToString(Formatting.None));
            }

            private HttpResponseData Put(HttpRequestData request)
            {
                string key = HeaderOf(request, "Idempotency-Key");
                string body = request.Body ?? "";
                string ifMatch = HeaderOf(request, "If-Match");
                PutRecord previous;
                if (key != null && _puts.TryGetValue(key, out previous))
                {
                    if (!string.Equals(previous.Body, body, StringComparison.Ordinal))
                    {
                        return ApiErrorJson(409, "IDEMPOTENCY_KEY_REUSED", "body changed");
                    }
                    return Json(200, previous.Response);
                }
                if (!string.Equals(ifMatch, "\"revision-" + Revision + "\"", StringComparison.Ordinal))
                {
                    return ApiErrorJson(409, "SYNC_REVISION_CONFLICT", "remote changed");
                }
                Revision++;
                string response = new JObject
                {
                    ["revision"] = Revision.ToString(CultureInfo.InvariantCulture),
                    ["updatedAt"] = "2026-09-19T08:00:00.000Z"
                }.ToString(Formatting.None);
                DocumentBody = body;
                if (key != null)
                {
                    _puts[key] = new PutRecord { Body = body, Response = response };
                }
                return Json(200, response);
            }

            private HttpResponseData Rotate(HttpRequestData request)
            {
                lock (Requests)
                {
                    RotateRequests.Add(request);
                }
                if (ThrowOnRotate)
                {
                    throw new InvalidOperationException("mock rotate response lost");
                }
                if (FailRotateResponses > 0)
                {
                    FailRotateResponses--;
                    return ApiErrorJson(500, "ROTATE_FAILED", "mock rotate failure");
                }
                string ifMatch = HeaderOf(request, "If-Match");
                if (!string.Equals(ifMatch, "\"revision-" + Revision + "\"", StringComparison.Ordinal))
                {
                    return ApiErrorJson(409, "SYNC_REVISION_CONFLICT", "remote changed");
                }
                var body = JObject.Parse(request.Body ?? "{}");
                LastRotateCurrentPassword = (string)body["currentPassword"];
                var envelope = (JObject)body["keyEnvelope"];
                var document = (JObject)body["document"];
                if (envelope == null || document == null)
                {
                    return ApiErrorJson(400, "VALIDATION_ERROR", "missing keyEnvelope/document");
                }
                DocumentKeyVersion = (int)envelope["keyVersion"];
                DocumentBody = document.ToString(Formatting.None);
                VaultEnvelope = (JObject)envelope.DeepClone();
                Revision++;
                return Json(200, new JObject
                {
                    ["id"] = VaultId,
                    ["keyVersion"] = DocumentKeyVersion,
                    ["revision"] = Revision.ToString(CultureInfo.InvariantCulture),
                    ["updatedAt"] = "2026-09-19T08:00:00.000Z"
                }.ToString(Formatting.None));
            }

            // POST sync/revisions/{r}/restore → 新 revision（内容沿用当前服务端文档），
            // 随后 SyncNow(use-remote) 把恢复后的远端拉下来。
            private HttpResponseData Restore(HttpRequestData request, string path)
            {
                RestoreRequest = request;
                string marker = "/sync/revisions/";
                int start = path.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
                int end = path.IndexOf("/restore", start, StringComparison.Ordinal);
                string restoredFrom = path.Substring(start, end - start);
                Revision++;
                return Json(200, new JObject
                {
                    ["revision"] = Revision.ToString(CultureInfo.InvariantCulture),
                    ["restoredFromRevision"] = restoredFrom,
                    ["updatedAt"] = "2026-09-19T08:00:00.000Z"
                }.ToString(Formatting.None));
            }

            private static string PathOf(string url)
            {
                int query = url.IndexOf('?');
                return query >= 0 ? url.Substring(0, query) : url;
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

            public static HttpResponseData ApiErrorJson(
                int status, string code, string message, string retryAfter = null)
            {
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "x-request-id", "mock-" + code.ToLowerInvariant() }
                };
                if (retryAfter != null)
                {
                    headers["retry-after"] = retryAfter;
                }
                return new HttpResponseData(
                    status,
                    headers,
                    @"{""statusCode"":" + status + @",""data"":{""code"":""" + code
                    + @""",""message"":""" + message + @"""}}");
            }

            public static string HeaderOf(HttpRequestData request, string name)
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
            public readonly FakeLocalAdapter Local;
            public readonly ManualTimerFactory Timers = new ManualTimerFactory();
            public readonly SyncCoordinator Coordinator;
            public string VaultKey;
            public string SeedRecoveryKey;

            public Harness(SyncDocumentV1 localDoc)
            {
                Auth = new AuthStore(AuthFile);
                Vault = new VaultCacheStore(VaultFile, Logger);
                Transport.AsyncHandler = Server.HandleAsync;
                // retryLimit 0：ApiClient 层不重试，退避重试是协调器 S13 的职责。
                Api = new ApiClient("https://sync.example.test", Auth, Transport, retryLimit: 0);
                Local = new FakeLocalAdapter { Current = localDoc };
                Coordinator = new SyncCoordinator(
                    Auth, Vault, Api, Crypto, new StubDevices(), Logger,
                    () => FixedNow, null, Local,
                    ms => Task.CompletedTask,
                    Timers);
            }

            public async Task SeedLoginAsync()
            {
                await Auth.SaveAsync(new AuthTokenResponse
                {
                    AccessToken = "a-test",
                    RefreshToken = "r-test",
                    ExpiresIn = 3600,
                    User = new AuthUserDto { Id = "u1", Email = "a@b.c" },
                    Device = new AuthDeviceDto { Id = "d-1", Name = "Lumia" }
                });
            }

            public async Task SeedVaultAsync(string revision, SyncDocumentV1 baseDoc, bool dirty)
            {
                var setup = await Crypto.CreateAsync("sync-password", 1);
                VaultKey = setup.VaultKeyBase64;
                SeedRecoveryKey = setup.RecoveryKey;
                Server.VaultEnvelope = setup.Envelope.ToJson();
                // 服务端处于同一基线 revision（轮换/清空的 If-Match 与之比对）。
                Server.Revision = int.Parse(revision, CultureInfo.InvariantCulture);
                await Vault.UpdateAsync(next =>
                {
                    next.UserId = "u1";
                    next.VaultId = MockSyncServer.VaultId;
                    next.VaultKeyBase64 = VaultKey;
                    next.KeyVersion = 1;
                    next.Revision = revision;
                    next.Preferences.Enabled = true;
                    next.Preferences.AutoSync = true;
                    next.BaseDocument = baseDoc == null ? null : baseDoc.Clone();
                    next.Dirty = dirty;
                });
            }

            public async Task ServeDocumentAsync(SyncDocumentV1 doc, int revision)
            {
                var envelope = await Crypto.EncryptDocumentAsync(
                    VaultKey, MockSyncServer.VaultId, SyncConstants.SchemaVersion, 1,
                    Encoding.UTF8.GetBytes(SyncDocumentWriter.Write(doc)));
                Server.DocumentBody = envelope.ToJson().ToString(Formatting.None);
                Server.Revision = revision;
            }

            public async Task<SyncDocumentV1> ReadServerDocumentAsync(string vaultKey)
            {
                var data = EncryptedDocumentData.Parse(JObject.Parse(Server.DocumentBody));
                var envelope = new EncryptedDocumentEnvelope
                {
                    SchemaVersion = data.SchemaVersion,
                    KeyVersion = data.KeyVersion,
                    Algorithm = data.Algorithm,
                    Nonce = data.Nonce,
                    Ciphertext = data.Ciphertext,
                    CiphertextHash = data.CiphertextHash
                };
                byte[] plaintext = await Crypto.DecryptDocumentAsync(
                    vaultKey, MockSyncServer.VaultId, envelope);
                if (plaintext == null)
                {
                    return null;
                }
                return SyncDocumentReader.Read(plaintext);
            }

            // 未被 Dispose 的一次性定时（重试/防抖）。
            public List<ManualTimer> LiveTimers()
            {
                return Timers.Timers.Where(t => !t.Disposed && !t.Periodic).ToList();
            }

            public ManualTimer LastLiveTimer()
            {
                return LiveTimers().Last();
            }
        }

        private static SyncDocumentV1 NewDoc(
            string serverName,
            string passwordOrNull = null,
            string updatedAt = "2026-09-19T08:00:00.000Z")
        {
            var doc = new SyncDocumentV1
            {
                SchemaVersion = SyncConstants.SchemaVersion,
                UpdatedAt = updatedAt,
                Preferences = new SyncPreferencesV1 { SyncPasswords = false, SyncPrivateKeys = false }
            };
            var server = new ServerRecord
            {
                Profile = new PortableServerProfile
                {
                    Id = "server-1",
                    Name = serverName,
                    Host = "ssh.example.test",
                    Port = 22,
                    Username = "deploy",
                    AuthType = AuthType.Password,
                    HostFingerprint = "",
                    Keepalive = 30
                }
            };
            if (passwordOrNull != null)
            {
                server.Secrets = new ServerSecrets { Password = passwordOrNull };
            }
            doc.Servers.Add(server);
            return doc;
        }

        private static string ExpectedRetryAt(long delayMs)
        {
            return FixedNow.AddMilliseconds((double)delayMs)
                .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        private static async Task WaitForPhaseAsync(SyncCoordinator coordinator, SyncPhase phase)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (coordinator.State.Phase != phase && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            Assert.Equal(phase, coordinator.State.Phase);
        }

        // ---------- §10.1 Coordinator 8：网络失败进入 offline 并安排自动重试 ----------

        [Fact]
        public async Task Coordinator08_Offline_SchedulesRetryWithBackoffTable()
        {
            var h = new Harness(NewDoc("Base"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("1", NewDoc("Base"), dirty: false);
            h.Server.Offline = true;

            // 退避序列 1/2/5/10/30/60/300/300…s（第 8 次仍封顶 300 s）。
            long[] expectedDelaysMs = { 1000, 2000, 5000, 10000, 30000, 60000, 300000, 300000 };
            for (int i = 0; i < expectedDelaysMs.Length; i++)
            {
                var error = await Assert.ThrowsAsync<ApiError>(() => h.Coordinator.SyncNowAsync());
                Assert.Equal("NETWORK_ERROR", error.Code);
                var state = h.Coordinator.State;
                Assert.Equal(SyncPhase.Offline, state.Phase);
                Assert.Contains("无法连接同步服务器", state.Message);
                Assert.Equal(ExpectedRetryAt(expectedDelaysMs[i]), state.NextRetryAt);
                var live = h.LiveTimers();
                Assert.Single(live);
                Assert.Equal((int)expectedDelaysMs[i], live[0].DelayMs);
            }
        }

        [Fact]
        public async Task RetryAfter_TakesPrecedenceOverBackoffSteps()
        {
            var h = new Harness(NewDoc("Base"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("1", NewDoc("Base"), dirty: false);
            h.Server.HeadOverride = () => MockSyncServer.ApiErrorJson(
                429, "RATE_LIMITED", "slow down", retryAfter: "45");

            var error = await Assert.ThrowsAsync<ApiError>(() => h.Coordinator.SyncNowAsync());
            Assert.Equal("RATE_LIMITED", error.Code);
            Assert.Equal(45000L, error.RetryAfterMs);
            // Retry-After 优先于退避表首步 1 s，但计数照样累进。
            // 429 是 http 类错误：相位为 error（仅 network/timeout 进 offline），但同样安排重试。
            Assert.Equal(SyncPhase.Error, h.Coordinator.State.Phase);
            Assert.Equal(ExpectedRetryAt(45000), h.Coordinator.State.NextRetryAt);
            Assert.Equal(45000, h.LastLiveTimer().DelayMs);

            // 下一次失败（无 Retry-After）走退避表第二步 2 s。
            h.Server.HeadOverride = null;
            h.Server.Offline = true;
            await Assert.ThrowsAsync<ApiError>(() => h.Coordinator.SyncNowAsync());
            Assert.Equal(ExpectedRetryAt(2000), h.Coordinator.State.NextRetryAt);
            Assert.Equal(2000, h.LastLiveTimer().DelayMs);
        }

        [Fact]
        public async Task RetryTimer_FiresSyncNow()
        {
            var h = new Harness(NewDoc("Changed"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("1", NewDoc("Base"), dirty: true);
            h.Server.Offline = true;
            await Assert.ThrowsAsync<ApiError>(() => h.Coordinator.SyncNowAsync());
            Assert.Equal(SyncPhase.Offline, h.Coordinator.State.Phase);
            Assert.Single(h.LiveTimers());

            // 网络恢复后触发重试定时 → 自动再同步并上传成功。
            h.Server.Offline = false;
            await h.ServeDocumentAsync(NewDoc("Base"), 1);
            h.LastLiveTimer().Fire();

            await WaitForPhaseAsync(h.Coordinator, SyncPhase.Synced);
            Assert.Equal(1, h.Server.PutCount());
            Assert.Equal("2", h.Vault.State.Revision);
            Assert.Empty(h.LiveTimers());
        }

        [Fact]
        public async Task TerminalAuth_ClearsSessionAndEntersAuthError()
        {
            var h = new Harness(NewDoc("Base"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("1", NewDoc("Base"), dirty: false);
            h.Server.HeadOverride = () => MockSyncServer.ApiErrorJson(
                401, "AUTH_DEVICE_REVOKED", "device revoked");

            await Assert.ThrowsAsync<ApiError>(() => h.Coordinator.SyncNowAsync());

            Assert.False(h.Auth.Session.Authenticated);
            Assert.Null(h.Vault.State.VaultKeyBase64);
            var state = h.Coordinator.State;
            Assert.Equal(SyncPhase.AuthError, state.Phase);
            Assert.Equal(VaultStatus.Locked, state.Vault);
            Assert.Null(state.NextRetryAt);
            Assert.Empty(h.LiveTimers());
        }

        [Fact]
        public async Task NonRetryableError_EntersErrorWithoutRetry()
        {
            var h = new Harness(NewDoc("Base"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("1", NewDoc("Base"), dirty: false);
            h.Server.HeadOverride = () => MockSyncServer.ApiErrorJson(
                400, "VALIDATION_ERROR", "bad request");

            await Assert.ThrowsAsync<ApiError>(() => h.Coordinator.SyncNowAsync());

            var state = h.Coordinator.State;
            Assert.Equal(SyncPhase.Error, state.Phase);
            Assert.Null(state.NextRetryAt);
            Assert.Empty(h.LiveTimers());
            // 登录会话保留（非终端鉴权错误不清会话）。
            Assert.True(h.Auth.Session.Authenticated);
        }

        [Fact]
        public async Task UploadSuccess_ResetsBackoff()
        {
            var h = new Harness(NewDoc("Changed"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("1", NewDoc("Base"), dirty: true);
            h.Server.Offline = true;
            await Assert.ThrowsAsync<ApiError>(() => h.Coordinator.SyncNowAsync());
            Assert.Equal(1000, h.LastLiveTimer().DelayMs);

            // 联网上传成功 → 退避计数清零。
            h.Server.Offline = false;
            await h.ServeDocumentAsync(NewDoc("Base"), 1);
            await h.Coordinator.SyncNowAsync();
            Assert.Equal(SyncPhase.Synced, h.Coordinator.State.Phase);

            // 再次失败从第一步 1 s 重新开始（而非 2 s）。
            h.Server.Offline = true;
            await Assert.ThrowsAsync<ApiError>(() => h.Coordinator.SyncNowAsync());
            Assert.Equal(ExpectedRetryAt(1000), h.Coordinator.State.NextRetryAt);
            Assert.Equal(1000, h.LastLiveTimer().DelayMs);
        }

        // ---------- §10.1 Coordinator 10：关闭敏感同步时轮换密钥，旧密钥不可用 ----------

        [Fact]
        public async Task Coordinator10_RotateSensitiveSync_DisablesPasswordsAndRotatesKey()
        {
            var h = new Harness(NewDoc("Base", "local-password"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("1", NewDoc("Base"), dirty: false);
            await h.Vault.UpdateAsync(next => { next.Preferences.SyncPasswords = true; });
            string oldKey = h.VaultKey;

            string recoveryKey = await h.Coordinator.RotateSensitiveSyncAsync(
                new SyncPreferences
                {
                    Enabled = true,
                    AutoSync = true,
                    SyncPasswords = false,
                    SyncPrivateKeys = false
                },
                "account-password",
                "new-sync-password");

            Assert.Matches("^SPM1-", recoveryKey);
            // 请求：POST vault/rotate，If-Match=轮换前 revision，新幂等键，三段式 body。
            Assert.Single(h.Server.RotateRequests);
            var rotate = h.Server.RotateRequests[0];
            Assert.Equal("\"revision-1\"", MockSyncServer.HeaderOf(rotate, "If-Match"));
            Assert.False(string.IsNullOrEmpty(MockSyncServer.HeaderOf(rotate, "Idempotency-Key")));
            Assert.Equal("account-password", h.Server.LastRotateCurrentPassword);
            var rotateBody = JObject.Parse(rotate.Body);
            Assert.NotNull(rotateBody["keyEnvelope"]);
            Assert.NotNull(rotateBody["document"]);
            Assert.Equal(2, (int)rotateBody["keyEnvelope"]["keyVersion"]);
            // 旧密钥解不开轮换后的文档；新密钥解开且不含 secrets。
            Assert.Null(await h.ReadServerDocumentAsync(oldKey));
            string newKey = h.Vault.State.VaultKeyBase64;
            Assert.NotEqual(oldKey, newKey);
            var uploaded = await h.ReadServerDocumentAsync(newKey);
            Assert.NotNull(uploaded);
            Assert.Equal("Base", uploaded.Servers[0].Profile.Name);
            Assert.True(uploaded.Servers[0].Secrets == null
                || uploaded.Servers[0].Secrets.Password == null);
            // 缓存与状态：keyVersion 2、revision+1、不脏、无 pending、无冲突。
            var cache = h.Vault.State;
            Assert.Equal(2, cache.KeyVersion);
            Assert.Equal("2", cache.Revision);
            Assert.False(cache.Dirty);
            Assert.False(cache.Preferences.SyncPasswords);
            Assert.Null(cache.PendingUpload);
            Assert.Null(cache.Conflict);
            Assert.Equal("Base", cache.BaseDocument.Servers[0].Profile.Name);
            var state = h.Coordinator.State;
            Assert.Equal(SyncPhase.Synced, state.Phase);
            Assert.Equal(VaultStatus.Ready, state.Vault);
            Assert.Equal(2, state.KeyVersion);
            Assert.Equal("2", state.Revision);
            Assert.Equal("敏感字段已清理，密钥和历史版本已轮换", state.Message);
        }

        [Fact]
        public async Task RotateSensitiveSync_RequiresDisablingSwitch()
        {
            var h = new Harness(NewDoc("Base"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("1", NewDoc("Base"), dirty: false);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => h.Coordinator.RotateSensitiveSyncAsync(
                    SyncPreferences.Defaults(), "account-password", "new-sync-password"));
            Assert.Equal("密钥轮换只用于关闭已启用的敏感同步", error.Message);
            Assert.Empty(h.Server.RotateRequests);
        }

        // ---------- §10.1 Coordinator 11：轮换失败时回滚 preferences ----------

        [Fact]
        public async Task Coordinator11_RotateFailure_RollsBackPreferences()
        {
            var h = new Harness(NewDoc("Base", "local-password"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("1", NewDoc("Base"), dirty: false);
            await h.Vault.UpdateAsync(next => { next.Preferences.SyncPasswords = true; });
            // 按应用启动路径重建状态（idle 基线；确定性失败不应改变相位）。
            await h.Coordinator.InitializeAsync();
            Assert.Equal(SyncPhase.Idle, h.Coordinator.State.Phase);
            string keyBefore = h.Vault.State.VaultKeyBase64;
            h.Server.FailRotateResponses = 1;

            var error = await Assert.ThrowsAsync<ApiError>(
                () => h.Coordinator.RotateSensitiveSyncAsync(
                    new SyncPreferences
                    {
                        Enabled = true,
                        AutoSync = true,
                        SyncPasswords = false,
                        SyncPrivateKeys = false
                    },
                    "account-password",
                    "new-sync-password"));
            Assert.Equal("ROTATE_FAILED", error.Code);

            var cache = h.Vault.State;
            Assert.True(cache.Preferences.SyncPasswords);
            Assert.Equal(keyBefore, cache.VaultKeyBase64);
            Assert.Equal(1, cache.KeyVersion);
            Assert.Equal("1", cache.Revision);
            // 确定性失败不改相位（沿用轮换前 idle），只回滚偏好。
            Assert.Equal(SyncPhase.Idle, h.Coordinator.State.Phase);
        }

        [Fact]
        public async Task RotateFailure_Ambiguous_SurfacesUnknownResultMessage()
        {
            var h = new Harness(NewDoc("Base", "local-password"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("1", NewDoc("Base"), dirty: false);
            await h.Vault.UpdateAsync(next => { next.Preferences.SyncPasswords = true; });
            h.Server.ThrowOnRotate = true;

            var error = await Assert.ThrowsAsync<ApiError>(
                () => h.Coordinator.RotateSensitiveSyncAsync(
                    new SyncPreferences
                    {
                        Enabled = true,
                        AutoSync = true,
                        SyncPasswords = false,
                        SyncPrivateKeys = false
                    },
                    "account-password",
                    "new-sync-password"));
            Assert.True(error.Ambiguous);

            // 偏好同样回滚；响应丢失（服务端可能已执行）→ error + 未知结果文案。
            Assert.True(h.Vault.State.Preferences.SyncPasswords);
            var state = h.Coordinator.State;
            Assert.Equal(SyncPhase.Error, state.Phase);
            Assert.Equal("轮换结果未知，若云端已经轮换，请用新的同步密码重新解锁保险库", state.Message);
        }

        // ---------- §10.1 Coordinator 12：修改同步密码使旧凭据失效 ----------

        [Fact]
        public async Task Coordinator12_ChangeSyncPassword_InvalidatesOldCredentials()
        {
            var h = new Harness(NewDoc("Base"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("0", NewDoc("Base"), dirty: false);
            string oldRecovery = h.SeedRecoveryKey;
            string keyBefore = h.Vault.State.VaultKeyBase64;

            string newRecovery = await h.Coordinator.ChangeSyncPasswordAsync(
                "account-password", "new-sync-password");

            Assert.Matches("^SPM1-", newRecovery);
            var cache = h.Vault.State;
            Assert.Equal(2, cache.KeyVersion);
            Assert.Equal("1", cache.Revision);
            Assert.False(cache.Dirty);
            Assert.Null(cache.PendingUpload);
            // 同步范围不变，文档内容还在，只是换了密钥。
            var uploaded = await h.ReadServerDocumentAsync(cache.VaultKeyBase64);
            Assert.NotNull(uploaded);
            Assert.Equal("Base", uploaded.Servers[0].Profile.Name);
            Assert.Null(await h.ReadServerDocumentAsync(keyBefore));
            Assert.Equal("同步密码已更新，请保存新的恢复密钥", h.Coordinator.State.Message);
            Assert.Equal(SyncPhase.Synced, h.Coordinator.State.Phase);

            // 旧同步密码与旧恢复密钥失效，新凭据可解锁。
            await h.Coordinator.LockVaultAsync();
            var wrongPassword = await Assert.ThrowsAsync<InvalidOperationException>(
                () => h.Coordinator.UnlockVaultAsync("sync-password", VaultUnlockMethod.Password));
            Assert.Equal("同步密码不正确", wrongPassword.Message);
            var wrongRecovery = await Assert.ThrowsAsync<InvalidOperationException>(
                () => h.Coordinator.UnlockVaultAsync(oldRecovery, VaultUnlockMethod.Recovery));
            Assert.Equal("恢复密钥无效", wrongRecovery.Message);
            await h.Coordinator.UnlockVaultAsync("new-sync-password", VaultUnlockMethod.Password);
            Assert.Equal(VaultStatus.Ready, h.Coordinator.State.Vault);
            await h.Coordinator.LockVaultAsync();
            await h.Coordinator.UnlockVaultAsync(newRecovery, VaultUnlockMethod.Recovery);
            Assert.Equal(VaultStatus.Ready, h.Coordinator.State.Vault);
        }

        // ---------- §10.1 Coordinator 13：保险库锁定时拒绝修改同步密码 ----------

        [Fact]
        public async Task Coordinator13_ChangeSyncPassword_WhileLocked_Throws()
        {
            var h = new Harness(NewDoc("Base"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("1", NewDoc("Base"), dirty: false);
            await h.Coordinator.LockVaultAsync();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => h.Coordinator.ChangeSyncPasswordAsync("account-password", "new-sync-password"));
            Assert.Equal("同步保险库未解锁", error.Message);
            Assert.Empty(h.Server.RotateRequests);
        }

        [Fact]
        public async Task RotateSensitiveSync_WhileLocked_Throws()
        {
            var h = new Harness(NewDoc("Base"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("1", NewDoc("Base"), dirty: false);
            await h.Coordinator.LockVaultAsync();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => h.Coordinator.RotateSensitiveSyncAsync(
                    SyncPreferences.Defaults(), "account-password", "new-sync-password"));
            Assert.Equal("同步保险库未解锁", error.Message);
        }

        // ---------- §10.1 Coordinator 19：同步文档与日志不暴露敏感信息 ----------

        [Fact]
        public async Task Coordinator19_RotateAndLogs_DoNotExposeSecrets()
        {
            var h = new Harness(NewDoc("Base", "local-password-SECRET"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("1", NewDoc("Base"), dirty: false);
            await h.Vault.UpdateAsync(next => { next.Preferences.SyncPasswords = true; });
            string keyBefore = h.Vault.State.VaultKeyBase64;

            string newRecovery = await h.Coordinator.RotateSensitiveSyncAsync(
                new SyncPreferences
                {
                    Enabled = true,
                    AutoSync = true,
                    SyncPasswords = false,
                    SyncPrivateKeys = false
                },
                "account-password-SECRET",
                "new-sync-password-SECRET");

            // 未开启开关时云端文档不含密码。
            var uploaded = await h.ReadServerDocumentAsync(h.Vault.State.VaultKeyBase64);
            Assert.NotNull(uploaded);
            Assert.True(uploaded.Servers[0].Secrets == null
                || uploaded.Servers[0].Secrets.Password == null);
            // 日志不含任何敏感材料。
            string logs = string.Join("\n", h.Logger.Lines);
            Assert.DoesNotContain("local-password-SECRET", logs);
            Assert.DoesNotContain("account-password-SECRET", logs);
            Assert.DoesNotContain("new-sync-password-SECRET", logs);
            Assert.DoesNotContain(newRecovery, logs);
            Assert.DoesNotContain(keyBefore, logs);
            Assert.DoesNotContain(h.Vault.State.VaultKeyBase64, logs);
            // 轮换请求原文（含登录密码与信封）只走传输层，不进日志。
            string rotateBodies = string.Join("\n", h.Server.RotateRequests.Select(r => r.Body));
            Assert.Contains("account-password-SECRET", rotateBodies);
        }

        // ---------- Restore / Clear / List ----------

        [Fact]
        public async Task RestoreRevision_RestoresAndSyncsUseRemote()
        {
            var h = new Harness(NewDoc("Local drift"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("1", NewDoc("Base"), dirty: false);
            await h.ServeDocumentAsync(NewDoc("Restored content"), 2);

            await h.Coordinator.RestoreRevisionAsync("2");

            var restore = h.Server.RestoreRequest;
            Assert.NotNull(restore);
            Assert.Equal("POST", restore.Method);
            Assert.EndsWith("/api/v1/sync/revisions/2/restore", restore.Url);
            Assert.Equal("\"revision-1\"", MockSyncServer.HeaderOf(restore, "If-Match"));
            Assert.False(string.IsNullOrEmpty(MockSyncServer.HeaderOf(restore, "Idempotency-Key")));
            // 恢复后 SyncNow(use-remote) 拉取应用：本地以远端为准，进 synced。
            Assert.Equal("Restored content", h.Local.Current.Servers[0].Profile.Name);
            Assert.Equal("3", h.Vault.State.Revision);
            Assert.False(h.Vault.State.Dirty);
            Assert.Equal(SyncPhase.Synced, h.Coordinator.State.Phase);
        }

        [Fact]
        public async Task ClearRevisions_SendsDeleteWithIfMatch()
        {
            var h = new Harness(NewDoc("Base"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("3", NewDoc("Base"), dirty: false);
            await h.ServeDocumentAsync(NewDoc("Base"), 3);
            await h.Coordinator.InitializeAsync();
            var before = h.Coordinator.State;

            DeleteRevisionsResponse response = await h.Coordinator.ClearRevisionsAsync();

            Assert.True(response.Ok);
            Assert.Equal("3", response.CurrentRevision);
            Assert.Equal(5, response.DeletedRevisions);
            Assert.NotNull(h.Server.ClearRequest);
            Assert.Equal("DELETE", h.Server.ClearRequest.Method);
            Assert.Equal("\"revision-3\"", MockSyncServer.HeaderOf(h.Server.ClearRequest, "If-Match"));
            // 清空历史不碰本地同步状态。
            Assert.Equal(before.Phase, h.Coordinator.State.Phase);
            Assert.Equal("3", h.Coordinator.State.Revision);
        }

        [Fact]
        public async Task ListRevisionsAndDevices_Passthrough()
        {
            var h = new Harness(NewDoc("Base"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("2", NewDoc("Base"), dirty: false);
            await h.ServeDocumentAsync(NewDoc("Base"), 2);
            await h.Coordinator.InitializeAsync();
            var before = h.Coordinator.State;

            RevisionListResponse revisions = await h.Coordinator.ListRevisionsAsync();
            Assert.Single(revisions.Items);
            Assert.Equal("2", revisions.Items[0].Revision);
            Assert.Equal(1, revisions.Items[0].KeyVersion);
            Assert.Equal("d-1", revisions.Items[0].CreatedByDevice.Id);

            DeviceListResponse devices = await h.Coordinator.ListDevicesAsync();
            Assert.Single(devices.Items);
            Assert.Equal("d-1", devices.Items[0].Id);
            Assert.True(devices.Items[0].Current);

            // 透传不改本地状态。
            Assert.Equal(before.Phase, h.Coordinator.State.Phase);
            Assert.Equal("2", h.Coordinator.State.Revision);
            Assert.Null(h.Vault.State.PendingUpload);
        }

        // ---------- MarkDirty 防抖 3000 ms ----------

        [Fact]
        public async Task MarkDirty_Disabled_DoesNothing()
        {
            var h = new Harness(NewDoc("Base"));
            await h.SeedLoginAsync();
            await h.Vault.UpdateAsync(next =>
            {
                next.UserId = "u1";
                next.Preferences.Enabled = false;
            });

            await h.Coordinator.MarkDirtyAsync();

            Assert.False(h.Vault.State.Dirty);
            Assert.Empty(h.LiveTimers());
        }

        [Fact]
        public async Task MarkDirty_NoAutoSync_MarksDirtyWithoutTimer()
        {
            var h = new Harness(NewDoc("Base"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("1", NewDoc("Base"), dirty: false);
            await h.Vault.UpdateAsync(next => { next.Preferences.AutoSync = false; });

            await h.Coordinator.MarkDirtyAsync();

            Assert.True(h.Vault.State.Dirty);
            Assert.True(h.Coordinator.State.Dirty);
            Assert.Empty(h.LiveTimers());
        }

        [Fact]
        public async Task MarkDirty_Debounces3000ms_ThenSyncs()
        {
            var h = new Harness(NewDoc("Changed"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("1", NewDoc("Base"), dirty: false);
            await h.ServeDocumentAsync(NewDoc("Base"), 1);

            await h.Coordinator.MarkDirtyAsync();
            Assert.True(h.Vault.State.Dirty);
            Assert.Single(h.LiveTimers());
            Assert.Equal(SyncCoordinator.MarkDirtyDebounceMs, h.LastLiveTimer().DelayMs);
            Assert.Equal(3000, SyncCoordinator.MarkDirtyDebounceMs);

            // 第二次标脏取消前一次防抖，只剩一个新的 3000 ms 定时。
            await h.Coordinator.MarkDirtyAsync();
            var live = h.LiveTimers();
            Assert.Single(live);
            Assert.Equal(3000, live[0].DelayMs);

            live[0].Fire();
            await WaitForPhaseAsync(h.Coordinator, SyncPhase.Synced);
            Assert.Equal(1, h.Server.PutCount());
            Assert.Equal("2", h.Vault.State.Revision);
            Assert.False(h.Vault.State.Dirty);
            Assert.Empty(h.LiveTimers());
        }

        [Fact]
        public async Task Dispose_CancelsPendingTimers()
        {
            var h = new Harness(NewDoc("Changed"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("1", NewDoc("Base"), dirty: false);
            await h.Coordinator.MarkDirtyAsync();
            Assert.Single(h.LiveTimers());

            h.Coordinator.Dispose();

            Assert.Empty(h.LiveTimers());
        }
    }
}
