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
    // S12b 验收：§10.1 Coordinator 7、14、15、16、17、18、21（单测名称对齐 §10.1）。
    // 范围：SaveConflict/initial-import/remote-deletion/RemoteDeletionConflicts/
    // ResolveConflict（use-remote/keep-local）+ 冲突持久化与重启恢复。
    // S13 范围（Rotate/Restore/退避重试/MarkDirty 防抖）不在此文件。
    public class SyncCoordinatorConflictTests
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

        // 假本地适配器：与 SyncCoordinatorSyncTests 同构，另加 ApplyGuard
        // 模拟 §5.2 两道保护（指纹变化 / 运行中隧道变更时抛错，整份不应用）。
        private sealed class FakeLocalAdapter : ISyncLocalPort
        {
            public SyncDocumentV1 Current;
            public int BuildCount;
            public int ApplyCount;
            public readonly List<SyncDocumentV1> Applied = new List<SyncDocumentV1>();
            public Func<SyncDocumentV1, Task> ApplyGuard;

            public Task<SyncDocumentV1> BuildLocalDocumentAsync(SyncPreferencesV1 preferences)
            {
                BuildCount++;
                var snapshot = Current.Clone();
                snapshot.Preferences = preferences.Clone();
                return Task.FromResult(snapshot);
            }

            public async Task ApplyDocumentAsync(SyncDocumentV1 document)
            {
                var guard = ApplyGuard;
                if (guard != null)
                {
                    await guard(document).ConfigureAwait(false);
                }
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
            }
        }

        // 与 SyncCoordinatorSyncTests 同构的 MockSyncService 分组。
        private sealed class MockSyncServer
        {
            public const string VaultId = "vault-test-1";

            public string DocumentBody;
            public int Revision;
            public int DocumentKeyVersion = 1;
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
                        { "x-sync-revision", Revision.ToString() },
                        { "x-key-version", DocumentKeyVersion.ToString() },
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

            public Harness(SyncDocumentV1 localDoc)
            {
                Auth = new AuthStore(AuthFile);
                Vault = new VaultCacheStore(VaultFile, Logger);
                Transport.AsyncHandler = Server.HandleAsync;
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
                        return Task.CompletedTask;
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

            // 重启恢复：同一文件重载 VaultCache，并用它构造新协调器。
            public async Task<SyncCoordinator> RestartCoordinatorAsync()
            {
                var reloaded = new VaultCacheStore(VaultFile, Logger);
                await reloaded.LoadAsync();
                return new SyncCoordinator(
                    Auth, reloaded, Api, Crypto, new StubDevices(), Logger,
                    null, null, Local,
                    ms => Task.CompletedTask);
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

        // 含主机 + 隧道 + 分组的完整文档（remote-deletion 需覆盖三类实体）。
        private static SyncDocumentV1 FullDoc(string serverName)
        {
            var doc = NewDoc(serverName);
            doc.Groups.Add(new GroupRecord { Id = "group-1", Name = "G1", Color = "#4F8CFF" });
            doc.Tunnels.Add(new TunnelRecord
            {
                Id = "tunnel-1",
                Name = "T1",
                ServerId = "server-1",
                GroupId = "group-1",
                Type = TunnelType.Local,
                ListenHost = "127.0.0.1",
                ListenPort = 8080,
                DestHost = "internal",
                DestPort = 80,
                DestServerId = "",
                AutoReconnect = true,
                Enabled = true
            });
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

        // ---------- §10.1 Coordinator 21：首次同步本机/云端都有数据 → initial-import ----------

        [Fact]
        public async Task Coordinator21_InitialImport_UseRemote()
        {
            var h = new Harness(NewDoc("Fresh device"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("0", null, dirty: false);
            await h.ServeDocumentAsync(NewDoc("Cloud source"), 1);

            await h.Coordinator.SyncNowAsync();

            var state = h.Coordinator.State;
            Assert.Equal(SyncPhase.Conflict, state.Phase);
            Assert.NotNull(state.Conflict);
            Assert.Equal(SyncConflictReason.InitialImport, state.Conflict.Reason);
            Assert.Equal("0", state.Conflict.LocalRevision);
            Assert.Equal("1", state.Conflict.RemoteRevision);
            Assert.Single(state.Conflict.Fields);
            Assert.Equal(SyncConflictEntity.Settings, state.Conflict.Fields[0].Entity);
            Assert.Equal("initial-import", state.Conflict.Fields[0].Id);
            Assert.Equal("*", state.Conflict.Fields[0].Field);
            Assert.False(state.Conflict.Fields[0].Sensitive);
            Assert.Equal(1, state.Conflict.RemoteSummary.Servers);
            Assert.Contains("首次同步", state.Message);
            // 冲突前不应用、不上传。
            Assert.Equal("Fresh device", h.Local.Current.Servers[0].Profile.Name);
            Assert.Equal(0, h.Server.PutCount());

            await h.Coordinator.ResolveConflictAsync(SyncNowStrategy.UseRemote);

            Assert.Equal("Cloud source", h.Local.Current.Servers[0].Profile.Name);
            var cache = h.Vault.State;
            Assert.Equal("1", cache.Revision);
            Assert.False(cache.Dirty);
            Assert.Null(cache.Conflict);
            Assert.Null(cache.ConflictRemoteDocument);
            Assert.Null(cache.ConflictRemoteRevision);
            state = h.Coordinator.State;
            Assert.Equal(SyncPhase.Synced, state.Phase);
            Assert.Equal("1", state.Revision);
            Assert.Null(state.Conflict);
        }

        [Fact]
        public async Task Coordinator21_InitialImport_KeepLocalUploadsOverRemote()
        {
            var h = new Harness(NewDoc("Fresh device"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("0", null, dirty: false);
            await h.ServeDocumentAsync(NewDoc("Cloud source"), 1);
            await h.Coordinator.SyncNowAsync();
            Assert.Equal(SyncPhase.Conflict, h.Coordinator.State.Phase);

            await h.Coordinator.ResolveConflictAsync(SyncNowStrategy.KeepLocal);

            Assert.Equal(1, h.Server.PutCount());
            Assert.Equal("\"revision-1\"", HeaderOf(h.Server.LastPut(), "If-Match"));
            Assert.Equal("Fresh device", (await h.ReadServerDocumentAsync()).Servers[0].Profile.Name);
            var cache = h.Vault.State;
            Assert.Equal("2", cache.Revision);
            Assert.False(cache.Dirty);
            Assert.Null(cache.Conflict);
            Assert.Equal(SyncPhase.Synced, h.Coordinator.State.Phase);
        }

        [Fact]
        public async Task Coordinator21_InitialImport_SecondBranch_KeepsLocalId()
        {
            // base 为 null 且以 keep-local 策略同步（远端在冲突期间又前进）：
            // 走第二 initial-import 分支（id "initial"，见 §7.3）。
            var h = new Harness(NewDoc("Fresh device"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("0", null, dirty: true);
            await h.ServeDocumentAsync(NewDoc("Cloud source"), 1);

            await h.Coordinator.SyncNowAsync(
                new SyncNowInput { Strategy = SyncNowStrategy.KeepLocal });

            var state = h.Coordinator.State;
            Assert.Equal(SyncPhase.Conflict, state.Phase);
            Assert.Equal(SyncConflictReason.InitialImport, state.Conflict.Reason);
            Assert.Equal("initial", state.Conflict.Fields[0].Id);
        }

        // ---------- §10.1 Coordinator 7：干净设备应用远端删除前要求确认 ----------

        [Fact]
        public async Task Coordinator07_RemoteDeletion_RequiresConfirmation()
        {
            var h = new Harness(FullDoc("Base"));
            await h.SeedLoginAsync();
            var baseline = FullDoc("Base");
            await h.SeedVaultAsync("1", baseline, dirty: false);
            // 远端删空主机与隧道（保留分组，与桌面端 remote-deletion 用例同形）。
            var emptied = new SyncDocumentV1
            {
                SchemaVersion = SyncConstants.SchemaVersion,
                UpdatedAt = "2026-09-19T09:00:00.000Z",
                Preferences = new SyncPreferencesV1 { SyncPasswords = false, SyncPrivateKeys = false }
            };
            emptied.Groups.Add(new GroupRecord { Id = "group-1", Name = "G1", Color = "#4F8CFF" });
            await h.ServeDocumentAsync(emptied, 2);

            await h.Coordinator.SyncNowAsync();

            var state = h.Coordinator.State;
            Assert.Equal(SyncPhase.Conflict, state.Phase);
            Assert.Equal(SyncConflictReason.RemoteDeletion, state.Conflict.Reason);
            Assert.Equal("1", state.Conflict.LocalRevision);
            Assert.Equal("2", state.Conflict.RemoteRevision);
            Assert.Equal(0, state.Conflict.RemoteSummary.Servers);
            Assert.Equal(0, state.Conflict.RemoteSummary.Tunnels);
            Assert.Equal(1, state.Conflict.RemoteSummary.Groups);
            Assert.Contains("删除", state.Message);
            var fields = state.Conflict.Fields;
            Assert.Contains(fields, f => f.Entity == SyncConflictEntity.Server
                && f.Id == "server-1" && f.Field == "*" && f.Sensitive);
            Assert.Contains(fields, f => f.Entity == SyncConflictEntity.Tunnel
                && f.Id == "tunnel-1" && f.Field == "*" && !f.Sensitive);
            // 确认前本地不动、不上传。
            Assert.Single(h.Local.Current.Servers);
            Assert.Single(h.Local.Current.Tunnels);
            Assert.Equal(0, h.Server.PutCount());

            // 冲突摘要持久化：同一文件重载后仍在。
            var reloaded = await new VaultCacheStore(h.VaultFile, h.Logger).LoadAsync();
            Assert.NotNull(reloaded.Conflict);
            Assert.Equal(SyncConflictReason.RemoteDeletion, reloaded.Conflict.Reason);
            Assert.Equal("2", reloaded.ConflictRemoteRevision);
            Assert.NotNull(reloaded.ConflictRemoteDocument);
            Assert.Empty(reloaded.ConflictRemoteDocument.Servers);

            // 重启恢复：新协调器按公式重建相位（idle，与桌面端构造器同构），
            // 冲突数据完整恢复仍可解决；下次同步重新进入 conflict。
            var restarted = await h.RestartCoordinatorAsync();
            Assert.Equal(SyncPhase.Idle, restarted.State.Phase);
            Assert.NotNull(restarted.State.Conflict);
            Assert.Equal(SyncConflictReason.RemoteDeletion, restarted.State.Conflict.Reason);
            Assert.Equal("2", restarted.State.Conflict.RemoteRevision);

            await restarted.SyncNowAsync();
            Assert.Equal(SyncPhase.Conflict, restarted.State.Phase);

            await restarted.ResolveConflictAsync(SyncNowStrategy.UseRemote);

            Assert.Empty(h.Local.Current.Servers);
            Assert.Empty(h.Local.Current.Tunnels);
            Assert.Equal(SyncPhase.Synced, restarted.State.Phase);
            Assert.Equal("2", restarted.State.Revision);
        }

        // ---------- §10.1 Coordinator 14：乐观锁 409 先报错，重试时检测到三方冲突 ----------

        [Fact]
        public async Task Coordinator14_RevisionConflict_RetryDetectsMergeConflict()
        {
            var h = new Harness(NewDoc("Local edit", "B-local-password"));
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
                    await h.ServeDocumentAsync(NewDoc("Remote edit"), 2);
                }
            };

            var error = await Assert.ThrowsAsync<ApiError>(() => h.Coordinator.SyncNowAsync());
            Assert.Equal("SYNC_REVISION_CONFLICT", error.Code);
            // 确定性 409 不作为 ambiguous pending 重放。
            Assert.Null(h.Vault.State.PendingUpload);
            Assert.Equal(SyncPhase.Error, h.Coordinator.State.Phase);

            await h.Coordinator.SyncNowAsync();

            var state = h.Coordinator.State;
            Assert.Equal(SyncPhase.Conflict, state.Phase);
            Assert.Equal(SyncConflictReason.MergeConflict, state.Conflict.Reason);
            Assert.Contains(state.Conflict.Fields, f => f.Entity == SyncConflictEntity.Server
                && f.Id == "server-1" && f.Field == "profile.name");
            Assert.Contains("确认", state.Message);
            // 冲突前不应用远端。
            Assert.Equal("Local edit", h.Local.Current.Servers[0].Profile.Name);
            // 冲突摘要不含值（脱敏）。
            string summaryJson = state.Conflict.ToJson().ToString(Formatting.None);
            Assert.DoesNotContain("B-local-password", summaryJson);
            Assert.DoesNotContain("Local edit", summaryJson);
            Assert.DoesNotContain("Remote edit", summaryJson);
        }

        // ---------- §10.1 Coordinator 15：冲突选择 use-remote 后保留本机凭据 ----------

        [Fact]
        public async Task Coordinator15_ResolveConflict_UseRemote_RetainsLocalSecrets()
        {
            var h = new Harness(NewDoc("Local edit", "B-local-password"));
            await h.SeedLoginAsync();
            var baseline = NewDoc("Base");
            await h.SeedVaultAsync("1", baseline, dirty: true);
            await h.ServeDocumentAsync(NewDoc("Remote edit"), 2);
            await h.Coordinator.SyncNowAsync();
            Assert.Equal(SyncPhase.Conflict, h.Coordinator.State.Phase);

            await h.Coordinator.ResolveConflictAsync(SyncNowStrategy.UseRemote);

            Assert.Equal("Remote edit", h.Local.Current.Servers[0].Profile.Name);
            Assert.Equal("B-local-password", h.Local.Current.Servers[0].Secrets.Password);
            var cache = h.Vault.State;
            Assert.Equal("2", cache.Revision);
            Assert.False(cache.Dirty);
            Assert.Null(cache.Conflict);
            Assert.Null(cache.ConflictRemoteDocument);
            Assert.Null(cache.ConflictRemoteRevision);
            var state = h.Coordinator.State;
            Assert.Equal(SyncPhase.Synced, state.Phase);
            Assert.Equal("2", state.Revision);
            Assert.Null(state.Conflict);
        }

        // ---------- §10.1 Coordinator 16：冲突选择 keep-local 后以远端 revision 为基准上传 ----------

        [Fact]
        public async Task Coordinator16_ResolveConflict_KeepLocal_UploadsOverRemoteRevision()
        {
            var h = new Harness(NewDoc("Local edit", "B-local-password"));
            await h.SeedLoginAsync();
            var baseline = NewDoc("Base");
            await h.SeedVaultAsync("1", baseline, dirty: true);
            await h.ServeDocumentAsync(NewDoc("Remote edit"), 2);
            await h.Coordinator.SyncNowAsync();
            Assert.Equal(SyncPhase.Conflict, h.Coordinator.State.Phase);

            await h.Coordinator.ResolveConflictAsync(SyncNowStrategy.KeepLocal);

            Assert.Equal(3, h.Server.Revision);
            Assert.Equal("Local edit", (await h.ReadServerDocumentAsync()).Servers[0].Profile.Name);
            var lastPut = h.Server.LastPut();
            Assert.Equal("\"revision-2\"", HeaderOf(lastPut, "If-Match"));
            var cache = h.Vault.State;
            Assert.Equal("3", cache.Revision);
            Assert.False(cache.Dirty);
            Assert.Null(cache.Conflict);
            var state = h.Coordinator.State;
            Assert.Equal(SyncPhase.Synced, state.Phase);
            Assert.Equal("3", state.Revision);
        }

        // ---------- §10.1 Coordinator 17/18：远端替换的安全边界 ----------

        [Fact]
        public async Task Coordinator17_RemoteFingerprintChange_RejectsReplacement()
        {
            var h = new Harness(NewDoc("Base"));
            await h.SeedLoginAsync();
            var baseline = NewDoc("Base");
            await h.SeedVaultAsync("1", baseline, dirty: false);
            await h.ServeDocumentAsync(NewDoc("Remote"), 2);
            h.Local.ApplyGuard = doc =>
            {
                throw new InvalidOperationException("服务器「Base」主机指纹发生变化，需要手动确认");
            };

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => h.Coordinator.SyncNowAsync());
            Assert.Contains("主机指纹发生变化", error.Message);
            Assert.Equal(0, h.Server.PutCount());
            Assert.Equal("1", h.Vault.State.Revision);
            Assert.Equal("Base", h.Vault.State.BaseDocument.Servers[0].Profile.Name);
            var state = h.Coordinator.State;
            Assert.Equal(SyncPhase.Error, state.Phase);
            Assert.Contains("主机指纹发生变化", state.Message);
        }

        [Fact]
        public async Task Coordinator18_RunningTunnelChange_RejectsReplacement()
        {
            var h = new Harness(FullDoc("Base"));
            await h.SeedLoginAsync();
            var baseline = FullDoc("Base");
            await h.SeedVaultAsync("1", baseline, dirty: false);
            var remote = FullDoc("Base");
            remote.Tunnels[0].DestPort = 443;
            await h.ServeDocumentAsync(remote, 2);
            h.Local.ApplyGuard = doc =>
            {
                throw new InvalidOperationException("运行中的隧道「T1」涉及远端连接变更，请先停止后重试");
            };

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => h.Coordinator.SyncNowAsync());
            Assert.Contains("请先停止后重试", error.Message);
            Assert.Equal(0, h.Server.PutCount());
            Assert.Equal("1", h.Vault.State.Revision);
            var state = h.Coordinator.State;
            Assert.Equal(SyncPhase.Error, state.Phase);
            Assert.Contains("请先停止后重试", state.Message);
        }

        // ---------- ResolveConflict 守卫与脱敏 ----------

        [Fact]
        public async Task ResolveConflict_WithoutPendingConflict_Throws()
        {
            var h = new Harness(NewDoc("Base"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("1", NewDoc("Base"), dirty: false);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => h.Coordinator.ResolveConflictAsync(SyncNowStrategy.UseRemote));
            Assert.Contains("没有待处理的同步冲突", error.Message);
        }

        [Fact]
        public async Task ResolveConflict_InvalidStrategy_Throws()
        {
            var h = new Harness(NewDoc("Base"));
            await h.SeedLoginAsync();
            await h.SeedVaultAsync("1", NewDoc("Base"), dirty: false);

            await Assert.ThrowsAsync<ArgumentException>(
                () => h.Coordinator.ResolveConflictAsync(SyncNowStrategy.None));
        }

        [Fact]
        public async Task Conflict_LogsAndSummary_DoNotContainSecrets()
        {
            var h = new Harness(NewDoc("Local edit", "local-password-SECRET"));
            await h.SeedLoginAsync();
            var baseline = NewDoc("Base");
            await h.SeedVaultAsync("1", baseline, dirty: true);
            await h.ServeDocumentAsync(NewDoc("Remote edit"), 2);
            await h.Coordinator.SyncNowAsync();
            Assert.Equal(SyncPhase.Conflict, h.Coordinator.State.Phase);

            string logs = string.Join("\n", h.Logger.Lines);
            Assert.DoesNotContain("local-password-SECRET", logs);
            Assert.DoesNotContain(h.VaultKey, logs);
            string summaryJson = h.Coordinator.State.Conflict.ToJson().ToString(Formatting.None);
            Assert.DoesNotContain("local-password-SECRET", summaryJson);
            Assert.DoesNotContain("Local edit", summaryJson);
            Assert.DoesNotContain("Remote edit", summaryJson);
        }
    }
}
