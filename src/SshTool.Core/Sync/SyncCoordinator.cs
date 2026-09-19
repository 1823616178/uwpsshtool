using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Sync.Api;
using SshTool.Core.Sync.Api.Dtos;
using SshTool.Core.Sync.Auth;
using SshTool.Core.Sync.Vault;

namespace SshTool.Core.Sync
{
    // 解锁方式：同步密码或恢复密钥（对应桌面端 unlockVault 的 method 参数）。
    public enum VaultUnlockMethod
    {
        Password,
        Recovery
    }

    // 03-SYNC-PROTOCOL.md §7.1 初始化与认证 + §7.2 保险库操作（不含 Rotate）。
    // S11 范围（第一部分）：Initialize、注册/登录后流程、ProbeVault、
    // SetupVault（先落盘 pending 再 POST）、Unlock、Lock、DeleteVault、
    // SetPreferences（拒绝直接关闭敏感开关）、Logout、ChangeAccountPassword、
    // DeleteAccount 与 StateChanged 事件。
    //
    // 不在 S11：PerformSync/SyncNow 上传下载（S12a）、冲突与 initial-import（S12b）、
    // 轮换/历史/退避重试/MarkDirty（S13）、触发器与轮询（S14）。
    // SetupVault/Unlock 成功后只把 dirty=true（或就绪）记入缓存并转 idle/ready，
    // 实际上传由 S12a 的 SyncNow 完成。
    //
    // 与桌面端 sync-coordinator.ts 逐项对齐（含文案）：
    //   - 已登录不再重复登录（每次登录新建设备，见踩坑 #10）。
    //   - 探测是咨询性工作：失败走 ProbeVaultSafely，绝不抛回登录/初始化（见 §7.1）。
    //   - pendingVaultSetup 先持久化再发请求；重试/重启复用同一材料与幂等键。
    // 日志脱敏：只记相位与 keyVersion/revision 等计数，绝不记录密码、恢复密钥、
    // vaultKey、信封内容与请求体。
    public sealed class SyncCoordinator
    {
        private readonly AuthStore _auth;
        private readonly VaultCacheStore _vault;
        private readonly ApiClient _api;
        private readonly IVaultCrypto _crypto;
        private readonly IDeviceDescriptorProvider _devices;
        private readonly ILogger _logger;
        private readonly Func<DateTimeOffset> _clock;
        private readonly Func<string> _guid;

        private readonly object _stateLock = new object();
        private SyncState _state;

        // 每次状态变更后同步触发（调用线程；UI 层负责封送到 UI 线程）。
        public event Action<SyncState> StateChanged;

        public SyncCoordinator(
            AuthStore auth,
            VaultCacheStore vault,
            ApiClient api,
            IVaultCrypto crypto,
            IDeviceDescriptorProvider devices,
            ILogger logger = null,
            Func<DateTimeOffset> clock = null,
            Func<string> newGuid = null)
        {
            if (auth == null)
            {
                throw new ArgumentNullException(nameof(auth));
            }
            if (vault == null)
            {
                throw new ArgumentNullException(nameof(vault));
            }
            if (api == null)
            {
                throw new ArgumentNullException(nameof(api));
            }
            if (crypto == null)
            {
                throw new ArgumentNullException(nameof(crypto));
            }
            if (devices == null)
            {
                throw new ArgumentNullException(nameof(devices));
            }
            _auth = auth;
            _vault = vault;
            _api = api;
            _crypto = crypto;
            _devices = devices;
            _logger = logger;
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _guid = newGuid ?? (() => Guid.NewGuid().ToString());
            lock (_stateLock)
            {
                _state = BuildState(_auth.Session, _vault.State);
            }
        }

        // 当前 UI 可观察状态（拷贝）。
        public SyncState State
        {
            get
            {
                lock (_stateLock)
                {
                    return _state.Clone();
                }
            }
        }

