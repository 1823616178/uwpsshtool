using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SshTool.Core.Common;
using SshTool.Core.Models;
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
    // S12a 验收：§10.1 Coordinator 6、9、20，以及
    // 「上传期间本地又改动 → dirty 保持并 250 ms 后再同步」（单测名称对齐 §10.1）。
    // 范围：PerformSync ①–④非冲突路径 + Upload + CommitRemote；
    // initial-import / remote-deletion / merge-conflict 与 ResolveConflict 在 S12b 覆盖。
    public class SyncCoordinatorSyncTests
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

        // 假本地适配器：内存 SyncDocumentV1 + §5.2「出现即覆盖、缺失即保留」
        // 的凭据语义（B 端独有凭据模拟；完整仓库级保留由 S10 单测覆盖）。
        private sealed class FakeLocalAdapter : ISyncLocalPort
        {
            public SyncDocumentV1 Current;
            public int BuildCount;
            public int ApplyCount;
            public readonly List<SyncDocumentV1> Applied = new List<SyncDocumentV1>();

            public Task<SyncDocumentV1> BuildLocalDocumentAsync(SyncPreferencesV1 preferences)
            {
                BuildCount++;
                var snapshot = Current.Clone();
                snapshot.Preferences = preferences.Clone();
                return Task.FromResult(snapshot);
            }

            public Task ApplyDocumentAsync(SyncDocumentV1 document)
            {
                ApplyCount++;
                Applied.Add(document.Clone());
                var next = document.Clone();
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

        // 桌面端 MockSyncService 的同步文档分组：HEAD/GET/PUT 语义、
        // If-Match 乐观锁、幂等键（同键同体返回旧响应，同键异体 409）、响应丢失。
        private sealed class MockSyncServer
        {
            public const string VaultId = "vault-test-1";

            public string DocumentBody;
            public int Revision;
            public int DocumentKeyVersion = 1;
            public int? HeadKeyVersion;
            public string HeadError;
            public int LosePutResponses;
            public Func<Task> BeforePut;
            public readonly List<HttpRequestData> Requests = new List<HttpRequestData>();

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
                        && r.Url.EndsWith("/sync/document", StringComparison.Ordinal));
                }
            }

            public int GetCount()
            {
                lock (Requests)
                {
                    return Requests.Count(r => r.Method == "GET"
                        && r.Url.EndsWith("/sync/document", StringComparison.Ordinal));
                }
            }

            public HttpRequestData LastPut()
            {
                lock (Requests)
                {
                    return Requests
                        .Where(r => r.Method == "PUT" && r.Url.EndsWith("/sync/document", StringComparison.Ordinal))
                        .Last();
                }
            }

            public async Task<HttpResponseData> HandleAsync(HttpRequestData request)
            {
                lock (Requests)
                {
                    Requests.Add(request);
                }
                string url = request.Url ?? "";
                string method = request.Method ?? "";
                if (url.EndsWith("/api/v1/sync/document", StringComparison.Ordinal))
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
                        return await PutAsync(request).ConfigureAwait(false);
                    }
                }
                return ApiErrorJson(404, "NOT_FOUND", "not implemented: " + method + " " + url);
            }

            private HttpResponseData Head()
            {
                if (HeadError != null)
                {
                    return ApiErrorJson(404, HeadError, "mock head failure");
                }
                if (DocumentBody == null)
                {
                    // HEAD 无响应体：ApiClient 按 §2.4.7 回退 GET 拿准确 data.code。
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
                        { "x-sync-revision", Revision.ToString() },
                        { "x-key-version", (HeadKeyVersion ?? DocumentKeyVersion).ToString() },
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
                envelope["revision"] = Revision.ToString();
                envelope["updatedByDeviceId"] = "remote-device";
                envelope["updatedAt"] = "2026-09-19T08:00:00.000Z";
                return Json(200, envelope.ToString(Formatting.None));
            }

            private async Task<HttpResponseData> PutAsync(HttpRequestData request)
            {
                if (BeforePut != null)
                {
                    await BeforePut().ConfigureAwait(false);
                }
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
                    if (LosePutResponses > 0)
                    {
                        LosePutResponses--;
                        throw new InvalidOperationException("mock response lost");
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
                    ["revision"] = Revision.ToString(),
                    ["updatedAt"] = "2026-09-19T08:00:00.000Z"
                }.ToString(Formatting.None);
                DocumentBody = body;
                if (key != null)
                {
                    _puts[key] = new PutRecord { Body = body, Response = response };
                }
                if (LosePutResponses > 0)
                {
                    LosePutResponses--;
                    throw new InvalidOperationException("mock response lost");
                }
                return Json(200, response);
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
            public readonly FakeLocalAdapter Local;
            public readonly List<long> Sleeps = new List<long>();
            public readonly SyncCoordinator Coordinator;
            public string VaultKey;

            // 默认 sleep 立即完成（单测无等待）；需要真实延迟的用例改写 SleepOverride。
            public Func<long, Task> SleepOverride;

            public Harness(SyncDocumentV1 localDoc)
            {
                Auth = new AuthStore(AuthFile);
                Vault = new VaultCacheStore(VaultFile, Logger);
                Transport.AsyncHandler = Server.HandleAsync;
                // retryLimit 0：单次语义，重试退避是 S13 范围；同时让失败用例无延迟。
                Api = new ApiClient("https://sync.example.test", Auth, Transport, retryLimit: 0);
                Local = new FakeLocalAdapter { Current = localDoc };
                Coordinator = new SyncCoordinator(
                    Auth, Vault, Api, Crypto, new StubDevices(), Logger,
                    null, null, Local,
                    ms =>
                    {
                        lock (Sleeps)
                        {
                            Sleeps.Add(ms);
                        }
                        var impl = SleepOverride;
                        return impl != null ? impl(ms) : Task.CompletedTask;
                    });
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

            public async Task<SyncDocumentV1> ReadServerDocumentAsync()
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
                byte[] plaintext = await Crypto.DecryptDocumentAsync(VaultKey, MockSyncServer.VaultId, envelope);
                return SyncDocumentReader.Read(plaintext);
            }
        }

        private static SyncDocumentV1 NewDoc(
            string serverName,
            string passwordOrNull = null,
            int port = 22,
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
                    Port = port,
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

        // ---------- §10.1 Coordinator 6：A 修改后 B 下载，保留 B 独有凭据 ----------

        [Fact]
        public async Task Coordinator06_RemoteNewer_CleanAppliesRemoteAndPreservesLocalSecrets()
        {
            var h = new Harness(NewDoc("B-local", "B-only-password"));
            await h.SeedLoginAsync();
            var baseline = NewDoc("Base");
            await h.SeedVaultAsync("1", baseline, dirty: false);
            // A 已上传更新（revision 2，远端无 secrets）。
            await h.ServeDocumentAsync(NewDoc("From-A"), 2);

            await h.Coordinator.SyncNowAsync();

            Assert.Equal(1, h.Local.ApplyCount);
            Assert.Equal("From-A", h.Local.Current.Servers[0].Profile.Name);
            // B 独有凭据保留（远端未出现的键保留本机值，见 §5.2）。
            Assert.Equal("B-only-password", h.Local.Current.Servers[0].Secrets.Password);
            var cache = h.Vault.State;
            Assert.Equal("2", cache.Revision);
            Assert.False(cache.Dirty);
            Assert.Equal("From-A", cache.BaseDocument.Servers[0].Profile.Name);
            Assert.Null(cache.PendingUpload);
            var state = h.Coordinator.State;
            Assert.Equal(SyncPhase.Synced, state.Phase);
            Assert.Equal("2", state.Revision);
            Assert.False(state.Dirty);
        }

        // ---------- 无新版本时的上传条件 ----------

        [Fact]
        public async Task SyncFlow_NoNewRevision_Dirty_Uploads()
        {
            var h = new Harness(NewDoc("Changed"));
            await h.SeedLoginAsync();
            var baseline = NewDoc("Base");
            await h.SeedVaultAsync("1", baseline, dirty: true);
            await h.ServeDocumentAsync(baseline, 1);

            await h.Coordinator.SyncNowAsync();

            Assert.Equal(1, h.Server.PutCount());
            var put = h.Server.LastPut();
            Assert.Equal("\"revision-1\"", HeaderOf(put, "If-Match"));
            Assert.False(string.IsNullOrEmpty(HeaderOf(put, "Idempotency-Key")));
            var cache = h.Vault.State;
            Assert.Equal("2", cache.Revision);
            Assert.False(cache.Dirty);
            Assert.Equal("Changed", cache.BaseDocument.Servers[0].Profile.Name);
            Assert.Null(cache.PendingUpload);
            Assert.Equal(SyncPhase.Synced, h.Coordinator.State.Phase);
            // 上传内容可解开且与本地一致（零偏差：经 Writer 加密）。
            Assert.Equal("Changed", (await h.ReadServerDocumentAsync()).Servers[0].Profile.Name);
        }

        [Fact]
        public async Task SyncFlow_NoNewRevision_Clean_NoUpload()
        {
            var h = new Harness(NewDoc("Base"));
            await h.SeedLoginAsync();
            var baseline = NewDoc("Base");
            await h.SeedVaultAsync("1", baseline, dirty: false);
            await h.ServeDocumentAsync(baseline, 1);

            await h.Coordinator.SyncNowAsync();

            Assert.Equal(0, h.Server.PutCount());
            Assert.Equal(0, h.Local.ApplyCount);
            Assert.Equal(SyncPhase.Synced, h.Coordinator.State.Phase);
            Assert.Equal("1", h.Vault.State.Revision);
        }

        [Fact]
        public async Task SyncFlow_NoDocumentYet_FirstUpload()
        {
            // HEAD 404（无响应体）→ GET 回退确认 SYNC_DOCUMENT_NOT_FOUND →
            // 按 revision "0" 首次上传（即使本地不脏，见 §7.3 ③）。
            var h = new Harness(NewDoc("Initial"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("0", null, dirty: false);

            await h.Coordinator.SyncNowAsync();

            Assert.Equal(1, h.Server.PutCount());
            Assert.Equal("\"revision-0\"", HeaderOf(h.Server.LastPut(), "If-Match"));
            Assert.Equal("1", h.Vault.State.Revision);
            Assert.False(h.Vault.State.Dirty);
            Assert.Equal(SyncPhase.Synced, h.Coordinator.State.Phase);
        }

        [Fact]
        public async Task SyncFlow_DirtyMergeWithoutConflicts_UploadsMerged()
        {
            // 本地改名、远端改端口：不同字段自动合并后上传（§8 MergeValue）。
            var h = new Harness(NewDoc("Local-Name"));
            await h.SeedLoginAsync();
            var baseline = NewDoc("Base");
            await h.SeedVaultAsync("1", baseline, dirty: true);
            await h.ServeDocumentAsync(NewDoc("Base", null, 2222), 2);

            await h.Coordinator.SyncNowAsync();

            Assert.Equal(1, h.Local.ApplyCount);
            Assert.Equal("Local-Name", h.Local.Current.Servers[0].Profile.Name);
            Assert.Equal(2222, h.Local.Current.Servers[0].Profile.Port);
            Assert.Equal(1, h.Server.PutCount());
            Assert.Equal("\"revision-2\"", HeaderOf(h.Server.LastPut(), "If-Match"));
            Assert.Equal("3", h.Vault.State.Revision);
            Assert.False(h.Vault.State.Dirty);
            Assert.Equal(SyncPhase.Synced, h.Coordinator.State.Phase);
            var uploaded = await h.ReadServerDocumentAsync();
            Assert.Equal("Local-Name", uploaded.Servers[0].Profile.Name);
            Assert.Equal(2222, uploaded.Servers[0].Profile.Port);
        }

        // ---------- §10.1 Coordinator 9：响应丢失后同 body 同幂等键续传 ----------

        [Fact]
        public async Task Coordinator09_PendingUpload_ReplaysIdenticalBodyAndKey()
        {
            var h = new Harness(NewDoc("Changed"));
            await h.SeedLoginAsync();
            var baseline = NewDoc("Base");
            await h.SeedVaultAsync("1", baseline, dirty: true);
            await h.ServeDocumentAsync(baseline, 1);
            h.Server.LosePutResponses = 1;

            // 响应丢失：服务端已提交（revision 2），客户端拿不到响应。
            var error = await Assert.ThrowsAsync<ApiError>(() => h.Coordinator.SyncNowAsync());
            Assert.True(error.Ambiguous);
            var pending = h.Vault.State.PendingUpload;
            Assert.NotNull(pending);
            Assert.Equal("1", pending.BaseRevision);
            Assert.False(string.IsNullOrEmpty(pending.IdempotencyKey));
            Assert.False(string.IsNullOrEmpty(pending.Body));
            Assert.Equal(2, h.Server.Revision);

            // pending 已落盘：同一文件重载后仍在（先落盘再发请求，踩坑 #12）。
            var reloaded = await new VaultCacheStore(h.VaultFile).LoadAsync();
            Assert.Equal(pending.IdempotencyKey, reloaded.PendingUpload.IdempotencyKey);
            Assert.Equal(pending.Body, reloaded.PendingUpload.Body);

            await h.Coordinator.SyncNowAsync();

            var puts = h.Server.Requests
                .Where(r => r.Method == "PUT" && r.Url.EndsWith("/sync/document", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(2, puts.Count);
            Assert.Equal(HeaderOf(puts[0], "Idempotency-Key"), HeaderOf(puts[1], "Idempotency-Key"));
            Assert.Equal(puts[0].Body, puts[1].Body);
            Assert.Equal(pending.Body, puts[1].Body);
            Assert.Equal("\"revision-1\"", HeaderOf(puts[1], "If-Match"));
            var cache = h.Vault.State;
            Assert.Equal("2", cache.Revision);
            Assert.False(cache.Dirty);
            Assert.Null(cache.PendingUpload);
            Assert.Equal(SyncPhase.Synced, h.Coordinator.State.Phase);
        }

        [Fact]
        public async Task PendingUpload_Replay_StillCurrent_SyncsWithoutRebuild()
        {
            // 重放后本地与 pending 一致 → 直接 synced：只多一次重放前的构建，不应用、不二次上传。
            var h = new Harness(NewDoc("Changed"));
            await h.SeedLoginAsync();
            var baseline = NewDoc("Base");
            await h.SeedVaultAsync("1", baseline, dirty: true);
            await h.ServeDocumentAsync(baseline, 1);
            h.Server.LosePutResponses = 1;
            await Assert.ThrowsAsync<ApiError>(() => h.Coordinator.SyncNowAsync());
            int buildsAfterFailure = h.Local.BuildCount;

            await h.Coordinator.SyncNowAsync();

            // 重放 1 次 PUT（共 2 次），只多 1 次构建（重放前的快照），无应用、无第 3 次 PUT。
            Assert.Equal(2, h.Server.PutCount());
            Assert.Equal(buildsAfterFailure + 1, h.Local.BuildCount);
            Assert.Equal(0, h.Local.ApplyCount);
            Assert.Equal(SyncPhase.Synced, h.Coordinator.State.Phase);
        }

        // ---------- §10.1 Coordinator 20：keyVersion 不一致 / revision 回退 ----------

        [Fact]
        public async Task Coordinator20_KeyVersionMismatch_Locks()
        {
            var h = new Harness(NewDoc("Base"));
            await h.SeedLoginAsync();
            var baseline = NewDoc("Base");
            await h.SeedVaultAsync("1", baseline, dirty: true);
            await h.ServeDocumentAsync(baseline, 1);
            h.Server.HeadKeyVersion = 2;

            await h.Coordinator.SyncNowAsync();

            Assert.Null(h.Vault.State.VaultKeyBase64);
            Assert.Equal(0, h.Server.PutCount());
            Assert.Equal(0, h.Server.GetCount());
            var state = h.Coordinator.State;
            Assert.Equal(SyncPhase.Locked, state.Phase);
            Assert.Equal(VaultStatus.Locked, state.Vault);
            Assert.Equal(2, state.KeyVersion);
            Assert.Contains("轮换", state.Message);
        }

        [Fact]
        public async Task Coordinator20_RevisionRollback_ErrorsWithoutUpload()
        {
            var h = new Harness(NewDoc("Base"));
            await h.SeedLoginAsync();
            var baseline = NewDoc("Base");
            await h.SeedVaultAsync("5", baseline, dirty: true);
            await h.ServeDocumentAsync(baseline, 3);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => h.Coordinator.SyncNowAsync());
            Assert.Contains("低于本机基线", error.Message);
            Assert.Equal(0, h.Server.PutCount());
            var state = h.Coordinator.State;
            Assert.Equal(SyncPhase.Error, state.Phase);
            Assert.Contains("低于本机基线", state.Message);
        }

        // ---------- HEAD 分支：云端保险库已被删除 ----------

        [Fact]
        public async Task SyncFlow_Head_VaultDeleted_Disables()
        {
            var h = new Harness(NewDoc("Base"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("1", NewDoc("Base"), dirty: true);
            h.Server.HeadError = "VAULT_NOT_FOUND";

            await h.Coordinator.SyncNowAsync();

            Assert.Null(h.Vault.State.VaultKeyBase64);
            Assert.Equal(MockSyncServer.VaultId, h.Vault.State.VaultId);
            Assert.Equal(0, h.Server.PutCount());
            var state = h.Coordinator.State;
            Assert.Equal(SyncPhase.Disabled, state.Phase);
            Assert.Equal(VaultStatus.Missing, state.Vault);
            Assert.Contains("已被删除", state.Message);
        }

        // ---------- Upload 特定错误清除 pending（409/版本失配/幂等键复用） ----------

        [Fact]
        public async Task Upload_RevisionConflict_ClearsPending()
        {
            // PUT 发出后远端先行（If-Match 失配）：409 上抛，pending 清除，
            // 下次同步走 ④ 拉远端（409 不在 Upload 内重试）。
            var h = new Harness(NewDoc("Changed"));
            await h.SeedLoginAsync();
            var baseline = NewDoc("Base");
            await h.SeedVaultAsync("1", baseline, dirty: true);
            await h.ServeDocumentAsync(baseline, 1);
            bool advanced = false;
            h.Server.BeforePut = async () =>
            {
                if (!advanced)
                {
                    advanced = true;
                    await h.ServeDocumentAsync(NewDoc("Remote"), 2);
                }
            };

            var error = await Assert.ThrowsAsync<ApiError>(() => h.Coordinator.SyncNowAsync());
            Assert.Equal("SYNC_REVISION_CONFLICT", error.Code);
            // 确定性 409 不作为 ambiguous pending 重放（下次同步走 ④ 拉远端）。
            Assert.Null(h.Vault.State.PendingUpload);
            Assert.Equal(SyncPhase.Error, h.Coordinator.State.Phase);
        }

        // ---------- 上传期间本地又改动 → dirty 保持并 250 ms 后再同步 ----------

        [Fact]
        public async Task UploadRace_LocalChangeDuringUpload_KeepsDirtyAndResyncsAfter250ms()
        {
            var h = new Harness(NewDoc("Changed"));
            await h.SeedLoginAsync();
            var baseline = NewDoc("Base");
            await h.SeedVaultAsync("1", baseline, dirty: true);
            await h.ServeDocumentAsync(baseline, 1);
            // 真实 250 ms 延迟：复同步在首次上传完成后才触发（立即完成的 sleep
            // 会让复同步并入仍在进行的首次任务，无法观察到第二次 PUT）。
            h.SleepOverride = ms => Task.Delay((int)ms);
            int hooks = 0;
            h.Server.BeforePut = () =>
            {
                hooks++;
                if (hooks == 1)
                {
                    // 上传 PUT 已发出、响应未回：在上传期间改动本地。
                    h.Local.Current.Servers[0].Profile.Name = "Changed-Again";
                    return h.Coordinator.NotifyLocalChangedAsync();
                }
                return Task.CompletedTask;
            };

            await h.Coordinator.SyncNowAsync();

            // 首次上传收尾检测到代际变化：dirty 保持、phase 回 idle（未 synced）。
            Assert.Equal("2", h.Vault.State.Revision);
            Assert.True(h.Vault.State.Dirty);
            Assert.Equal(SyncPhase.Idle, h.Coordinator.State.Phase);
            lock (h.Sleeps)
            {
                Assert.Contains(250L, h.Sleeps);
            }
            // 250 ms 后自动再同步一次：第二次 PUT 把期间改动送上云，最终 synced。
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (h.Server.PutCount() < 2 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
            Assert.Equal(2, h.Server.PutCount());
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (h.Coordinator.State.Phase != SyncPhase.Synced && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
            Assert.Equal(SyncPhase.Synced, h.Coordinator.State.Phase);
            Assert.Equal("3", h.Vault.State.Revision);
            Assert.False(h.Vault.State.Dirty);
            Assert.Equal("Changed-Again", (await h.ReadServerDocumentAsync()).Servers[0].Profile.Name);
        }

        // ---------- 单飞 SyncNow ----------

        [Fact]
        public async Task SyncNow_ConcurrentCalls_ShareSingleRun()
        {
            var h = new Harness(NewDoc("Changed"));
            await h.SeedLoginAsync();
            var baseline = NewDoc("Base");
            await h.SeedVaultAsync("1", baseline, dirty: true);
            await h.ServeDocumentAsync(baseline, 1);
            var gate = new TaskCompletionSource<bool>();
            h.Server.BeforePut = () => gate.Task;

            Task first = h.Coordinator.SyncNowAsync();
            Task second = h.Coordinator.SyncNowAsync();
            Assert.Same(first, second);
            await Task.Delay(200);
            Assert.Equal(1, h.Server.PutCount());

            gate.SetResult(true);
            await Task.WhenAll(first, second);
            Assert.Equal(SyncPhase.Synced, h.Coordinator.State.Phase);
        }

        // ---------- 守卫：未登录 / 未启用 / 未解锁 ----------

        [Fact]
        public async Task SyncNow_Guards_SignedOutDisabledLocked()
        {
            var unsigned = new Harness(NewDoc("Base"));
            await unsigned.Coordinator.SyncNowAsync();
            Assert.Equal(SyncPhase.SignedOut, unsigned.Coordinator.State.Phase);
            Assert.Empty(unsigned.Server.Requests);

            var disabled = new Harness(NewDoc("Base"));
            await disabled.SeedLoginAsync();
            await disabled.Vault.UpdateAsync(next =>
            {
                next.UserId = "u1";
                next.Preferences.Enabled = false;
            });
            await disabled.Coordinator.SyncNowAsync();
            Assert.Equal(SyncPhase.Disabled, disabled.Coordinator.State.Phase);
            Assert.Empty(disabled.Server.Requests);

            var locked = new Harness(NewDoc("Base"));
            await locked.SeedLoginAsync();
            await locked.Vault.UpdateAsync(next =>
            {
                next.UserId = "u1";
                next.VaultId = MockSyncServer.VaultId;
                next.KeyVersion = 1;
                next.Preferences.Enabled = true;
            });
            await locked.Coordinator.SyncNowAsync();
            Assert.Equal(SyncPhase.Locked, locked.Coordinator.State.Phase);
            Assert.Empty(locked.Server.Requests);
        }

        // ---------- 脱敏 ----------

        [Fact]
        public async Task SyncFlow_Logs_DoNotContainSecrets()
        {
            var h = new Harness(NewDoc("Local", "local-password-SECRET"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("1", NewDoc("Base"), dirty: true);
            await h.ServeDocumentAsync(NewDoc("Base"), 1);
            await h.Coordinator.SyncNowAsync();

            string all = string.Join("\n", h.Logger.Lines);
            Assert.DoesNotContain("local-password-SECRET", all);
            Assert.DoesNotContain(h.VaultKey, all);
            Assert.DoesNotContain("Local", all);
        }
    }
}
