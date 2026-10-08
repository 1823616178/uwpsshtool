using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SshTool.Core.Common;
using SshTool.Core.Sessions;
using SshTool.Core.Storage;
using SshTool.Core.Sync.Api;
using SshTool.Core.Sync.Api.Dtos;
using SshTool.Core.Sync.Auth;
using SshTool.Core.Sync.Protocol;
using SshTool.Core.Sync.Vault;

namespace SshTool.Core.Sync
{
    // 解锁方式：同步密码或恢复密钥（对应桌面端 unlockVault 的 method 参数）。
    public enum VaultUnlockMethod
    {
        Password,
        Recovery
    }

    // S12a：协调器↔本地文档的最小端口（SyncLocalAdapter 已实现；单测用假实现）。
    // Build 读本地仓库快照并按 VaultCache 偏好输出 secrets；Apply 整份替换
    //（ChangeOrigin.Sync，不再标脏，避免上传回环，见 §5.2/踩坑 #13）。
    public interface ISyncLocalPort
    {
        Task<SyncDocumentV1> BuildLocalDocumentAsync(SyncPreferencesV1 preferences);

        Task ApplyDocumentAsync(SyncDocumentV1 document);
    }

    // SyncNow 的冲突解决策略（桌面端 SyncNowInput.strategy）。
    // None = 常规同步；KeepLocal/UseRemote 只由 S12b 的 ResolveConflict 传入。
    public enum SyncNowStrategy
    {
        None,
        KeepLocal,
        UseRemote
    }

    public sealed class SyncNowInput
    {
        public SyncNowStrategy Strategy { get; set; }
    }

    // 03-SYNC-PROTOCOL.md §7.1 初始化与认证 + §7.2 保险库操作（含 Rotate）
    // + §7.3 同步主流程与上传（S12a：单飞 SyncNow、pendingUpload 重放、HEAD 分支、
    // 无新版本上传条件、有新版本干净应用/脏合并上传、Upload/CommitRemote；
    // S12b：SaveConflict/initial-import/remote-deletion/RemoteDeletionConflicts/ResolveConflict；
    // S13：RotateVaultKey/RestoreRevision/ClearRevisions/List 透传、MarkDirty 防抖、
    // HandleSyncError 退避重试与 ITimerFactory）。
    // S11 范围（第一部分）：Initialize、注册/登录后流程、ProbeVault、
    // SetupVault（先落盘 pending 再 POST）、Unlock、Lock、DeleteVault、
    // SetPreferences（拒绝直接关闭敏感开关）、Logout、ChangeAccountPassword、
    // DeleteAccount 与 StateChanged 事件。
    //
    // S14（触发器与应用接线）不在此文件：MarkDirty 只负责标脏 + 防抖调度，
    // 仓库 Changed 接线与轮询由 SyncTriggers 落地。
    // SetupVault/Unlock 成功后只把 dirty=true（或就绪）记入缓存并转 idle/ready，
    // 首次加密上传由 SyncNow 完成（与桌面端 setupVault 尾部 syncNow 对齐）。
    //
    // 与桌面端 sync-coordinator.ts 逐项对齐（含文案），两处故意差异：
    //   - MarkDirty 防抖 3000 ms（以 §7.3 为准；桌面端同名方法为 1200 ms）。
    //   - 定时经 ITimerFactory（复用 SessionManager 的接口；桌面端用 setTimeout）。
    //
    // 日志脱敏：只记相位与 keyVersion/revision 等计数，绝不记录密码、恢复密钥、
    // vaultKey、信封内容与请求体。
    public sealed class SyncCoordinator : IDisposable, ISyncTriggerTarget
    {
        private readonly AuthStore _auth;
        private readonly VaultCacheStore _vault;
        private readonly ApiClient _api;
        private readonly IVaultCrypto _crypto;
        private readonly IDeviceDescriptorProvider _devices;
        private readonly ILogger _logger;
        private readonly Func<DateTimeOffset> _clock;
        private readonly Func<string> _guid;
        private readonly ISyncLocalPort _local;
        private readonly Func<long, Task> _sleep;

        private readonly object _stateLock = new object();
        private SyncState _state;

        // 单飞 SyncNow：运行中的同步任务（并发调用共用同一个 Task，见桌面端 running）。
        private readonly object _syncLock = new object();
        private Task _runningSync;
        private object _runningToken;

        // 本地修改代际：Upload 收尾时比对，期间有改动则保持 dirty 并 250 ms 后再同步。
        // 自增与快照都在 _stateLock 下（S13 的 MarkDirty 复用同一计数）。
        private int _changeGeneration;

        // S13：重试退避与防抖定时（复用 SessionManager 的 ITimerFactory，见 §7.4/§7.3）。
        // 未注入时用 Task.Delay 内建实现（单测默认）；生产由 S14 注入 DispatcherTimerFactory。
        private readonly ITimerFactory _timers;
        private readonly ILastDeviceStore _lastDevice;
        private readonly object _timerLock = new object();
        private IDisposable _debounceTimer;
        private IDisposable _retryTimer;

        // §7.4 退避表（秒）：delay = RetryAfterMs ?? steps[min(retryAttempt, 6)] × 1000。
        private static readonly int[] RetryDelayStepsSeconds = { 1, 2, 5, 10, 30, 60, 300 };

        // §7.3 MarkDirty 防抖：3000 ms 后 SyncNow。
        public const int MarkDirtyDebounceMs = 3000;

