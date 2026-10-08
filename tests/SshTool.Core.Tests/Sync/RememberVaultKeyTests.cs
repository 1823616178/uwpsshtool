using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SshTool.Core.Storage;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Vault;
using SshTool.Core.Tests.Fakes;
using Xunit;
using Harness = SshTool.Core.Tests.Sync.SyncCoordinatorVaultTests.Harness;

namespace SshTool.Core.Tests.Sync
{
    // feat/remember-vault：「登陆上去后还要输入同步密码」——本机记住保险库密钥（vault.bin，DPAPI）。
    // 全新安装：登录 → 云端已有保险库 → 输入一次 → 重启不再问；退出后同一账号重新登录不再问；
    // 别的设备改了同步密码 / 删库重建 → 只问一次并说明原因；换账号绝不沿用上一个账号的密钥。
    public class RememberVaultKeyTests
    {
        private const string SyncPassword = "sync-password";

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

        // 另一台设备已建好保险库；返回一台全新安装、共用同一服务端的设备。
        private static async Task<Harness> FreshDeviceWithExistingVaultAsync(SyncDeviceOptions options = null)
        {
            var owner = new Harness();
            await owner.Coordinator.InitializeAsync();
            await owner.Coordinator.RegisterAsync("a@b.c", "login-password");
            await owner.Coordinator.SetupVaultAsync(SyncPassword);

            var device = new Harness(null, options);
            device.Server.ServeEnvelope(owner.Server.VaultEnvelope);
            await device.Coordinator.InitializeAsync();
            return device;
        }

        private static SyncScreenKind Screen(Harness h)
        {
            return new SyncStatePresenter().DetermineScreen(h.Auth.Session, h.Coordinator.State);
        }

        private static async Task<Harness> UnlockedDeviceAsync(SyncDeviceOptions options = null)
        {
            var device = await FreshDeviceWithExistingVaultAsync(options);
            await device.Coordinator.LoginAsync("a@b.c", "login-password");
            Assert.Equal(SyncScreenKind.UnlockVault, Screen(device));
            await device.Coordinator.UnlockVaultAsync(SyncPassword, VaultUnlockMethod.Password);
            Assert.Equal(SyncScreenKind.Status, Screen(device));
            return device;
        }

        private static int Unwraps(Harness h)
        {
            return h.Crypto.Calls.Count(c => c == "UnwrapWithPassword");
        }

        // ---------- 只问一次 ----------

        [Fact]
        public async Task FreshInstall_AskedOnce_FirstTimeWithoutNotice()
        {
            var device = await FreshDeviceWithExistingVaultAsync();

            await device.Coordinator.LoginAsync("a@b.c", "login-password");

            Assert.Equal(SyncScreenKind.UnlockVault, Screen(device));
            Assert.False(SyncRouting.ShouldShowUnlockNotice(device.Coordinator.State));
        }

        [Fact]
        public async Task AfterUnlock_Restart_NoPrompt()
        {
            var device = await UnlockedDeviceAsync();

            var restarted = new Harness(device);
            await restarted.Coordinator.InitializeAsync();

            Assert.Equal(SyncScreenKind.Status, Screen(restarted));
            Assert.Equal(VaultStatus.Ready, restarted.Coordinator.State.Vault);
        }

        [Fact]
        public async Task Logout_LoginSameAccount_NoPrompt()
        {
            var device = await UnlockedDeviceAsync();
            string key = device.Vault.State.VaultKeyBase64;
            int unwraps = Unwraps(device);

            await device.Coordinator.LogoutAsync();
            await device.Coordinator.LoginAsync("a@b.c", "login-password");

            Assert.Equal(SyncScreenKind.Status, Screen(device));
            Assert.Equal(key, device.Vault.State.VaultKeyBase64);
            Assert.Equal(unwraps, Unwraps(device));
        }

