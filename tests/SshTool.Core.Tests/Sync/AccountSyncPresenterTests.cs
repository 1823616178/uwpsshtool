using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Auth;
using SshTool.Core.Sync.Vault;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // feat/account-sync-ui：「账号与同步」页重设计的纯逻辑。回归点：
    //   - 保险库 Locked / Missing 时本页不再转走（此前一进来就被弹去解锁页，用户找不到同步密码入口）；
    //   - 保险库卡按状态给出「创建 / 输入同步密码解锁 / 已解锁 + 锁定」；
    //   - 「立即同步」只在保险库已解锁且同步开启时可用，不可用时有说明。
    public class AccountSyncPresenterTests
    {
        private static AuthState SignedIn()
        {
            return new AuthState { Authenticated = true, UserId = "u1", DeviceId = "d1", UserEmail = "a@b.c" };
        }

        private static SyncState State(SyncPhase phase, VaultStatus vault, bool enabled = true)
        {
            var state = new SyncState { Phase = phase, Vault = vault };
            state.Preferences.Enabled = enabled;
            return state;
        }

        [Theory]
        [InlineData(VaultStatus.Missing)]
        [InlineData(VaultStatus.Locked)]
        [InlineData(VaultStatus.Ready)]
        public void NeedsRouting_OnlyWhenSignedOut(VaultStatus vault)
        {
            Assert.False(AccountSyncPresenter.NeedsRouting(SignedIn()));
            Assert.True(AccountSyncPresenter.NeedsRouting(AuthState.Unauthenticated));
            Assert.True(AccountSyncPresenter.NeedsRouting(null));
            // 路由页（登录后 GoAfterAuth）仍按保险库状态去建库 / 解锁页，首登流程不变。
            Assert.Equal(vault == VaultStatus.Ready,
                SyncRouting.Decide(true, SignedIn(), State(SyncPhase.Idle, vault)) == SyncScreenKind.Status);
        }

        [Theory]
        [InlineData(SyncPhase.Synced, SyncTone.Success, "IconCheck")]
        [InlineData(SyncPhase.Idle, SyncTone.Accent, "IconSync")]
        [InlineData(SyncPhase.Syncing, SyncTone.Accent, "IconSync")]
        [InlineData(SyncPhase.Offline, SyncTone.Warning, "IconCloudOff")]
        [InlineData(SyncPhase.Conflict, SyncTone.Warning, "IconWarning")]
        [InlineData(SyncPhase.Locked, SyncTone.Warning, "IconLock")]
        [InlineData(SyncPhase.Error, SyncTone.Danger, "IconSyncError")]
        [InlineData(SyncPhase.AuthError, SyncTone.Danger, "IconSyncError")]
        [InlineData(SyncPhase.Disabled, SyncTone.Neutral, "IconCloud")]
        [InlineData(SyncPhase.SignedOut, SyncTone.Neutral, "IconCloudOff")]
        public void Hero_ToneAndGlyphPerPhase(SyncPhase phase, SyncTone tone, string glyph)
        {
            var spec = AccountSyncPresenter.Hero(SignedIn(), State(phase, VaultStatus.Ready));
            Assert.Equal(tone, spec.Tone);
            Assert.Equal(glyph, spec.GlyphKey);
            Assert.Equal(phase == SyncPhase.Syncing, spec.Spin);
        }

        [Fact]
        public void Hero_SyncNow_OnlyWhenReadyEnabledAndIdle()
        {
            var ready = AccountSyncPresenter.Hero(SignedIn(), State(SyncPhase.Synced, VaultStatus.Ready));
            Assert.True(ready.CanSyncNow);
            Assert.Null(ready.SyncNowHintKey);

            var syncing = AccountSyncPresenter.Hero(SignedIn(), State(SyncPhase.Syncing, VaultStatus.Ready));
            Assert.False(syncing.CanSyncNow);
            Assert.Null(syncing.SyncNowHintKey);

            var locked = AccountSyncPresenter.Hero(SignedIn(), State(SyncPhase.Locked, VaultStatus.Locked));
            Assert.False(locked.CanSyncNow);
            Assert.Equal("AccountSync_HintUnlockVault", locked.SyncNowHintKey);

            var missing = AccountSyncPresenter.Hero(SignedIn(), State(SyncPhase.Disabled, VaultStatus.Missing, enabled: false));
            Assert.False(missing.CanSyncNow);
            Assert.Equal("AccountSync_HintCreateVault", missing.SyncNowHintKey);

            var disabled = AccountSyncPresenter.Hero(SignedIn(), State(SyncPhase.Disabled, VaultStatus.Ready, enabled: false));
            Assert.False(disabled.CanSyncNow);
            Assert.Equal("AccountSync_HintSyncDisabled", disabled.SyncNowHintKey);

            var signedOut = AccountSyncPresenter.Hero(AuthState.Unauthenticated, State(SyncPhase.SignedOut, VaultStatus.Missing));
            Assert.False(signedOut.CanSyncNow);
        }

        [Fact]
        public void Hero_ShowsDetail_ForErrorsAndNotices()
        {
            var error = AccountSyncPresenter.Hero(SignedIn(), State(SyncPhase.Error, VaultStatus.Ready));
            Assert.True(error.ShowDetail);
            Assert.Equal(SyncTone.Danger, error.DetailTone);

            var offline = AccountSyncPresenter.Hero(SignedIn(), State(SyncPhase.Offline, VaultStatus.Ready));
            Assert.True(offline.ShowDetail);
            Assert.Equal(SyncTone.Warning, offline.DetailTone);

            // 其他设备改了同步密码：相位是 Locked，但必须说明原因。
            var rotated = State(SyncPhase.Locked, VaultStatus.Locked);
            rotated.MessageCode = SyncMessageCode.RemoteKeyRotated;
            Assert.True(AccountSyncPresenter.Hero(SignedIn(), rotated).ShowDetail);

            var changed = State(SyncPhase.Idle, VaultStatus.Ready);
            changed.MessageCode = SyncMessageCode.SyncPasswordChanged;
            var info = AccountSyncPresenter.Hero(SignedIn(), changed);
            Assert.True(info.ShowDetail);
            Assert.Equal(SyncTone.Accent, info.DetailTone);

            Assert.False(AccountSyncPresenter.Hero(SignedIn(), State(SyncPhase.Synced, VaultStatus.Ready)).ShowDetail);
            Assert.False(AccountSyncPresenter.Hero(SignedIn(), null).ShowDetail);
        }

        [Fact]
        public void VaultCard_Locked_OffersUnlock()
        {
            var spec = AccountSyncPresenter.VaultCard(SignedIn(), State(SyncPhase.Locked, VaultStatus.Locked), true);
            Assert.Equal(VaultCardKind.Locked, spec.Kind);
            Assert.True(spec.ShowUnlock);
            Assert.False(spec.ShowLock);
            Assert.False(spec.ShowChangePassword);
            Assert.False(spec.ShowCreate);
            Assert.False(spec.ShowRemember);
            Assert.True(spec.ShowDeleteVault);
            Assert.Equal(SyncTone.Warning, spec.Tone);
        }

        [Fact]
        public void VaultCard_Missing_OffersCreate()
        {
            var spec = AccountSyncPresenter.VaultCard(SignedIn(), State(SyncPhase.Disabled, VaultStatus.Missing, false), true);
            Assert.Equal(VaultCardKind.Missing, spec.Kind);
            Assert.True(spec.ShowCreate);
            Assert.False(spec.ShowUnlock);
            Assert.False(spec.ShowDeleteVault);
        }

        [Theory]
        [InlineData(true, "AccountSync_VaultReadyDescRemember")]
        [InlineData(false, "AccountSync_VaultReadyDescForget")]
        public void VaultCard_Ready_OffersLockAndChangePassword(bool remember, string descKey)
        {
            var spec = AccountSyncPresenter.VaultCard(SignedIn(), State(SyncPhase.Synced, VaultStatus.Ready), remember);
            Assert.Equal(VaultCardKind.Ready, spec.Kind);
            Assert.True(spec.ShowLock);
            Assert.True(spec.ShowChangePassword);
            Assert.True(spec.ShowRemember);
            Assert.False(spec.ShowUnlock);
            Assert.Equal(descKey, spec.DescriptionKey);
            Assert.Equal(SyncTone.Success, spec.Tone);
        }

        [Fact]
        public void VaultCard_HiddenWhenSignedOut()
        {
            Assert.Equal(VaultCardKind.Hidden,
                AccountSyncPresenter.VaultCard(AuthState.Unauthenticated, State(SyncPhase.SignedOut, VaultStatus.Locked), true).Kind);
            Assert.Equal(VaultCardKind.Hidden, AccountSyncPresenter.VaultCard(SignedIn(), null, true).Kind);
        }

        [Theory]
        [InlineData("windows-mobile-arm", DevicePlatformKind.Phone, "Windows 10 Mobile")]
        [InlineData("windows-uwp-x64", DevicePlatformKind.Desktop, "Windows 10")]
        [InlineData("win32", DevicePlatformKind.Desktop, "Windows")]
        [InlineData("darwin", DevicePlatformKind.Desktop, "macOS")]
        [InlineData("linux", DevicePlatformKind.Desktop, "Linux")]
        [InlineData("android", DevicePlatformKind.Phone, "Android")]
        [InlineData("ios", DevicePlatformKind.Phone, "iOS")]
        [InlineData("toaster-os", DevicePlatformKind.Unknown, "toaster-os")]
        [InlineData("", DevicePlatformKind.Unknown, "")]
        [InlineData(null, DevicePlatformKind.Unknown, "")]
        public void Platform_ClassifyAndDescribe(string platform, DevicePlatformKind kind, string label)
        {
            Assert.Equal(kind, AccountSyncPresenter.ClassifyPlatform(platform));
            Assert.Equal(label, AccountSyncPresenter.DescribePlatform(platform));
        }

        [Fact]
        public void History_ClassifiesCurrentRotationAndInitial()
        {
            var items = new List<HistoryEntryInput>
            {
                new HistoryEntryInput { Revision = "5", KeyVersion = 2 },
                new HistoryEntryInput { Revision = "4", KeyVersion = 2 },
                new HistoryEntryInput { Revision = "3", KeyVersion = 2 },
                new HistoryEntryInput { Revision = "2", KeyVersion = 1 },
                new HistoryEntryInput { Revision = "1", KeyVersion = 1 }
            };
            var kinds = AccountSyncPresenter.ClassifyHistory(items, "5");
            Assert.Equal(new[]
            {
                HistoryEventKind.Current,
                HistoryEventKind.Update,
                HistoryEventKind.KeyRotated,
                HistoryEventKind.Update,
                HistoryEventKind.Initial
            }, kinds);
        }

        [Fact]
        public void History_PagedList_OldestIsNotInitialUnlessRevisionOne()
        {
            var items = new List<HistoryEntryInput>
            {
                new HistoryEntryInput { Revision = "30", KeyVersion = 1 },
                new HistoryEntryInput { Revision = "29", KeyVersion = 1 }
            };
            var kinds = AccountSyncPresenter.ClassifyHistory(items, "0");
            Assert.Equal(new[] { HistoryEventKind.Update, HistoryEventKind.Update }, kinds);
            Assert.Empty(AccountSyncPresenter.ClassifyHistory(null, "1"));
            Assert.Empty(AccountSyncPresenter.ClassifyHistory(new List<HistoryEntryInput>(), "1"));
        }

        // App 侧把字形键查 Themes/Tokens.xaml、文案键查 resw：漏一个真机上就是空白图标 / 空白文字。
        [Fact]
        public void EveryGlyphKey_IsDefinedInTokens()
        {
            string tokens = File.ReadAllText(RepoFile("src", "SshTool.App", "Themes", "Tokens.xaml"), Encoding.UTF8);
            foreach (string key in AllGlyphKeys())
            {
                Assert.Contains("x:Key=\"" + key + "\"", tokens);
            }
        }

        [Theory]
        [InlineData("zh-cn")]
        [InlineData("en-us")]
        public void EveryTextKey_HasLocalizedText(string lang)
        {
            string resw = File.ReadAllText(
                RepoFile("src", "SshTool.App", "Strings", lang, "Resources.resw"), Encoding.UTF8);
            foreach (string key in AllTextKeys())
            {
                Assert.Contains("name=\"" + key + "\"", resw);
            }
            foreach (HistoryEventKind kind in Enum.GetValues(typeof(HistoryEventKind)))
            {
                Assert.Contains("name=\"AccountSync_History_" + kind + "\"", resw);
            }
        }

        // App 接线：本页的路由判定必须走 AccountSyncPresenter（只在未登录时转走）；
        // 解锁 / 建库完成后 GoAfterAuth 要把返回栈里旧的同类页剪掉（否则返回键会回到旧状态页）。
        [Fact]
        public void App_UsesPresenterRouting_AndPrunesDuplicateTarget()
        {
            string vm = File.ReadAllText(
                RepoFile("src", "SshTool.App", "ViewModels", "Sync", "AccountSyncViewModel.cs"), Encoding.UTF8);
            Assert.Contains("AccountSyncPresenter.NeedsRouting(", vm);
            Assert.Contains("LockVaultAsync", vm);
            string nav = File.ReadAllText(
                RepoFile("src", "SshTool.App", "Views", "Sync", "SyncNavigation.cs"), Encoding.UTF8);
            Assert.Contains("top != targetPage", nav);
        }

        private static IEnumerable<string> AllGlyphKeys()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (SyncPhase phase in Enum.GetValues(typeof(SyncPhase)))
            {
                keys.Add(AccountSyncPresenter.GlyphOf(phase));
            }
            foreach (DevicePlatformKind kind in Enum.GetValues(typeof(DevicePlatformKind)))
            {
                keys.Add(AccountSyncPresenter.GlyphOf(kind));
            }
            foreach (HistoryEventKind kind in Enum.GetValues(typeof(HistoryEventKind)))
            {
                keys.Add(AccountSyncPresenter.GlyphOf(kind));
            }
            foreach (VaultCardSpec spec in AllVaultSpecs())
            {
                keys.Add(spec.GlyphKey);
            }
            return keys;
        }

        private static IEnumerable<string> AllTextKeys()
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (VaultCardSpec spec in AllVaultSpecs())
            {
                if (spec.TitleKey != null)
                {
                    keys.Add(spec.TitleKey);
                }
                if (spec.DescriptionKey != null)
                {
                    keys.Add(spec.DescriptionKey);
                }
            }
            keys.Add(AccountSyncPresenter.Hero(SignedIn(), State(SyncPhase.Locked, VaultStatus.Locked)).SyncNowHintKey);
            keys.Add(AccountSyncPresenter.Hero(SignedIn(), State(SyncPhase.Disabled, VaultStatus.Missing)).SyncNowHintKey);
            keys.Add(AccountSyncPresenter.Hero(SignedIn(), State(SyncPhase.Disabled, VaultStatus.Ready, false)).SyncNowHintKey);
            return keys;
        }

        private static IEnumerable<VaultCardSpec> AllVaultSpecs()
        {
            foreach (VaultStatus vault in Enum.GetValues(typeof(VaultStatus)))
            {
                yield return AccountSyncPresenter.VaultCard(SignedIn(), State(SyncPhase.Idle, vault), true);
                yield return AccountSyncPresenter.VaultCard(SignedIn(), State(SyncPhase.Idle, vault), false);
            }
            yield return AccountSyncPresenter.VaultCard(null, null, true);
        }

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                dir = dir.Parent;
            }
            throw new FileNotFoundException(string.Join("/", parts));
        }
    }
}