        // §7.4 退避计数：可重试错误且 autoSync 时按表累进；Upload 成功清零。
        // 读写都在 _stateLock 下（定时回调与同步路径并发）。
        private int _retryAttempt;

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
            Func<string> newGuid = null,
            ISyncLocalPort local = null,
            Func<long, Task> sleep = null,
            ITimerFactory timers = null,
            ILastDeviceStore lastDevice = null)
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
            _local = local;
            _sleep = sleep ?? (ms => Task.Delay((int)ms));
            _timers = timers ?? new TaskDelayTimerFactory();
            _lastDevice = lastDevice;
            lock (_stateLock)
            {
                // O15：构建状态只需元数据，不克隆文档。
                _state = BuildState(_auth.Session, _vault.CloneWithoutDocuments());
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

        // S14：ISyncTriggerTarget 的状态入口（与 State 同义；触发器只读快照做门控）。
        public SyncState CurrentState
        {
            get { return State; }
        }

        // §7.1 Initialize：读会话 → 绑定缓存 → 重建状态 → 无 vaultId 则安全探测。
        // 轮询在 S14 落地；登录/初始化成功后的首次同步由调用方显式 SyncNowAsync 触发。
        public async Task InitializeAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            var session = await LoadAuthWithRetryAsync().ConfigureAwait(false);
            await _vault.LoadAsync().ConfigureAwait(false);
            await _vault.BindToUserAsync(session.Authenticated ? session.UserId : null).ConfigureAwait(false);
            // O15：重建状态只需元数据，不克隆文档。
            ApplySessionState(_auth.Session, _vault.CloneWithoutDocuments());
            // fix/auth-audit：已登录（含升级前登录的会话）就记下本机设备，下次重新登录时可撤销它。
            RememberDevice(_auth.Session);
            if (_auth.Session.Authenticated && _vault.ReadVaultId() == null)
            {
                await ProbeVaultSafelyAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // fix/persist-login：启动读 auth.bin 遇到暂时性错误（DPAPI/IO）时退避重试。此前一次失败就让
        // 整个 InitializeAsync 抛出，本次运行一直显示「未登录」——用户只好再登录一次，
        // 服务端又多建一台设备（配额 10）。重试仍失败照旧上抛，由 RecoverSessionIfLoadFailedAsync 兜底。
        internal static readonly int[] AuthLoadRetryDelaysMs = { 300, 1000 };

        private async Task<AuthState> LoadAuthWithRetryAsync()
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return await _auth.LoadAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt < AuthLoadRetryDelaysMs.Length)
                {
                    Warn("读取登录状态失败，稍后重试 " + ex.GetType().Name);
                    await _sleep(AuthLoadRetryDelaysMs[attempt]).ConfigureAwait(false);
                }
            }
        }

        // fix/persist-login：启动时登录状态没读出来（auth.bin 读文件抛错，文件原样保留）。
        public bool SessionLoadFailed
        {
            get { return _auth.LastLoadFailed; }
        }

        // fix/persist-login：上次读 auth.bin 失败时重新初始化一次（回到前台、登录 / 注册前调用）。
        // 不抛；返回是否因此恢复出已登录会话。登录过后 LastLoadFailed 即复位，不会用旧文件覆盖新会话。
        public async Task<bool> RecoverSessionIfLoadFailedAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!_auth.LastLoadFailed)
            {
                return false;
            }
            try
            {
                await InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Warn("重新读取登录状态失败 " + ex.GetType().Name);
                return false;
            }
            bool restored = _auth.Session.Authenticated;
            if (restored)
            {
                Info("已恢复登录状态");
            }
            return restored;
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
            // fix/persist-login：启动时没读出登录状态的话先再读一次——已有会话就不再新建设备。
            await RecoverSessionIfLoadFailedAsync(cancellationToken).ConfigureAwait(false);
            EnsureNotSignedIn();
            var descriptor = _devices.GetDescriptor(deviceName);
            await _api.RegisterAsync(email, password, inviteCode, descriptor, cancellationToken)
                .ConfigureAwait(false);
            await RetirePreviousDeviceAsync(cancellationToken).ConfigureAwait(false);
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
            await RecoverSessionIfLoadFailedAsync(cancellationToken).ConfigureAwait(false);
            EnsureNotSignedIn();
            var descriptor = _devices.GetDescriptor(deviceName);
            await _api.LoginAsync(email, password, descriptor, cancellationToken).ConfigureAwait(false);
            await RetirePreviousDeviceAsync(cancellationToken).ConfigureAwait(false);
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
            // O15：只读 VaultId 标量，不克隆文档。
            string currentVaultId = _vault.ReadVaultId();
            if (!string.Equals(currentVaultId, response.Id, StringComparison.Ordinal))
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
            // O15：构建状态只需元数据，不克隆文档。
            var after = _vault.CloneWithoutDocuments();
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
            // O15：只读 PendingVaultSetup，不克隆文档。
            PendingVaultSetup pending = _vault.ReadPendingVaultSetup();
            if (pending == null)
            {
                var setup = await _crypto.CreateAsync(syncPassword, 1).ConfigureAwait(false);
                if (setup == null || setup.Envelope == null
                    || string.IsNullOrEmpty(setup.VaultKeyBase64)
                    || string.IsNullOrEmpty(setup.RecoveryKey))
                {
                    throw new SyncOperationException(SyncErrorCode.VaultCreateFailed, "保险库创建失败，请重试");
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
                pending = _vault.ReadPendingVaultSetup();
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
            // O15：UpdateAsync 落盘后只需 Preferences，ReadPreferences() 不克隆文档。
            var preferences = _vault.ReadPreferences();
            PatchState(next =>
            {
                next.Phase = SyncPhase.Idle;
                next.Vault = VaultStatus.Ready;
                next.KeyVersion = created.KeyVersion;
                next.Revision = "0";
                next.Dirty = true;
                next.Preferences = preferences;
                next.Conflict = null;
                SetMessage(next, SyncMessageCode.None);
            });
            Info("保险库已创建");
            return recoveryKey;
        }

        // §7.2 UnlockVault：拉信封 → 解包 → 记 key 并启用 → idle/ready（首次同步由调用方 SyncNowAsync 触发）。
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
                throw method == VaultUnlockMethod.Password
                    ? new SyncOperationException(SyncErrorCode.SyncPasswordWrong, "同步密码不正确")
                    : new SyncOperationException(SyncErrorCode.RecoveryKeyInvalid, "恢复密钥无效");
            }
            await _vault.UpdateAsync(next =>
            {
                next.VaultId = response.Id;
                next.VaultKeyBase64 = key;
                next.KeyVersion = response.Envelope.KeyVersion;
                next.Preferences.Enabled = true;
            }).ConfigureAwait(false);
            // O15：UpdateAsync 落盘后只需 Preferences，ReadPreferences() 不克隆文档。
            var preferences = _vault.ReadPreferences();
            PatchState(next =>
            {
                next.Phase = SyncPhase.Idle;
                next.Vault = VaultStatus.Ready;
                next.KeyVersion = response.Envelope.KeyVersion;
                next.Preferences = preferences;
                SetMessage(next, SyncMessageCode.None);
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
                SetMessage(next, SyncMessageCode.None);
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
            // O15：ClearAsync/BindToUserAsync 后只需 Preferences，ReadPreferences() 不克隆文档。
            var preferences = _vault.ReadPreferences();
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
                SetMessage(next, SyncMessageCode.VaultDeletedLocalKept);
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
            // O15：只读 Preferences 标量，不克隆文档。
            var previous = _vault.ReadPreferences();
            if ((previous.SyncPasswords && !preferences.SyncPasswords)
                || (previous.SyncPrivateKeys && !preferences.SyncPrivateKeys))
            {
                throw new SyncOperationException(SyncErrorCode.RotateRequired, "关闭敏感同步需要执行密钥轮换，请在“安全清理”流程中输入账号密码");
            }
            var snapshot = preferences.Clone();
            await _vault.UpdateAsync(next =>
            {
                next.Preferences = snapshot;
                next.Dirty = true;
            }).ConfigureAwait(false);
            // O15：UpdateAsync 后只需 Preferences 和 VaultKeyBase64，均用标量读法。
            var statePreferences = _vault.ReadPreferences();
            string vaultKeyAfter = _vault.ReadVaultKeyBase64();
            PatchState(next =>
            {
                next.Preferences = statePreferences;
                next.Dirty = true;
                next.Phase = statePreferences.Enabled
                    ? (vaultKeyAfter != null ? SyncPhase.Idle : SyncPhase.Locked)
                    : SyncPhase.Disabled;
            });
        }

        // §7.1 Logout：服务端失败也清本地（finally），失败原样上抛。
        public async Task LogoutAsync(
            bool all = false,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var session = _auth.Session;
            bool clearLocal = true;
            try
            {
                if (session.Authenticated)
                {
                    if (all)
                    {
                        await _api.LogoutAllAsync(cancellationToken).ConfigureAwait(false);
                        // 所有设备记录都已撤销，不必再记着本机旧记录。
                        ForgetDevice();
                    }
                    else
                    {
                        await _api.LogoutAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                Info("已退出登录");
            }
            catch (Exception ex) when (all && ShouldKeepSessionAfterLogoutAllFailure(ex))
            {
                // fix/auth-audit：「退出所有设备」没送达服务端（离线/超时/5xx）时保留本机登录并上抛。
                // 此前照样清掉本机——界面回到登录页，用户以为其他设备都已下线，实际它们仍可同步；
                // 本机也失去了重试的会话。单设备退出仍按原语义：失败也清本地。
                clearLocal = false;
                Warn("退出所有设备失败，保留本机登录以便重试 " + ex.GetType().Name);
                throw;
            }
            finally
            {
                if (clearLocal)
                {
                    await ClearLocalAfterLogoutAsync().ConfigureAwait(false);
                }
            }
        }

        // 已无会话（终端鉴权错误时 ApiClient 已清）就没什么可保留的；其余失败保留会话。
        private bool ShouldKeepSessionAfterLogoutAllFailure(Exception error)
        {
            if (!_auth.Session.Authenticated)
            {
                return false;
            }
            var apiError = error as ApiError;
            return apiError == null || !IsTerminalAuthError(apiError);
        }

        // 服务端失败也清本地（由 LogoutAsync 的 finally 调用：失败时状态同样回到 signed_out）。
        private async Task ClearLocalAfterLogoutAsync()
        {
            await _auth.ClearAsync().ConfigureAwait(false);
            await _vault.LockAsync().ConfigureAwait(false);
            // O15：只读 VaultId 标量，不克隆文档。
            string logoutVaultId = _vault.ReadVaultId();
            PatchState(next =>
            {
                next.Phase = SyncPhase.SignedOut;
                next.Vault = logoutVaultId != null ? VaultStatus.Locked : VaultStatus.Missing;
                SetMessage(next, SyncMessageCode.None);
            });
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
            // O15：只读 VaultId 标量，不克隆文档。
            string changePassVaultId = _vault.ReadVaultId();
            PatchState(next =>
            {
                next.Phase = SyncPhase.SignedOut;
                next.Vault = changePassVaultId != null ? VaultStatus.Locked : VaultStatus.Missing;
                SetMessage(next, SyncMessageCode.LoginPasswordChanged);
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
            ForgetDevice();
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
                SetMessage(next, SyncMessageCode.None);
            });
            Info("账号已注销");
        }

        // ---------- 内部流程 ----------

        // fix/auth-audit：登录 / 注册成功后撤销本机上一条设备记录（同一账号、不同设备 id 时）。
        // 先记下新设备再撤销旧的：撤销失败（离线、已被撤销 DEVICE_NOT_FOUND 等）只记警告，
        // 绝不影响本次登录；该条旧记录不再重试（最多漏一条，不会再无限堆积）。
        internal async Task RetirePreviousDeviceAsync(CancellationToken cancellationToken)
        {
            if (_lastDevice == null)
            {
                return;
            }
            var session = _auth.Session;
            if (!session.Authenticated)
            {
                return;
            }
            string previousUser;
            string previousDevice;
            bool hadPrevious = TryDecodeDevice(ReadDeviceRecord(), out previousUser, out previousDevice);
            RememberDevice(session);
            if (!hadPrevious
                || !string.Equals(previousUser, session.UserId, StringComparison.Ordinal)
                || string.Equals(previousDevice, session.DeviceId, StringComparison.Ordinal))
            {
                return;
            }
            try
            {
                await _api.DeleteDeviceAsync(previousDevice, cancellationToken).ConfigureAwait(false);
                Info("已撤销本机上一次登录留下的设备记录");
            }
            catch (Exception ex)
            {
                var apiError = ex as ApiError;
                Warn("撤销本机旧设备记录失败 " + (apiError != null && apiError.Code != null
                    ? apiError.Code
                    : ex.GetType().Name));
            }
        }

        private void RememberDevice(AuthState session)
        {
            if (_lastDevice == null || session == null || !session.Authenticated
                || string.IsNullOrEmpty(session.UserId) || string.IsNullOrEmpty(session.DeviceId))
            {
                return;
            }
            WriteDeviceRecord(EncodeDevice(session.UserId, session.DeviceId));
        }

        private void ForgetDevice()
        {
            if (_lastDevice != null)
            {
                WriteDeviceRecord(null);
            }
        }

        private string ReadDeviceRecord()
        {
            try
            {
                return _lastDevice.Read();
            }
            catch (Exception ex)
            {
                Warn("读取本机设备记录失败 " + ex.GetType().Name);
                return null;
            }
        }

        private void WriteDeviceRecord(string value)
        {
            try
            {
                _lastDevice.Write(value);
            }
            catch (Exception ex)
            {
                Warn("写入本机设备记录失败 " + ex.GetType().Name);
            }
        }

        private const char DeviceRecordSeparator = '\n';

        internal static string EncodeDevice(string userId, string deviceId)
        {
            return userId + DeviceRecordSeparator + deviceId;
        }

        internal static bool TryDecodeDevice(string value, out string userId, out string deviceId)
        {
            userId = null;
            deviceId = null;
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }
            int split = value.IndexOf(DeviceRecordSeparator);
            if (split <= 0 || split >= value.Length - 1)
            {
                return false;
            }
            userId = value.Substring(0, split);
            deviceId = value.Substring(split + 1);
            return true;
        }

        private async Task AfterAuthenticatedAsync(CancellationToken cancellationToken)
        {
            var session = _auth.Session;
            await _vault.BindToUserAsync(session.Authenticated ? session.UserId : null)
                .ConfigureAwait(false);
            // O15：重建状态只需元数据，不克隆文档。
            ApplySessionState(_auth.Session, _vault.CloneWithoutDocuments());
            if (_auth.Session.Authenticated && _vault.ReadVaultId() == null)
            {
                await ProbeVaultSafelyAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // 探测是咨询性工作：瞬断绝不拒绝认证、不抛回调用方（见桌面端 probeVaultSafely，
        // 其错误同样走 HandleSyncErrorAsync 映射）。
        private async Task ProbeVaultSafelyAsync(CancellationToken cancellationToken)
        {
            try
            {
                await ProbeVaultAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                await HandleSyncErrorAsync(error).ConfigureAwait(false);
            }
        }

        // §7.4 HandleSyncError（S13 完整版，与桌面端 handleSyncError 同构）：
        // 终端鉴权 → 清会话 + 锁库 + auth_error；已无会话 → signed_out；
        // 其他 → offline（网络/超时）或 error；可重试（offline/429/≥500）且 autoSync
        // 时按退避表 [1,2,5,10,30,60,300]s 安排 SyncNow（Retry-After 优先），
        // 经 ITimerFactory 调度并记 nextRetryAt。
        // 释放语义：定时回调只做 fire-and-forget（内部吞错记日志），绝不抛回调用方。
        private async Task HandleSyncErrorAsync(Exception error)
        {
            var apiError = error as ApiError;
            if (apiError != null && IsTerminalAuthError(apiError))
            {
                await _auth.ClearAsync().ConfigureAwait(false);
                await _vault.LockAsync().ConfigureAwait(false);
                // O15：只读 VaultId 标量，不克隆文档。
                string termVaultId = _vault.ReadVaultId();
                PatchState(next =>
                {
                    next.Phase = SyncPhase.AuthError;
                    next.Vault = termVaultId != null ? VaultStatus.Locked : VaultStatus.Missing;
                    SetErrorMessage(next, apiError);
                    next.NextRetryAt = null;
                });
                return;
            }
            if (!_auth.Session.Authenticated)
            {
                await _vault.LockAsync().ConfigureAwait(false);
                // O15：只读 VaultId 标量，不克隆文档。
                string signedOutVaultId = _vault.ReadVaultId();
                PatchState(next =>
                {
                    next.Phase = SyncPhase.SignedOut;
                    next.Vault = signedOutVaultId != null ? VaultStatus.Locked : VaultStatus.Missing;
                    SetErrorMessage(next, error);
                    next.NextRetryAt = null;
                });
                return;
            }
            bool offline = apiError != null
                && (apiError.Kind == ApiErrorKind.Network || apiError.Kind == ApiErrorKind.Timeout);
            bool retryable = offline
                || (apiError != null && apiError.Status == 429)
                || (apiError != null && apiError.Status != null && apiError.Status.Value >= 500);
            string nextRetryAt = null;
            if (retryable && IsAutoSyncEnabled())
            {
                long delayMs;
                lock (_stateLock)
                {
                    long stepMs = (long)BackoffTable.DelayFor(RetryDelayStepsSeconds, _retryAttempt) * 1000L;
                    _retryAttempt++;
                    delayMs = apiError != null && apiError.RetryAfterMs != null
                        ? apiError.RetryAfterMs.Value
                        : stepMs;
                }
                nextRetryAt = _clock().ToUniversalTime().AddMilliseconds((double)delayMs)
                    .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                ScheduleRetry(delayMs);
                Info("同步失败，已安排自动重试 delayMs=" + delayMs.ToString(CultureInfo.InvariantCulture));
            }
            PatchState(next =>
            {
                next.Phase = offline ? SyncPhase.Offline : SyncPhase.Error;
                SetErrorMessage(next, error);
                next.NextRetryAt = nextRetryAt;
            });
        }

        // O15：直接用 ReadAutoSync() 读标量，不克隆文档。
        private bool IsAutoSyncEnabled()
        {
            return _vault.ReadAutoSync();
        }

        // §7.4：取消上一次重试定时，安排 delayMs 后 SyncNow（桌面端 retryTimer 同构）。
        private void ScheduleRetry(long delayMs)
        {
            int dueMs = delayMs < 0 ? 0 : (delayMs > int.MaxValue ? int.MaxValue : (int)delayMs);
            lock (_timerLock)
            {
                if (_retryTimer != null)
                {
                    _retryTimer.Dispose();
                    _retryTimer = null;
                }
                _retryTimer = _timers.Schedule(dueMs, () =>
                {
                    var ignore = RetryTimerFiredAsync();
                });
            }
        }

        private async Task RetryTimerFiredAsync()
        {
            IDisposable fired;
            lock (_timerLock)
            {
                fired = _retryTimer;
                _retryTimer = null;
            }
            if (fired != null)
            {
                fired.Dispose();
            }
            try
            {
                await SyncNowAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Info("自动重试同步失败：" + FormatErrorMessage(error));
            }
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
                SetMessage(next, SyncMessageCode.None);
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

        // fix/persist-login：401 只有带服务端业务码（data.code）时才算终端鉴权错误。我们的服务器
        // 对每个 401 都给 data.code；没有 code 的 401（HEAD 无响应体在刷新后仍 401、或中间代理 /
        // 认证网关返回的 401 页面）并不能证明会话失效，按普通错误处理，下次同步再试，不清会话。
        internal static bool IsTerminalAuthError(ApiError error)
        {
            if (error == null || error.Kind != ApiErrorKind.Authentication)
            {
                return false;
            }
            return error.Code == "AUTH_DEVICE_REVOKED"
                || error.Code == "AUTH_TOKEN_REUSED"
                || error.Code == "AUTH_REFRESH_UNAVAILABLE"
                || (error.Status == 401 && !error.CodeUnknown);
        }

        private static string FormatErrorMessage(Exception error)
        {
            var apiError = error as ApiError;
            if (apiError != null && !string.IsNullOrEmpty(apiError.RequestId))
            {
                return apiError.Message + " (request id: " + apiError.RequestId + ")";
            }
            if (error != null && error.Message != null)
            {
                return error.Message;
            }
            return "sync request failed";
        }

        private void EnsureAuthenticated()
        {
            if (!_auth.Session.Authenticated)
            {
                throw new SyncOperationException(SyncErrorCode.SignInRequired, "请先登录");
            }
        }

        private void EnsureNotSignedIn()
        {
            if (_auth.Session.Authenticated)
            {
                throw new SyncOperationException(SyncErrorCode.AlreadySignedIn, "已登录，请先退出登录后再继续");
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

        private void Warn(string message)
        {
            if (_logger != null)
            {
                _logger.Log(LogLevel.Warning, "Sync", message);
            }
        }

        // ---------- S12a：同步主流程与上传（§7.3 PerformSync ①–④非冲突路径、Upload、CommitRemote） ----------

        // 本地发生用户修改时调用（S13 的 MarkDirty 防抖与 S14 的仓库接线复用）：
        // generation +1 并持久化 dirty；Upload 收尾据此判断上传期间是否有改动。
        public async Task NotifyLocalChangedAsync()
        {
            lock (_stateLock)
            {
                _changeGeneration++;
            }
            await _vault.UpdateAsync(next =>
            {
                next.Dirty = true;
            }).ConfigureAwait(false);
            PatchState(next =>
            {
                next.Dirty = true;
            });
        }

        // §7.3 SyncNow：单飞——已有运行中的同步时返回同一个 Task（桌面端 running）。
        // 注意：假实现/内存存储下全链路可能同步完成，RunSyncAsync 的 finally 会先于外层
        // 赋值执行；必须用 token 守卫清槽（同 S07 刷新单飞的教训），否则槽里留下已完成的
        // 旧任务，后续调用会复用它而不再同步。
        //
        // S14：无参重载是 ISyncTriggerTarget.SyncNowAsync 的实现（与双参重载 strategy=None 同义；
        // 触发器只做“来一次同一次”，冲突解决策略只由 ResolveConflict 传入）。
        public Task SyncNowAsync()
        {
            return SyncNowAsync(null, default(CancellationToken));
        }

        public Task SyncNowAsync(
            SyncNowInput input = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            SyncNowStrategy strategy = input != null ? input.Strategy : SyncNowStrategy.None;
            lock (_syncLock)
            {
                if (_runningSync != null)
                {
                    return _runningSync;
                }
                var token = new object();
                _runningToken = token;
                Task task = RunSyncAsync(strategy, cancellationToken, token);
                if (task.IsCompleted)
                {
                    // 同步完成/同步抛错：finally 已按 token 清槽，此处不再占槽，直接返回。
                    return task;
                }
                _runningSync = task;
                return task;
            }
        }

        private async Task RunSyncAsync(
            SyncNowStrategy strategy, CancellationToken cancellationToken, object token)
        {
            try
            {
                await PerformSyncAsync(strategy, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_syncLock)
                {
                    if (ReferenceEquals(_runningToken, token))
                    {
                        _runningToken = null;
                        _runningSync = null;
                    }
                }
            }
        }

        private async Task PerformSyncAsync(SyncNowStrategy strategy, CancellationToken cancellationToken)
        {
            // O15：门禁检查只需 Preferences/VaultKey/VaultId，不克隆文档。
            VaultCacheState snapshot = _vault.CloneWithoutDocuments();
            if (!_auth.Session.Authenticated)
            {
                PatchState(next =>
                {
                    next.Phase = SyncPhase.SignedOut;
                });
                return;
            }
            if (snapshot.Preferences == null || !snapshot.Preferences.Enabled)
            {
                PatchState(next =>
                {
                    next.Phase = SyncPhase.Disabled;
                });
                return;
            }
            if (string.IsNullOrEmpty(snapshot.VaultKeyBase64) || string.IsNullOrEmpty(snapshot.VaultId))
            {
                string snapshotVaultId = snapshot.VaultId;
                PatchState(next =>
                {
                    next.Phase = SyncPhase.Locked;
                    next.Vault = snapshotVaultId != null ? VaultStatus.Locked : VaultStatus.Missing;
                });
                return;
            }

            PatchState(next =>
            {
                next.Phase = SyncPhase.Syncing;
                SetMessage(next, SyncMessageCode.None);
                next.NextRetryAt = null;
            });
            Info("开始同步");
            try
            {
                SyncDocumentV1 local = await BuildLocalDocumentAsync().ConfigureAwait(false);
                // ① 重放未确认的上传；返回 null 表示重放后本地仍是最新（已 synced）。
                SyncDocumentV1 continued = await ReplayPendingUploadAsync(local, cancellationToken)
                    .ConfigureAwait(false);
                if (continued == null)
                {
                    return;
                }
                local = continued;
                VaultCacheState active = _vault.State;
                // ② 探测
                SyncDocumentHead head;
                try
                {
                    head = await _api.HeadSyncDocumentAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ApiError error) when (error.Code == "SYNC_DOCUMENT_NOT_FOUND")
                {
                    // 云端已建库但还没有文档：按 revision "0" 走首次上传（§7.3 ②）。
                    head = new SyncDocumentHead
                    {
                        Revision = "0",
                        KeyVersion = active.KeyVersion,
                        Etag = null,
                        LastModified = null
                    };
                }
                catch (ApiError error) when (error.Code == "VAULT_NOT_FOUND")
                {
                    // 保险库在别的设备上被删除：本地密钥失效，进 disabled（§7.3 ②）。
                    await _vault.LockAsync().ConfigureAwait(false);
                    PatchState(next =>
                    {
                        next.Phase = SyncPhase.Disabled;
                        next.Vault = VaultStatus.Missing;
                        SetMessage(next, SyncMessageCode.RemoteVaultDeleted);
                        next.NextRetryAt = null;
                    });
                    Info("云端保险库已被删除");
                    return;
                }
                if (head.KeyVersion != active.KeyVersion)
                {
                    await _vault.LockAsync().ConfigureAwait(false);
                    PatchState(next =>
                    {
                        next.Phase = SyncPhase.Locked;
                        next.Vault = VaultStatus.Locked;
                        next.KeyVersion = head.KeyVersion;
                        SetMessage(next, SyncMessageCode.RemoteKeyRotated);
                        next.NextRetryAt = null;
                    });
                    Info("云端密钥版本不一致，已锁定");
                    return;
                }
                if (CompareRevisions(head.Revision, active.Revision) < 0)
                {
                    throw new SyncOperationException(SyncErrorCode.RemoteRevisionBehind, "云端 revision 低于本机基线，已停止上传以避免覆盖");
                }
                // ③ 云端没有新版本
                if (string.Equals(head.Revision, active.Revision, StringComparison.Ordinal))
                {
                    if (active.Dirty
                        || string.Equals(head.Revision, "0", StringComparison.Ordinal)
                        || strategy == SyncNowStrategy.KeepLocal)
                    {
                        await UploadAsync(local, head.Revision, active.VaultId, active.KeyVersion, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        PatchState(next =>
                        {
                            next.Phase = SyncPhase.Synced;
                            SetMessage(next, SyncMessageCode.None);
                        });
                    }
                    return;
                }
                // ④ 云端有新版本
                SyncDocumentResponse remoteResponse =
                    await _api.GetSyncDocumentAsync(cancellationToken).ConfigureAwait(false);
                SyncDocumentV1 remote = await DecryptRemoteAsync(
                    remoteResponse, active.VaultKeyBase64, active.VaultId).ConfigureAwait(false);
                if (active.BaseDocument == null && strategy == SyncNowStrategy.None)
                {
                    await SaveConflictAsync(
                        local, remote, remoteResponse.Revision,
                        SingleField(SyncConflictEntity.Settings, "initial-import"),
                        SyncConflictReason.InitialImport).ConfigureAwait(false);
                    return;
                }
                if (!active.Dirty && strategy == SyncNowStrategy.None && active.BaseDocument != null)
                {
                    List<SyncConflictField> deletions =
                        RemoteDeletionConflicts(active.BaseDocument, remote);
                    if (deletions.Count != 0)
                    {
                        await SaveConflictAsync(
                            local, remote, remoteResponse.Revision,
                            deletions, SyncConflictReason.RemoteDeletion).ConfigureAwait(false);
                        return;
                    }
                }
                if (!active.Dirty || strategy == SyncNowStrategy.UseRemote)
                {
                    await _local.ApplyDocumentAsync(remote).ConfigureAwait(false);
                    await CommitRemoteAsync(remote, remoteResponse.Revision).ConfigureAwait(false);
                    Info("已应用远端文档 revision=" + remoteResponse.Revision);
                    return;
                }
                if (active.BaseDocument == null)
                {
                    await SaveConflictAsync(
                        local, remote, remoteResponse.Revision,
                        SingleField(SyncConflictEntity.Settings, "initial"),
                        SyncConflictReason.InitialImport).ConfigureAwait(false);
                    return;
                }
                SyncMergeResult merged = SyncMerge.Merge(active.BaseDocument, local, remote);
                if (merged.Conflicts.Count != 0)
                {
                    await SaveConflictAsync(
                        local, remote, remoteResponse.Revision,
                        ToConflictFields(merged.Conflicts),
                        SyncConflictReason.MergeConflict).ConfigureAwait(false);
                    return;
                }
                await _local.ApplyDocumentAsync(merged.Document).ConfigureAwait(false);
                await _vault.UpdateAsync(next =>
                {
                    next.Revision = remoteResponse.Revision;
                    next.Dirty = true;
                }).ConfigureAwait(false);
                await UploadAsync(
                    merged.Document, remoteResponse.Revision, active.VaultId, active.KeyVersion, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error)
            {
                await HandleSyncErrorAsync(error).ConfigureAwait(false);
                throw;
            }
        }

        // ① 重放未确认的上传：body 原文 + 同一幂等键 + If-Match=pending.baseRevision。
        // 无 pending 时原样返回本地文档；重放后本地仍是最新时提交并返回 null（已 synced）；
        // 否则返回重建后的本地文档继续 ②③④。重放失败不清除 pending（下次再续；只有
        // Upload 内的三种确定性错误才清除，见 UploadAsync）。
        private async Task<SyncDocumentV1> ReplayPendingUploadAsync(
            SyncDocumentV1 local, CancellationToken cancellationToken)
        {
            PendingUpload pending = _vault.State.PendingUpload;
            if (pending == null)
            {
                return local;
            }
            EncryptedDocumentData data =
                EncryptedDocumentData.Parse(JsonText.ParseObject(pending.Body));
            SyncWriteResponse resumed = await _api.PutSyncDocumentAsync(
                data, pending.BaseRevision, pending.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            bool stillCurrent = SyncDocumentWriter.SameContent(local, pending.Document);
            await _vault.UpdateAsync(next =>
            {
                next.Revision = resumed.Revision;
                next.BaseDocument = pending.Document;
                next.Dirty = !stillCurrent;
                next.PendingUpload = null;
                next.LastSyncedAt = resumed.UpdatedAt;
            }).ConfigureAwait(false);
            PatchState(next =>
            {
                next.Revision = resumed.Revision;
                next.Dirty = !stillCurrent;
                next.LastSyncedAt = resumed.UpdatedAt;
                SetMessage(next, SyncMessageCode.None);
            });
            if (stillCurrent)
            {
                PatchState(next =>
                {
                    next.Phase = SyncPhase.Synced;
                });
                Info("续传完成 revision=" + resumed.Revision);
                return null;
            }
            return await BuildLocalDocumentAsync().ConfigureAwait(false);
        }

        // §7.3 Upload：快照 generation → 加密 → 先落盘 pendingUpload 再 PUT；
        // SYNC_REVISION_CONFLICT / VAULT_KEY_VERSION_MISMATCH / IDEMPOTENCY_KEY_REUSED
        // 时清除 pending 后上抛（409 不在 Upload 内重试，下次同步走 ④ 拉远端合并）；
        // 成功后按 generation 是否变化决定 dirty，变化且 autoSync 时 250 ms 后再同步。
        private async Task UploadAsync(
            SyncDocumentV1 doc,
            string baseRevision,
            string vaultId,
            int keyVersion,
            CancellationToken cancellationToken)
        {
            int generation;
            lock (_stateLock)
            {
                generation = _changeGeneration;
            }
            // O15：只读 VaultKeyBase64 标量，不克隆文档。
            string vaultKey = _vault.ReadVaultKeyBase64();
            if (string.IsNullOrEmpty(vaultKey))
            {
                throw new SyncOperationException(SyncErrorCode.VaultLocked, "保险库未解锁");
            }
            byte[] plaintext = SyncDocumentWriter.WriteUtf8(doc);
            EncryptedDocumentEnvelope encrypted = await _crypto.EncryptDocumentAsync(
                vaultKey, vaultId, SyncConstants.SchemaVersion, keyVersion, plaintext).ConfigureAwait(false);
            if (encrypted == null)
            {
                throw new SyncOperationException(SyncErrorCode.EncryptFailed, "同步文档加密失败，请重试");
            }
            string body = encrypted.ToJson().ToString(Formatting.None);
            string idempotencyKey = _guid();
            var pending = new PendingUpload
            {
                IdempotencyKey = idempotencyKey,
                BaseRevision = baseRevision,
                Body = body,
                // O07：不再 doc.Clone()。doc 是 BuildLocalDocumentAsync 现建的独占快照，
                // 本方法之后不再改它；几行后 next.BaseDocument = doc 本来也没拷贝。
                // 两者在 store 的活状态里共享同一实例无碍——VaultCacheState.Clone()
                // 在每次读 State 时各自深拷贝一份，共享不会漏到外面。
                Document = doc,
                CreatedAt = NowIso()
            };
            // 踩坑 #12：先持久化再发请求（响应丢失后用完全相同的 body 与幂等键续传）。
            await _vault.UpdateAsync(next =>
            {
                next.PendingUpload = pending;
            }).ConfigureAwait(false);
            SyncWriteResponse response;
            try
            {
                response = await _api.PutSyncDocumentAsync(
                    ToDocumentData(encrypted), baseRevision, idempotencyKey, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ApiError error) when (
                error.Code == "SYNC_REVISION_CONFLICT"
                || error.Code == "VAULT_KEY_VERSION_MISMATCH"
                || error.Code == "IDEMPOTENCY_KEY_REUSED")
            {
                await _vault.UpdateAsync(next =>
                {
                    next.PendingUpload = null;
                }).ConfigureAwait(false);
                throw;
            }
            bool changed;
            lock (_stateLock)
            {
                changed = generation != _changeGeneration;
                // §7.3：上传成功 → 退避计数清零（与桌面端 upload 尾部 retryAttempt=0 对齐）。
                _retryAttempt = 0;
            }
            string syncedAt = NowIso();
            await _vault.UpdateAsync(next =>
            {
                next.Revision = response.Revision;
                next.BaseDocument = doc;
                next.Dirty = changed;
                next.PendingUpload = null;
                next.LastSyncedAt = syncedAt;
                next.Conflict = null;
                next.ConflictRemoteDocument = null;
                next.ConflictRemoteRevision = null;
            }).ConfigureAwait(false);
            PatchState(next =>
            {
                next.Phase = changed ? SyncPhase.Idle : SyncPhase.Synced;
                next.Revision = response.Revision;
                next.Dirty = changed;
                next.LastSyncedAt = syncedAt;
                next.Conflict = null;
                SetMessage(next, SyncMessageCode.None);
            });
            Info("上传完成 revision=" + response.Revision);
            // O15：只读 AutoSync 标量，不克隆文档。
            if (changed && _vault.ReadAutoSync())
            {
                _ = FollowUpSyncAfterDelayAsync();
            }
        }

        // §7.3 CommitRemote：远端为准落本地基线（S12a 只用于干净应用路径）。
        private async Task CommitRemoteAsync(SyncDocumentV1 document, string revision)
        {
            string syncedAt = NowIso();
            await _vault.UpdateAsync(next =>
            {
                next.BaseDocument = document;
                next.Revision = revision;
                next.Dirty = false;
                next.LastSyncedAt = syncedAt;
                next.Conflict = null;
                next.ConflictRemoteDocument = null;
                next.ConflictRemoteRevision = null;
            }).ConfigureAwait(false);
            PatchState(next =>
            {
                next.Phase = SyncPhase.Synced;
                next.Revision = revision;
                next.Dirty = false;
                next.LastSyncedAt = syncedAt;
                next.Conflict = null;
                SetMessage(next, SyncMessageCode.None);
            });
        }

        // §7.3 ④ 解密远端：解密失败抛错进 error（绝不上传覆盖，见 §4.3）；
        // schemaVersion != 1 按 §4.3 报「云端文档版本更高，请升级应用」（只读不写）。
        private async Task<SyncDocumentV1> DecryptRemoteAsync(
            SyncDocumentResponse response, string vaultKey, string vaultId)
        {
            var envelope = new EncryptedDocumentEnvelope
            {
                SchemaVersion = response.SchemaVersion,
                KeyVersion = response.KeyVersion,
                Algorithm = response.Algorithm,
                Nonce = response.Nonce,
                Ciphertext = response.Ciphertext,
                CiphertextHash = response.CiphertextHash
            };
            byte[] plaintext = await _crypto.DecryptDocumentAsync(vaultKey, vaultId, envelope)
                .ConfigureAwait(false);
            if (plaintext == null)
            {
                throw new SyncOperationException(SyncErrorCode.DecryptFailed, "云端文档解密失败");
            }
            return DecodeRemoteDocument(plaintext);
        }

        private static SyncDocumentV1 DecodeRemoteDocument(byte[] plaintext)
        {
            string json = Encoding.UTF8.GetString(plaintext, 0, plaintext.Length);
            JObject root;
            try
            {
                root = JsonText.ParseObject(json);
            }
            catch (Exception ex)
            {
                throw new SyncDocumentInvalidException("$", "云端文档不是有效 JSON：" + ex.Message);
            }
            JToken version = root["schemaVersion"];
            if (version != null && version.Type == JTokenType.Integer
                && (long)version != SyncConstants.SchemaVersion)
            {
                throw new SyncOperationException(SyncErrorCode.DocumentTooNew, "云端文档版本更高，请升级应用");
            }
            return SyncDocumentReader.Read(json);
        }

        private async Task<SyncDocumentV1> BuildLocalDocumentAsync()
        {
            if (_local == null)
            {
                throw new InvalidOperationException("本地同步适配器未配置");
            }
            // O15：只读 Preferences，不克隆文档。
            SyncPreferences prefs = _vault.ReadPreferences();
            var documentPrefs = new SyncPreferencesV1
            {
                SyncPasswords = prefs != null && prefs.SyncPasswords,
                SyncPrivateKeys = prefs != null && prefs.SyncPrivateKeys
            };
            return await _local.BuildLocalDocumentAsync(documentPrefs).ConfigureAwait(false);
        }

        // revision 是 u64 十进制字符串（踩坑 #8）：按长度再按字典序比较，不转数字。
        private static int CompareRevisions(string left, string right)
        {
            string a = string.IsNullOrEmpty(left) ? "0" : left;
            string b = string.IsNullOrEmpty(right) ? "0" : right;
            if (a.Length != b.Length)
            {
                return a.Length < b.Length ? -1 : 1;
            }
            return string.CompareOrdinal(a, b);
        }

        // Upload 收尾发现上传期间本地又有改动：250 ms 后再同步一次（桌面端同值）。
        private async Task FollowUpSyncAfterDelayAsync()
        {
            try
            {
                await _sleep(250).ConfigureAwait(false);
                await SyncNowAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Info("延迟复同步失败：" + FormatErrorMessage(error));
            }
        }

        // ---------- S12b：冲突、首次导入与远端删除（§7.3 SaveConflict/RemoteDeletionConflicts/ResolveConflict） ----------

        // §7.3 ResolveConflict：use-remote 落本地基线并 synced；
        // keep-local 以远端 revision 为基准脏上传（SyncNow(keep-local) 走 ③ 强制上传）。
        // 无待处理冲突 → 抛（与桌面端 resolveConflict 同文案）。
        public async Task ResolveConflictAsync(
            SyncNowStrategy strategy,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (strategy != SyncNowStrategy.UseRemote && strategy != SyncNowStrategy.KeepLocal)
            {
                throw new ArgumentException("冲突解决策略必须为 use-remote 或 keep-local", nameof(strategy));
            }
            VaultCacheState cache = _vault.State;
            SyncDocumentV1 remote = cache.ConflictRemoteDocument;
            string revision = cache.ConflictRemoteRevision;
            if (remote == null || string.IsNullOrEmpty(revision))
            {
                throw new SyncOperationException(SyncErrorCode.NoPendingConflict, "没有待处理的同步冲突");
            }
            if (strategy == SyncNowStrategy.UseRemote)
            {
                await _local.ApplyDocumentAsync(remote.Clone()).ConfigureAwait(false);
                await CommitRemoteAsync(remote, revision).ConfigureAwait(false);
                Info("冲突已解决（use-remote）revision=" + revision);
                return;
            }
            await _vault.UpdateAsync(next =>
            {
                next.Revision = revision;
                next.Dirty = true;
                next.Conflict = null;
                next.ConflictRemoteDocument = null;
                next.ConflictRemoteRevision = null;
            }).ConfigureAwait(false);
            PatchState(next =>
            {
                next.Phase = SyncPhase.Idle;
                next.Revision = revision;
                next.Dirty = true;
                next.Conflict = null;
                SetMessage(next, SyncMessageCode.None);
            });
            Info("冲突已解决（keep-local），以远端 revision 为基准上传 revision=" + revision);
            await SyncNowAsync(
                new SyncNowInput { Strategy = SyncNowStrategy.KeepLocal }, cancellationToken)
                .ConfigureAwait(false);
        }

        // §7.3 SaveConflict：摘要 + 远端文档 + 远端 revision 一起落 VaultCache
        // （重启后恢复，见 VaultCacheState.conflict* 三字段），相位进 conflict。
        // 脱敏：摘要只有实体/id/字段名/是否敏感与远端计数，绝不含字段值。
        private async Task SaveConflictAsync(
            SyncDocumentV1 local,
            SyncDocumentV1 remote,
            string remoteRevision,
            List<SyncConflictField> fields,
            SyncConflictReason reason)
        {
            // O15：只读 Revision 标量，不克隆文档。
            string localRevision = _vault.ReadRevision();
            var summary = new SyncConflictSummary
            {
                Reason = reason,
                LocalRevision = localRevision,
                RemoteRevision = remoteRevision,
                LocalUpdatedAt = local.UpdatedAt,
                RemoteUpdatedAt = remote.UpdatedAt,
                RemoteSummary = BuildRemoteSummary(remote),
                Fields = fields
            };
            SyncConflictSummary stateSummary = summary.Clone();
            SyncDocumentV1 remoteSnapshot = remote.Clone();
            await _vault.UpdateAsync(next =>
            {
                next.Conflict = summary;
                next.ConflictRemoteDocument = remoteSnapshot;
                next.ConflictRemoteRevision = remoteRevision;
            }).ConfigureAwait(false);
            PatchState(next =>
            {
                next.Phase = SyncPhase.Conflict;
                next.Conflict = stateSummary;
                SetMessage(next, ConflictMessage(reason));
            });
            Info("检测到同步冲突 reason=" + SyncConflictSummary.ReasonToJson(reason)
                + " fields=" + fields.Count.ToString(CultureInfo.InvariantCulture));
        }

        private static SyncMessageCode ConflictMessage(SyncConflictReason reason)
        {
            switch (reason)
            {
                case SyncConflictReason.InitialImport:
                    return SyncMessageCode.ConflictInitialImport;
                case SyncConflictReason.RemoteDeletion:
                    return SyncMessageCode.ConflictRemoteDeletion;
                default:
                    return SyncMessageCode.ConflictGeneric;
            }
        }

        // fix/functional-pass（P2-2）：状态卡消息只给码，Message 为英文诊断（App 按码查 resw）。
        private static void SetMessage(SyncState next, SyncMessageCode code)
        {
            next.MessageCode = code;
            next.MessageError = null;
            next.Message = code == SyncMessageCode.None ? "" : code.ToString();
        }

        private static void SetErrorMessage(SyncState next, Exception error)
        {
            next.MessageCode = SyncMessageCode.Error;
            next.MessageError = error;
            next.Message = FormatErrorMessage(error);
        }

        // §7.3 RemoteDeletionConflicts：base 中有、remote 中没有的 server/tunnel/group。
        // server 删标记敏感（含潜在凭据指向），tunnel/group 不敏感；field 恒为 "*"。
        private static List<SyncConflictField> RemoteDeletionConflicts(
            SyncDocumentV1 baseDocument, SyncDocumentV1 remote)
        {
            var fields = new List<SyncConflictField>();
            var remoteServerIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var server in remote.Servers)
            {
                if (server != null && server.Profile != null)
                {
                    remoteServerIds.Add(server.Profile.Id);
                }
            }
            foreach (var server in baseDocument.Servers)
            {
                if (server == null || server.Profile == null)
                {
                    continue;
                }
                if (!remoteServerIds.Contains(server.Profile.Id))
                {
                    fields.Add(new SyncConflictField
                    {
                        Entity = SyncConflictEntity.Server,
                        Id = server.Profile.Id,
                        Field = "*",
                        Sensitive = true
                    });
                }
            }
            var remoteTunnelIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tunnel in remote.Tunnels)
            {
                if (tunnel != null)
                {
                    remoteTunnelIds.Add(tunnel.Id);
                }
            }
            foreach (var tunnel in baseDocument.Tunnels)
            {
                if (tunnel == null)
                {
                    continue;
                }
                if (!remoteTunnelIds.Contains(tunnel.Id))
                {
                    fields.Add(new SyncConflictField
                    {
                        Entity = SyncConflictEntity.Tunnel,
                        Id = tunnel.Id,
                        Field = "*",
                        Sensitive = false
                    });
                }
            }
            var remoteGroupIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in remote.Groups)
            {
                if (group != null)
                {
                    remoteGroupIds.Add(group.Id);
                }
            }
            foreach (var group in baseDocument.Groups)
            {
                if (group == null)
                {
                    continue;
                }
                if (!remoteGroupIds.Contains(group.Id))
                {
                    fields.Add(new SyncConflictField
                    {
                        Entity = SyncConflictEntity.Group,
                        Id = group.Id,
                        Field = "*",
                        Sensitive = false
                    });
                }
            }
            return fields;
        }

        private static SyncRemoteSummary BuildRemoteSummary(SyncDocumentV1 remote)
        {
            bool includesPasswords = false;
            bool includesPrivateKeys = false;
            foreach (var server in remote.Servers)
            {
                if (server == null || server.Secrets == null)
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(server.Secrets.Password)
                    || !string.IsNullOrEmpty(server.Secrets.Passphrase))
                {
                    includesPasswords = true;
                }
                if (!string.IsNullOrEmpty(server.Secrets.PrivateKey))
                {
                    includesPrivateKeys = true;
                }
            }
            return new SyncRemoteSummary
            {
                Servers = remote.Servers.Count,
                Tunnels = remote.Tunnels.Count,
                Groups = remote.Groups.Count,
                IncludesPasswords = includesPasswords,
                IncludesPrivateKeys = includesPrivateKeys
            };
        }

        private static List<SyncConflictField> SingleField(SyncConflictEntity entity, string id)
        {
            return new List<SyncConflictField>
            {
                new SyncConflictField
                {
                    Entity = entity,
                    Id = id,
                    Field = "*",
                    Sensitive = false
                }
            };
        }

        private static List<SyncConflictField> ToConflictFields(
            IReadOnlyList<SyncMergeConflict> conflicts)
        {
            var fields = new List<SyncConflictField>(conflicts.Count);
            foreach (var conflict in conflicts)
            {
                fields.Add(new SyncConflictField
                {
                    Entity = ToConflictEntity(conflict.Entity),
                    Id = conflict.Id,
                    Field = conflict.Field,
                    Sensitive = conflict.Sensitive
                });
            }
            return fields;
        }

        private static SyncConflictEntity ToConflictEntity(SyncMergeEntity entity)
        {
            switch (entity)
            {
                case SyncMergeEntity.Server: return SyncConflictEntity.Server;
                case SyncMergeEntity.Tunnel: return SyncConflictEntity.Tunnel;
                case SyncMergeEntity.Group: return SyncConflictEntity.Group;
                default: return SyncConflictEntity.Settings;
            }
        }

        private static EncryptedDocumentData ToDocumentData(EncryptedDocumentEnvelope envelope)
        {
            return new EncryptedDocumentData
            {
                SchemaVersion = envelope.SchemaVersion,
                KeyVersion = envelope.KeyVersion,
                Algorithm = envelope.Algorithm,
                Nonce = envelope.Nonce,
                Ciphertext = envelope.Ciphertext,
                CiphertextHash = envelope.CiphertextHash
            };
        }

        // ---------- S13：轮换、历史、MarkDirty 与释放（§7.2 Rotate、§7.3 Restore/Clear/MarkDirty） ----------

        // §7.2 RotateSensitiveSync：仅用于关闭敏感同步（开→关）；成功返回新恢复密钥。
        // 前置：已解锁；确有「开→关」，否则抛（关闭走 SetPreferences 直接拒绝，见 S11）。
        public Task<string> RotateSensitiveSyncAsync(
            SyncPreferences preferences,
            string currentPassword,
            string syncPassword,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (preferences == null)
            {
                throw new ArgumentNullException(nameof(preferences));
            }
            RequireCredential(currentPassword, nameof(currentPassword));
            RequireCredential(syncPassword, nameof(syncPassword));
            // O15：校验状态只需元数据，不克隆文档。
            VaultCacheState cache = _vault.CloneWithoutDocuments();
            if (string.IsNullOrEmpty(cache.VaultId)
                || string.IsNullOrEmpty(cache.VaultKeyBase64)
                || cache.KeyVersion < 1)
            {
                throw new SyncOperationException(SyncErrorCode.VaultLocked, "同步保险库未解锁");
            }
            SyncPreferences current = cache.Preferences ?? SyncPreferences.Defaults();
            bool disablesPassword = current.SyncPasswords && !preferences.SyncPasswords;
            bool disablesPrivateKey = current.SyncPrivateKeys && !preferences.SyncPrivateKeys;
            if (!disablesPassword && !disablesPrivateKey)
            {
                throw new SyncOperationException(SyncErrorCode.RotateOnlyDisables, "密钥轮换只用于关闭已启用的敏感同步");
            }
            return RotateVaultKeyAsync(
                preferences, currentPassword, syncPassword,
                SyncMessageCode.SensitiveCleared, cancellationToken);
        }

        // §7.2 ChangeSyncPassword：轮换整个 vaultKey，旧同步密码与旧恢复密钥一起失效。
        // 同步范围（preferences）不变，文档内容只换密钥（与桌面端 changeSyncPassword 对齐）。
        public Task<string> ChangeSyncPasswordAsync(
            string currentPassword,
            string syncPassword,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            RequireCredential(currentPassword, nameof(currentPassword));
            RequireCredential(syncPassword, nameof(syncPassword));
            // O15：校验状态只需元数据，不克隆文档。
            VaultCacheState cache = _vault.CloneWithoutDocuments();
            if (string.IsNullOrEmpty(cache.VaultId)
                || string.IsNullOrEmpty(cache.VaultKeyBase64)
                || cache.KeyVersion < 1)
            {
                throw new SyncOperationException(SyncErrorCode.VaultLocked, "同步保险库未解锁");
            }
            SyncPreferences unchanged = cache.Preferences == null
                ? SyncPreferences.Defaults()
                : cache.Preferences.Clone();
            return RotateVaultKeyAsync(
                unchanged, currentPassword, syncPassword,
                SyncMessageCode.SyncPasswordChanged, cancellationToken);
        }

        // §7.2 RotateVaultKey：生成新 vaultKey → 新同步密码重新包装 →
        // 整份文档重新加密上传（POST vault/rotate，If-Match=轮换前 revision，原子清空历史）。
        // 成功：换 key/version/revision/基线，清 pendingUpload 与冲突，进 synced，返回新恢复密钥。
        // 失败：回滚 preferences 并原样上抛；Ambiguous（响应丢失、服务端可能已执行）→
        // phase=error 并提示用新同步密码重新解锁（新恢复密钥已找不回，但新密码仍可解）。
        // 注意 currentPassword 是账号登录密码（踩坑 #14），syncPassword 是新的同步密码。
        private async Task<string> RotateVaultKeyAsync(
            SyncPreferences preferences,
            string currentPassword,
            string syncPassword,
            SyncMessageCode successMessage,
            CancellationToken cancellationToken)
        {
            // O15：读取 vaultId/revision/keyVersion 等元数据，不克隆文档。
            VaultCacheState cache = _vault.CloneWithoutDocuments();
            if (string.IsNullOrEmpty(cache.VaultId)
                || string.IsNullOrEmpty(cache.VaultKeyBase64)
                || cache.KeyVersion < 1)
            {
                throw new SyncOperationException(SyncErrorCode.VaultLocked, "同步保险库未解锁");
            }
            string vaultId = cache.VaultId;
            string baseRevision = cache.Revision;
            int nextVersion = cache.KeyVersion + 1;
            VaultSetupResult setup = await _crypto.CreateAsync(syncPassword, nextVersion)
                .ConfigureAwait(false);
            if (setup == null || setup.Envelope == null
                || string.IsNullOrEmpty(setup.VaultKeyBase64)
                || string.IsNullOrEmpty(setup.RecoveryKey))
            {
                throw new SyncOperationException(SyncErrorCode.VaultCreateFailed, "保险库创建失败，请重试");
            }
            SyncPreferences previous = cache.Preferences == null
                ? SyncPreferences.Defaults()
                : cache.Preferences.Clone();
            SyncPreferences wanted = preferences.Clone();
            try
            {
                await _vault.UpdateAsync(next =>
                {
                    next.Preferences = wanted.Clone();
                }).ConfigureAwait(false);
                // 新偏好先生效，Build 按新偏好输出（关闭的敏感字段不再进文档，见 §5.1）。
                SyncDocumentV1 document = await BuildLocalDocumentAsync().ConfigureAwait(false);
                byte[] plaintext = SyncDocumentWriter.WriteUtf8(document);
                EncryptedDocumentEnvelope encrypted = await _crypto.EncryptDocumentAsync(
                    setup.VaultKeyBase64, vaultId, SyncConstants.SchemaVersion, nextVersion, plaintext)
                    .ConfigureAwait(false);
                if (encrypted == null)
                {
                    throw new SyncOperationException(SyncErrorCode.EncryptFailed, "同步文档加密失败，请重试");
                }
                RotateVaultResponse response = await _api.RotateVaultAsync(
                    currentPassword, ToData(setup.Envelope), ToDocumentData(encrypted),
                    baseRevision, _guid(), cancellationToken).ConfigureAwait(false);
                if (response.KeyVersion != nextVersion)
                {
                    throw new SyncOperationException(SyncErrorCode.UnexpectedKeyVersion, "服务端返回了意外的密钥版本");
                }
                // Lumia 的本地端口无 lockSecrets（凭据只在 SecretStore，无内存明文缓存）；
                // 新基线直接应用（ChangeOrigin.Sync，不再标脏）。
                await _local.ApplyDocumentAsync(document).ConfigureAwait(false);
                string syncedAt = response.UpdatedAt;
                SyncDocumentV1 committed = document.Clone();
                SyncPreferences committedPrefs = wanted.Clone();
                await _vault.UpdateAsync(next =>
                {
                    next.VaultKeyBase64 = setup.VaultKeyBase64;
                    next.KeyVersion = response.KeyVersion;
                    next.Revision = response.Revision;
                    next.Preferences = committedPrefs;
                    next.BaseDocument = committed;
                    next.Dirty = false;
                    next.PendingUpload = null;
                    next.Conflict = null;
                    next.ConflictRemoteDocument = null;
                    next.ConflictRemoteRevision = null;
                    next.LastSyncedAt = syncedAt;
                }).ConfigureAwait(false);
                SyncPreferences statePrefs = wanted.Clone();
                PatchState(next =>
                {
                    next.Phase = SyncPhase.Synced;
                    next.Vault = VaultStatus.Ready;
                    next.KeyVersion = response.KeyVersion;
                    next.Revision = response.Revision;
                    next.Preferences = statePrefs;
                    next.Dirty = false;
                    next.LastSyncedAt = syncedAt;
                    next.Conflict = null;
                    SetMessage(next, successMessage);
                });
                Info("保险库密钥已轮换 keyVersion="
                    + response.KeyVersion.ToString(CultureInfo.InvariantCulture));
                return setup.RecoveryKey;
            }
            catch (Exception error)
            {
                SyncPreferences rollback = previous.Clone();
                await _vault.UpdateAsync(next =>
                {
                    next.Preferences = rollback;
                }).ConfigureAwait(false);
                var apiError = error as ApiError;
                if (apiError != null && apiError.Ambiguous)
                {
                    PatchState(next =>
                    {
                        next.Phase = SyncPhase.Error;
                        SetMessage(next, SyncMessageCode.RotateResultUnknown);
                    });
                }
                throw;
            }
        }

        // §7.3 RestoreRevision：恢复历史版本（POST，If-Match=当前 revision，新幂等键），
        // 随后 SyncNow(use-remote) 把恢复后的远端拉下来应用。
        public async Task RestoreRevisionAsync(
            string revision,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(revision))
            {
                throw new ArgumentException("revision 不能为空", nameof(revision));
            }
            // O15：只读 Revision 标量，不克隆文档。
            string currentRevision = _vault.ReadRevision();
            await _api.RestoreRevisionAsync(revision, currentRevision, _guid(), cancellationToken)
                .ConfigureAwait(false);
            Info("历史版本已恢复 revision=" + revision);
            await SyncNowAsync(
                new SyncNowInput { Strategy = SyncNowStrategy.UseRemote }, cancellationToken)
                .ConfigureAwait(false);
        }

        // §7.3 ClearRevisions：清空历史（DELETE，If-Match=当前 revision）。
        public Task<DeleteRevisionsResponse> ClearRevisionsAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            // O15：只读 Revision 标量，不克隆文档。
            return _api.DeleteRevisionsAsync(_vault.ReadRevision(), cancellationToken);
        }

        // 历史与设备列表透传（U18 直接绑定返回的 DTO；不碰本地状态）。
        public Task<RevisionListResponse> ListRevisionsAsync(
            int limit = 20,
            string beforeRevision = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return _api.ListRevisionsAsync(limit, beforeRevision, cancellationToken);
        }

        public Task<DeviceListResponse> ListDevicesAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return _api.ListDevicesAsync(cancellationToken);
        }

        // §7.3 MarkDirty：未启用直接返回；generation+1 并持久化 dirty；
        // autoSync 时防抖 MarkDirtyDebounceMs（3000 ms）后 SyncNow。
        // S14 的仓库 Changed 接线调用此方法（Sync 来源与无关实体由调用方过滤）。
        public async Task MarkDirtyAsync()
        {
            // O15：只读 Preferences，不克隆文档。
            SyncPreferences preferences = _vault.ReadPreferences();
            if (preferences == null || !preferences.Enabled)
            {
                return;
            }
            await NotifyLocalChangedAsync().ConfigureAwait(false);
            if (!preferences.AutoSync)
            {
                return;
            }
            lock (_timerLock)
            {
                if (_debounceTimer != null)
                {
                    _debounceTimer.Dispose();
                    _debounceTimer = null;
                }
                _debounceTimer = _timers.Schedule(MarkDirtyDebounceMs, () =>
                {
                    var ignore = DebouncedSyncNowAsync();
                });
            }
            Info("本地已标脏，防抖后同步");
        }

        private async Task DebouncedSyncNowAsync()
        {
            IDisposable fired;
            lock (_timerLock)
            {
                fired = _debounceTimer;
                _debounceTimer = null;
            }
            if (fired != null)
            {
                fired.Dispose();
            }
            try
            {
                await SyncNowAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                Info("防抖同步失败：" + FormatErrorMessage(error));
            }
        }

        // 释放防抖与重试定时（S14 组合根在注销/释放协调器时调用）。
        public void Dispose()
        {
            lock (_timerLock)
            {
                if (_debounceTimer != null)
                {
                    _debounceTimer.Dispose();
                    _debounceTimer = null;
                }
                if (_retryTimer != null)
                {
                    _retryTimer.Dispose();
                    _retryTimer = null;
                }
            }
        }

        // 未注入 ITimerFactory 时的内建实现（Task.Delay 一次性定时，供单测与桌面逻辑单测）。
        private sealed class TaskDelayTimerFactory : ITimerFactory
        {
            public IDisposable Schedule(int delayMs, Action callback)
            {
                var source = new CancellationTokenSource();
                Task.Delay(delayMs < 0 ? 0 : delayMs, source.Token).ContinueWith(t =>
                {
                    if (!t.IsCanceled && callback != null)
                    {
                        callback();
                    }
                }, TaskContinuationOptions.ExecuteSynchronously);
                return new CancelOnDispose(source);
            }

            public IDisposable SchedulePeriodic(int periodMs, Action callback)
            {
                throw new NotSupportedException("SyncCoordinator 仅使用一次性定时");
            }

            private sealed class CancelOnDispose : IDisposable
            {
                private CancellationTokenSource _source;

                public CancelOnDispose(CancellationTokenSource source)
                {
                    _source = source;
                }

                public void Dispose()
                {
                    CancellationTokenSource source = _source;
                    _source = null;
                    if (source != null)
                    {
                        try
                        {
                            source.Cancel();
                        }
                        catch (ObjectDisposedException)
                        {
                        }
                        source.Dispose();
                    }
                }
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
                throw new SyncOperationException(SyncErrorCode.EnvelopeInvalid, "保险库信封无效");
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
                throw new SyncOperationException(SyncErrorCode.EnvelopeInvalid, "保险库信封无效");
            }
            return envelope;
        }
    }
}