        [Fact]
        public async Task Logout_Restart_LoginSameAccount_NoPrompt()
        {
            var device = await UnlockedDeviceAsync();
            await device.Coordinator.LogoutAsync();

            var restarted = new Harness(device);
            await restarted.Coordinator.InitializeAsync();
            Assert.Equal(SyncScreenKind.Login, Screen(restarted));
            await restarted.Coordinator.LoginAsync("a@b.c", "login-password");

            Assert.Equal(SyncScreenKind.Status, Screen(restarted));
        }

        [Fact]
        public async Task LoginPasswordChange_ThenLogin_NoSyncPasswordPrompt()
        {
            var device = await UnlockedDeviceAsync();

            await device.Coordinator.ChangeAccountPasswordAsync("login-password", "new-login-password");
            await device.Coordinator.LoginAsync("a@b.c", "new-login-password");

            Assert.Equal(SyncScreenKind.Status, Screen(device));
        }

        // ---------- 记住的密钥失效：只问一次并说明原因 ----------

        [Fact]
        public async Task SyncPasswordChangedElsewhere_PromptOnceWithNotice()
        {
            var device = await UnlockedDeviceAsync();
            await device.Coordinator.LogoutAsync();
            // 别的设备修改同步密码：整个 vaultKey 轮换，keyVersion 1 → 2。
            var rotated = await new FakeVaultCrypto().CreateAsync("new-sync-password", 2);
            device.Server.ServeEnvelope(rotated.Envelope.ToJson());

            await device.Coordinator.LoginAsync("a@b.c", "login-password");

            Assert.Equal(SyncScreenKind.UnlockVault, Screen(device));
            Assert.Null(device.Vault.State.VaultKeyBase64);
            Assert.Equal(2, device.Coordinator.State.KeyVersion);
            Assert.True(SyncRouting.ShouldShowUnlockNotice(device.Coordinator.State));

            await device.Coordinator.UnlockVaultAsync("new-sync-password", VaultUnlockMethod.Password);
            Assert.Equal(SyncScreenKind.Status, Screen(device));
            Assert.False(SyncRouting.ShouldShowUnlockNotice(device.Coordinator.State));

            var restarted = new Harness(device);
            await restarted.Coordinator.InitializeAsync();
            Assert.Equal(SyncScreenKind.Status, Screen(restarted));
        }

        [Fact]
        public async Task VaultRecreatedElsewhere_PromptWithNotice()
        {
            var device = await UnlockedDeviceAsync();
            await device.Coordinator.LogoutAsync();
            device.Server.ActiveVaultId = "vault-recreated";

            await device.Coordinator.LoginAsync("a@b.c", "login-password");

            Assert.Equal(SyncScreenKind.UnlockVault, Screen(device));
            Assert.Null(device.Vault.State.VaultKeyBase64);
            Assert.Equal("vault-recreated", device.Vault.State.VaultId);
            Assert.True(SyncRouting.ShouldShowUnlockNotice(device.Coordinator.State));
        }