        // §7.1 Initialize：读会话 → 绑定缓存 → 重建状态 → 无 vaultId 则安全探测。
        // S11 无轮询与 SyncNow（S13/S14 落地）。
        public async Task InitializeAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            var session = await _auth.LoadAsync().ConfigureAwait(false);
            await _vault.LoadAsync().ConfigureAwait(false);
            await _vault.BindToUserAsync(session.Authenticated ? session.UserId : null).ConfigureAwait(false);
            ApplySessionState(_auth.Session, _vault.State);
            if (_auth.Session.Authenticated && _vault.State.VaultId == null)
            {
                await ProbeVaultSafelyAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // 注册 → AfterAuthenticated。已登录时拒绝（避免新建多余设备）。
        public async Task<AuthState> RegisterAsync(
            string email,
            string password,
            string inviteCode = null,
            string deviceName = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            RequireCredential(email, nameof(email));
            RequireCredential(password, nameof(password));
            EnsureNotSignedIn();
            var descriptor = _devices.GetDescriptor(deviceName);
            await _api.RegisterAsync(email, password, inviteCode, descriptor, cancellationToken)
                .ConfigureAwait(false);
            await AfterAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
            Info("注册成功");
            return _auth.Session;
        }

        // 登录 → AfterAuthenticated。已登录时拒绝。
        public async Task<AuthState> LoginAsync(
            string email,
            string password,
            string deviceName = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            RequireCredential(email, nameof(email));
            RequireCredential(password, nameof(password));
            EnsureNotSignedIn();
            var descriptor = _devices.GetDescriptor(deviceName);
            await _api.LoginAsync(email, password, descriptor, cancellationToken).ConfigureAwait(false);
            await AfterAuthenticatedAsync(cancellationToken).ConfigureAwait(false);
            Info("登录成功");
            return _auth.Session;
        }

        // §7.1 ProbeVault：发现云端保险库并记下 id/keyVersion。
        // vaultId 变化 → 旧 key/baseDocument/dirty 全部失效（key 置 null，revision 回 "0"）。
        // VAULT_NOT_FOUND → missing/disabled；其他错误原样抛出（由 Safely 版承接）。
        public async Task ProbeVaultAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            VaultEnvelopeResponse response;
            try
            {
                response = await _api.GetVaultKeyEnvelopeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ApiError error)
            {
                if (error.Code == "VAULT_NOT_FOUND")
                {
                    PatchState(next =>
                    {
                        next.Vault = VaultStatus.Missing;
                        next.Phase = SyncPhase.Disabled;
                    });
                    return;
                }
                throw;
            }
            var current = _vault.State;
            if (!string.Equals(current.VaultId, response.Id, StringComparison.Ordinal))
            {
                await _vault.UpdateAsync(next =>
                {
                    next.VaultId = response.Id;
                    next.KeyVersion = response.Envelope.KeyVersion;
                    next.VaultKeyBase64 = null;
                    next.Revision = "0";
                    next.BaseDocument = null;
                    next.Dirty = false;
                }).ConfigureAwait(false);
            }
            else
            {
                await _vault.UpdateAsync(next =>
                {
                    next.KeyVersion = response.Envelope.KeyVersion;
                }).ConfigureAwait(false);
            }
            var after = _vault.State;
            PatchState(next =>
            {
                next.Vault = after.VaultKeyBase64 != null ? VaultStatus.Ready : VaultStatus.Locked;
                next.Phase = after.VaultKeyBase64 != null ? SyncPhase.Idle : SyncPhase.Locked;
                next.KeyVersion = response.Envelope.KeyVersion;
                next.Revision = after.Revision;
                next.Dirty = after.Dirty;
            });
        }

        // §7.2 SetupVault：创建保险库并返回恢复密钥。
        // 无 pending 时生成材料并先落盘再 POST；响应丢失后重试/重启复用同一 pending。
        public async Task<string> SetupVaultAsync(
            string syncPassword,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            EnsureAuthenticated();
            RequireCredential(syncPassword, nameof(syncPassword));
            PendingVaultSetup pending = _vault.State.PendingVaultSetup;
            if (pending == null)
            {
                var setup = await _crypto.CreateAsync(syncPassword, 1).ConfigureAwait(false);
                if (setup == null || setup.Envelope == null
                    || string.IsNullOrEmpty(setup.VaultKeyBase64)
                    || string.IsNullOrEmpty(setup.RecoveryKey))
                {
                    throw new InvalidOperationException("保险库创建失败，请重试");
                }
                var fresh = new PendingVaultSetup
                {
                    IdempotencyKey = _guid(),
                    VaultKeyBase64 = setup.VaultKeyBase64,
                    RecoveryKey = setup.RecoveryKey,
                    KeyEnvelope = setup.Envelope.Clone(),
                    CreatedAt = NowIso()
                };
                // 必须先持久化，再发起可能已被服务端执行但丢失响应的 POST（踩坑 #12）。
                await _vault.UpdateAsync(next =>
                {
                    next.PendingVaultSetup = fresh;
                }).ConfigureAwait(false);
                pending = _vault.State.PendingVaultSetup;
            }

            string idempotencyKey = pending.IdempotencyKey;
            string vaultKey = pending.VaultKeyBase64;
            string recoveryKey = pending.RecoveryKey;
            VaultWriteResponse created = await _api.CreateVaultAsync(
                ToData(pending.KeyEnvelope), idempotencyKey, cancellationToken).ConfigureAwait(false);

            await _vault.UpdateAsync(next =>
            {
                next.VaultId = created.Id;
                next.VaultKeyBase64 = vaultKey;
                next.KeyVersion = created.KeyVersion;
                next.Revision = "0";
                next.BaseDocument = null;
                next.Preferences.Enabled = true;
                next.Dirty = true;
                next.PendingVaultSetup = null;
            }).ConfigureAwait(false);
            var preferences = _vault.State.Preferences.Clone();
            PatchState(next =>
            {
                next.Phase = SyncPhase.Idle;
                next.Vault = VaultStatus.Ready;
                next.KeyVersion = created.KeyVersion;
                next.Revision = "0";
                next.Dirty = true;
                next.Preferences = preferences;
                next.Conflict = null;
                next.Message = "";
            });
            Info("保险库已创建");
            return recoveryKey;
        }

        // §7.2 UnlockVault：拉信封 → 解包 → 记 key 并启用 → idle/ready（同步由 S12a 接管）。
        public async Task UnlockVaultAsync(
            string secret,
            VaultUnlockMethod method,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            RequireCredential(secret, nameof(secret));
            var response = await _api.GetVaultKeyEnvelopeAsync(cancellationToken).ConfigureAwait(false);
            var domain = ToDomainOrThrow(response.Envelope);
            string key = method == VaultUnlockMethod.Password
                ? await _crypto.UnwrapWithPasswordAsync(domain, secret).ConfigureAwait(false)
                : await _crypto.UnwrapWithRecoveryKeyAsync(domain, secret).ConfigureAwait(false);
            if (string.IsNullOrEmpty(key))
            {
                throw new InvalidOperationException(
                    method == VaultUnlockMethod.Password ? "同步密码不正确" : "恢复密钥无效");
            }
            await _vault.UpdateAsync(next =>
            {
                next.VaultId = response.Id;
                next.VaultKeyBase64 = key;
                next.KeyVersion = response.Envelope.KeyVersion;
                next.Preferences.Enabled = true;
            }).ConfigureAwait(false);
            var preferences = _vault.State.Preferences.Clone();
            PatchState(next =>
            {
                next.Phase = SyncPhase.Idle;
                next.Vault = VaultStatus.Ready;
                next.KeyVersion = response.Envelope.KeyVersion;
                next.Preferences = preferences;
                next.Message = "";
            });
            Info("保险库已解锁");
        }

        // §7.2 LockVault：仅清 key。
        public async Task LockVaultAsync()
        {
            await _vault.LockAsync().ConfigureAwait(false);
            PatchState(next =>
            {
                next.Vault = VaultStatus.Locked;
                next.Phase = SyncPhase.Locked;
                next.Message = "";
            });
        }

        // §7.2 DeleteVault：删云端库与历史 → 本地 Clear（保留 userId）→ disabled。
        public async Task DeleteVaultAsync(
            string currentPassword,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            RequireCredential(currentPassword, nameof(currentPassword));
            await _api.DeleteVaultAsync(currentPassword, cancellationToken).ConfigureAwait(false);
            await _vault.ClearAsync().ConfigureAwait(false);
            var session = _auth.Session;
            if (session.Authenticated && !string.IsNullOrEmpty(session.UserId))
            {
                await _vault.BindToUserAsync(session.UserId).ConfigureAwait(false);
            }
            var cache = _vault.State;
            var preferences = cache.Preferences.Clone();
            PatchState(next =>
            {
                next.Phase = session.Authenticated ? SyncPhase.Disabled : SyncPhase.SignedOut;
                next.Vault = VaultStatus.Missing;
                next.Preferences = preferences;
                next.Revision = "0";
                next.KeyVersion = 0;
                next.Dirty = false;
                next.LastSyncedAt = null;
                next.NextRetryAt = null;
                next.Conflict = null;
                next.Message = "云端保险库和历史版本已删除，本机配置仍保留";
            });
            Info("云端保险库已删除");
        }