        [Fact]
        public async Task ProbeOffline_AtLogin_KeepsRememberedKey()
        {
            var device = await UnlockedDeviceAsync();
            await device.Coordinator.LogoutAsync();
            device.Transport.Handler = r =>
            {
                if (r.Url.EndsWith("/vault/key-envelope", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("offline");
                }
                return device.Server.Handle(r);
            };

            await device.Coordinator.LoginAsync("a@b.c", "login-password");

            Assert.True(device.Auth.Session.Authenticated);
            Assert.NotNull(device.Vault.State.VaultKeyBase64);
            Assert.Equal(VaultStatus.Ready, device.Coordinator.State.Vault);
        }

        // ---------- 换账号绝不沿用 ----------

        [Fact]
        public async Task DifferentAccount_DoesNotGetPreviousKey()
        {
            var device = await UnlockedDeviceAsync();
            await device.Coordinator.LogoutAsync();
            device.Server.LoginUserId = "u2";

            await device.Coordinator.LoginAsync("other@b.c", "login-password");

            Assert.Null(device.Vault.State.VaultKeyBase64);
            Assert.Equal("u2", device.Vault.State.UserId);
            Assert.NotEqual(SyncScreenKind.Status, Screen(device));
            Assert.False(SyncRouting.ShouldShowUnlockNotice(device.Coordinator.State));

            // 再换回原账号：密钥已随换账号清掉，需要重新输入一次。
            await device.Coordinator.LogoutAsync();
            device.Server.LoginUserId = SyncCoordinatorVaultTests.MockSyncServer.UserId;
            await device.Coordinator.LoginAsync("a@b.c", "login-password");
            Assert.Equal(SyncScreenKind.UnlockVault, Screen(device));
        }

        [Fact]
        public async Task DeleteAccount_ClearsRememberedKey()
        {
            var device = await UnlockedDeviceAsync();

            await device.Coordinator.DeleteAccountAsync("login-password");

            Assert.Null(device.Vault.State.VaultKeyBase64);
            Assert.Null(device.Vault.State.UserId);
        }

        // ---------- 开关 ----------

        [Fact]
        public async Task RememberOff_LogoutForgetsKey_NextLoginPrompts()
        {
            var settings = new DictionarySettings();
            settings.Values[SyncDeviceOptions.RememberVaultKeyKey] = false;
            var device = await UnlockedDeviceAsync(new SyncDeviceOptions(settings));
            Assert.False(device.Coordinator.RememberVaultKey);

            await device.Coordinator.LogoutAsync();
            Assert.Null(device.Vault.State.VaultKeyBase64);

            await device.Coordinator.LoginAsync("a@b.c", "login-password");
            Assert.Equal(SyncScreenKind.UnlockVault, Screen(device));
            Assert.False(SyncRouting.ShouldShowUnlockNotice(device.Coordinator.State));
        }

        [Fact]
        public async Task TurnOff_WhileSignedIn_KeepsKeyUntilLogout_AndPersists()
        {
            var settings = new DictionarySettings();
            var device = await UnlockedDeviceAsync(new SyncDeviceOptions(settings));
            Assert.True(device.Coordinator.RememberVaultKey);

            await device.Coordinator.SetRememberVaultKeyAsync(false);

            Assert.NotNull(device.Vault.State.VaultKeyBase64);
            Assert.False((bool)settings.Values[SyncDeviceOptions.RememberVaultKeyKey]);
            await device.Coordinator.LogoutAsync();
            Assert.Null(device.Vault.State.VaultKeyBase64);
        }

        [Fact]
        public async Task TurnOff_WhileSignedOut_ForgetsImmediately()
        {
            var device = await UnlockedDeviceAsync(new SyncDeviceOptions(new DictionarySettings()));
            await device.Coordinator.LogoutAsync();
            Assert.NotNull(device.Vault.State.VaultKeyBase64);

            await device.Coordinator.SetRememberVaultKeyAsync(false);

            Assert.Null(device.Vault.State.VaultKeyBase64);
        }

        [Fact]
        public void DeviceOptions_DefaultOn_ToleratesBadValuesAndFailures()
        {
            var settings = new DictionarySettings();
            var options = new SyncDeviceOptions(settings);
            Assert.True(options.RememberVaultKey);

            options.RememberVaultKey = false;
            Assert.False(options.RememberVaultKey);

            settings.Values[SyncDeviceOptions.RememberVaultKeyKey] = "nope";
            Assert.True(options.RememberVaultKey);

            settings.Fail = true;
            Assert.True(options.RememberVaultKey);
            options.RememberVaultKey = false;
        }

        [Fact]
        public void DeviceOptions_KeyIsNotASyncedSetting()
        {
            Assert.DoesNotContain(SettingDefinitions.All, d => d.Key == SyncDeviceOptions.RememberVaultKeyKey);
        }

        [Fact]
        public void UnlockNotice_OnlyForRotatedKey()
        {
            Assert.False(SyncRouting.ShouldShowUnlockNotice(null));
            Assert.False(SyncRouting.ShouldShowUnlockNotice(new SyncState { Phase = SyncPhase.Locked }));
            Assert.True(SyncRouting.ShouldShowUnlockNotice(new SyncState
            {
                Phase = SyncPhase.Locked,
                MessageCode = SyncMessageCode.RemoteKeyRotated
            }));
        }
    }
}