        // §7.2 SetPreferences：关闭已开启的敏感开关必须走轮换（S13），此处直接拒绝。
        public async Task SetPreferencesAsync(SyncPreferences preferences)
        {
            if (preferences == null)
            {
                throw new ArgumentNullException(nameof(preferences));
            }
            var previous = _vault.State.Preferences;
            if ((previous.SyncPasswords && !preferences.SyncPasswords)
                || (previous.SyncPrivateKeys && !preferences.SyncPrivateKeys))
            {
                throw new InvalidOperationException("关闭敏感同步需要执行密钥轮换，请在“安全清理”流程中输入账号密码");
            }
            var snapshot = preferences.Clone();
            await _vault.UpdateAsync(next =>
            {
                next.Preferences = snapshot;
                next.Dirty = true;
            }).ConfigureAwait(false);
            var after = _vault.State;
            var statePreferences = after.Preferences.Clone();
            PatchState(next =>
            {
                next.Preferences = statePreferences;
                next.Dirty = true;
                next.Phase = statePreferences.Enabled
                    ? (after.VaultKeyBase64 != null ? SyncPhase.Idle : SyncPhase.Locked)
                    : SyncPhase.Disabled;
            });
        }

        // §7.1 Logout：服务端失败也清本地（finally），失败原样上抛。
        public async Task LogoutAsync(
            bool all = false,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var session = _auth.Session;
            try
            {
                if (session.Authenticated)
                {
                    if (all)
                    {
                        await _api.LogoutAllAsync(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await _api.LogoutAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                Info("已退出登录");
            }
            finally
            {
                // 服务端失败也清本地（patchState 必须在 finally 内：失败时状态同样回到 signed_out）。
                await _auth.ClearAsync().ConfigureAwait(false);
                await _vault.LockAsync().ConfigureAwait(false);
                var cache = _vault.State;
                PatchState(next =>
                {
                    next.Phase = SyncPhase.SignedOut;
                    next.Vault = cache.VaultId != null ? VaultStatus.Locked : VaultStatus.Missing;
                    next.Message = "";
                });
            }
        }

        // §7.1 ChangeAccountPassword：成功后清本地（用新密码重登），失败不清。
        public async Task ChangeAccountPasswordAsync(
            string currentPassword,
            string newPassword,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            RequireCredential(currentPassword, nameof(currentPassword));
            RequireCredential(newPassword, nameof(newPassword));
            await _api.ChangePasswordAsync(currentPassword, newPassword, cancellationToken)
                .ConfigureAwait(false);
            await _auth.ClearAsync().ConfigureAwait(false);
            await _vault.LockAsync().ConfigureAwait(false);
            var cache = _vault.State;
            PatchState(next =>
            {
                next.Phase = SyncPhase.SignedOut;
                next.Vault = cache.VaultId != null ? VaultStatus.Locked : VaultStatus.Missing;
                next.Message = "密码已修改，请使用新密码重新登录";
            });
            Info("登录密码已修改，需重新登录");
        }

        // §7.1 DeleteAccount：成功后清 Auth 与整个 VaultCache → signed_out。
        public async Task DeleteAccountAsync(
            string currentPassword,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            RequireCredential(currentPassword, nameof(currentPassword));
            await _api.DeleteAccountAsync(currentPassword, cancellationToken).ConfigureAwait(false);
            await _auth.ClearAsync().ConfigureAwait(false);
            await _vault.ClearAsync().ConfigureAwait(false);
            PatchState(next =>
            {
                next.Phase = SyncPhase.SignedOut;
                next.Vault = VaultStatus.Missing;
                next.Preferences = SyncPreferences.Defaults();
                next.Revision = "0";
                next.KeyVersion = 0;
                next.Dirty = false;
                next.LastSyncedAt = null;
                next.NextRetryAt = null;
                next.Conflict = null;
                next.Message = "";
            });
            Info("账号已注销");
        }

        // ---------- 内部流程 ----------

        private async Task AfterAuthenticatedAsync(CancellationToken cancellationToken)
        {
            var session = _auth.Session;
            await _vault.BindToUserAsync(session.Authenticated ? session.UserId : null)
                .ConfigureAwait(false);
            ApplySessionState(_auth.Session, _vault.State);
            if (_auth.Session.Authenticated && _vault.State.VaultId == null)
            {
                await ProbeVaultSafelyAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // 探测是咨询性工作：瞬断绝不拒绝认证、不抛回调用方（见桌面端 probeVaultSafely）。
        private async Task ProbeVaultSafelyAsync(CancellationToken cancellationToken)
        {
            try
            {
                await ProbeVaultAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                await HandleProbeErrorAsync(error).ConfigureAwait(false);
            }
        }

        // S11 的最小错误映射（完整退避重试在 S13 落地）：
        // 终端鉴权 → 清会话 + 锁库 + auth_error；已无会话 → signed_out；
        // 其他 → offline（网络/超时）或 error，保留登录会话。
        private async Task HandleProbeErrorAsync(Exception error)
        {
            var apiError = error as ApiError;
            if (apiError != null && IsTerminalAuthError(apiError))
            {
                await _auth.ClearAsync().ConfigureAwait(false);
                await _vault.LockAsync().ConfigureAwait(false);
                var cache = _vault.State;
                PatchState(next =>
                {
                    next.Phase = SyncPhase.AuthError;
                    next.Vault = cache.VaultId != null ? VaultStatus.Locked : VaultStatus.Missing;
                    next.Message = FormatErrorMessage(apiError);
                    next.NextRetryAt = null;
                });
                return;
            }
            if (!_auth.Session.Authenticated)
            {
                await _vault.LockAsync().ConfigureAwait(false);
                var cache = _vault.State;
                PatchState(next =>
                {
                    next.Phase = SyncPhase.SignedOut;
                    next.Vault = cache.VaultId != null ? VaultStatus.Locked : VaultStatus.Missing;
                    next.Message = FormatErrorMessage(error);
                    next.NextRetryAt = null;
                });
                return;
            }
            bool offline = apiError != null
                && (apiError.Kind == ApiErrorKind.Network || apiError.Kind == ApiErrorKind.Timeout);
            PatchState(next =>
            {
                next.Phase = offline ? SyncPhase.Offline : SyncPhase.Error;
                next.Message = FormatErrorMessage(error);
                next.NextRetryAt = null;
            });
        }

        private void ApplySessionState(AuthState session, VaultCacheState cache)
        {
            var rebuilt = BuildState(session, cache);
            PatchState(next =>
            {
                next.Phase = rebuilt.Phase;
                next.Vault = rebuilt.Vault;
                next.Preferences = rebuilt.Preferences;
                next.Revision = rebuilt.Revision;
                next.KeyVersion = rebuilt.KeyVersion;
                next.Dirty = rebuilt.Dirty;
                next.LastSyncedAt = rebuilt.LastSyncedAt;
                next.NextRetryAt = null;
                next.Conflict = rebuilt.Conflict;
                next.Message = "";
            });
        }

        // §7.1 状态公式：未登录 → signed_out；已登录 → enabled ?
        // (vaultKey ? idle : locked) : disabled；vault = vaultKey ? ready : (vaultId ? locked : missing)。
        private static SyncState BuildState(AuthState session, VaultCacheState cache)
        {
            SyncPreferences preferences = cache.Preferences == null
                ? SyncPreferences.Defaults()
                : cache.Preferences.Clone();
            var state = new SyncState
            {
                Preferences = preferences,
                Revision = cache.Revision ?? "0",
                KeyVersion = cache.KeyVersion,
                Dirty = cache.Dirty,
                LastSyncedAt = cache.LastSyncedAt,
                NextRetryAt = null,
                Message = "",
                Conflict = cache.Conflict == null ? null : cache.Conflict.Clone()
            };
            if (session == null || !session.Authenticated)
            {
                state.Phase = SyncPhase.SignedOut;
                state.Vault = VaultStatus.Missing;
                return state;
            }
            state.Vault = cache.VaultKeyBase64 != null
                ? VaultStatus.Ready
                : (cache.VaultId != null ? VaultStatus.Locked : VaultStatus.Missing);
            if (!preferences.Enabled)
            {
                state.Phase = SyncPhase.Disabled;
            }
            else
            {
                state.Phase = cache.VaultKeyBase64 != null ? SyncPhase.Idle : SyncPhase.Locked;
            }
            return state;
        }

        private void PatchState(Action<SyncState> mutate)
        {
            SyncState snapshot;
            lock (_stateLock)
            {
                var next = _state.Clone();
                mutate(next);
                _state = next;
                snapshot = next.Clone();
            }
            var handler = StateChanged;
            if (handler != null)
            {
                handler(snapshot);
            }
        }

        private static bool IsTerminalAuthError(ApiError error)
        {
            if (error.Kind != ApiErrorKind.Authentication)
            {
                return false;
            }
            return error.Code == "AUTH_DEVICE_REVOKED"
                || error.Code == "AUTH_TOKEN_REUSED"
                || error.Code == "AUTH_REFRESH_UNAVAILABLE"
                || error.Status == 401;
        }

        private static string FormatErrorMessage(Exception error)
        {
            var apiError = error as ApiError;
            if (apiError != null && !string.IsNullOrEmpty(apiError.RequestId))
            {
                return apiError.Message + "（请求 ID：" + apiError.RequestId + "）";
            }
            if (error != null && error.Message != null)
            {
                return error.Message;
            }
            return "同步请求失败";
        }

        private void EnsureAuthenticated()
        {
            if (!_auth.Session.Authenticated)
            {
                throw new InvalidOperationException("请先登录");
            }
        }

        private void EnsureNotSignedIn()
        {
            if (_auth.Session.Authenticated)
            {
                throw new InvalidOperationException("已登录，请先退出登录后再继续");
            }
        }

        private static void RequireCredential(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(name + "不能为空", name);
            }
        }

        private string NowIso()
        {
            return _clock().ToUniversalTime().ToString(
                "yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        private void Info(string message)
        {
            if (_logger != null)
            {
                _logger.Log(LogLevel.Info, "Sync", message);
            }
        }

        // ---------- 信封 DTO ↔ 域模型 ----------

        private static VaultKeyEnvelopeData ToData(VaultKeyEnvelope envelope)
        {
            return new VaultKeyEnvelopeData
            {
                KeyVersion = envelope.KeyVersion,
                PasswordWrappedKey = envelope.PasswordWrappedKey,
                PasswordWrapNonce = envelope.PasswordWrapNonce,
                RecoveryWrappedKey = envelope.RecoveryWrappedKey,
                RecoveryWrapNonce = envelope.RecoveryWrapNonce,
                KdfSalt = envelope.KdfSalt,
                KdfParameters = new KdfParametersData
                {
                    Algorithm = envelope.KdfAlgorithm,
                    Memory = envelope.KdfMemory,
                    Iterations = envelope.KdfIterations,
                    Parallelism = envelope.KdfParallelism
                }
            };
        }

        // KDF 参数一律来自信封并校验范围（踩坑 #1：禁止写死常量；越界视为无效信封）。
        private static VaultKeyEnvelope ToDomainOrThrow(VaultKeyEnvelopeData data)
        {
            if (data == null || data.KdfParameters == null)
            {
                throw new InvalidOperationException("保险库信封无效");
            }
            var envelope = new VaultKeyEnvelope
            {
                KeyVersion = data.KeyVersion,
                PasswordWrappedKey = data.PasswordWrappedKey,
                PasswordWrapNonce = data.PasswordWrapNonce,
                RecoveryWrappedKey = data.RecoveryWrappedKey,
                RecoveryWrapNonce = data.RecoveryWrapNonce,
                KdfSalt = data.KdfSalt,
                KdfAlgorithm = data.KdfParameters.Algorithm,
                KdfMemory = data.KdfParameters.Memory,
                KdfIterations = data.KdfParameters.Iterations,
                KdfParallelism = data.KdfParameters.Parallelism
            };
            if (envelope.KeyVersion < 1 || !envelope.HasValidKdf()
                || string.IsNullOrEmpty(envelope.PasswordWrappedKey)
                || string.IsNullOrEmpty(envelope.PasswordWrapNonce)
                || string.IsNullOrEmpty(envelope.RecoveryWrappedKey)
                || string.IsNullOrEmpty(envelope.RecoveryWrapNonce)
                || string.IsNullOrEmpty(envelope.KdfSalt))
            {
                throw new InvalidOperationException("保险库信封无效");
            }
            return envelope;
        }
    }
}
