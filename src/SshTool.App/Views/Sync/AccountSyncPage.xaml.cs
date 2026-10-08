using System;
using SshTool.App.Controls;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.App.ViewModels.Sync;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Auth;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views.Sync
{
    // U17/U18 同步状态页（02-UI-DESIGN.md §5.13 状态 Pivot + 设备/历史 Pivot）。
    // 前置路由：只有未登录由 ViewModel.NeedsRouting 在本页 OnNavigatedTo 经 SyncNavigation 转去登录页。
    // feat/account-sync-ui：保险库未建 / 已锁定不再转走——本页保险库卡给出「创建保险库」/
    // 「输入同步密码解锁」（用户反馈「在哪里输入保险箱密码」），已解锁时可「锁定保险库」（本机忘记同步密码）。
    // 危险操作（删除保险库/注销账号/退出所有设备/撤销设备/恢复与清空历史）走 ContentDialog 二次确认。
    public sealed partial class AccountSyncPage : Page
    {
        // fix/login-feedback：本页只转走一次。StateChanged 每次都会重报 "Screen"，
        // 不加闸会在一次状态抖动里连续发起多次导航。
        private bool _routed;

        public AccountSyncPage()
        {
            ViewModel = CreateViewModel();
            this.InitializeComponent();
            ViewModel.PropertyChanged += OnViewModelChanged;
            ViewModel.RequestSecurityRotate += OnRequestSecurityRotate;
            DevicesList.ItemsSource = ViewModel.Devices;
            HistoryList.ItemsSource = ViewModel.History;
        }

        public AccountSyncViewModel ViewModel { get; private set; }

        private DialogService Dlg
        {
            get
            {
                DialogService service;
                ServiceRegistry.TryGet(out service);
                return service;
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            // fix/login-feedback：未登录会被路由到登录页；本页只是中转，
            // 转走后从返回栈拿掉自己，登录完成后返回键不会回到一个旧的状态页副本。
            if (ViewModel.NeedsRouting && RouteAway())
            {
                return;
            }
            Header.Title = Localized.Get("AccountSync_Title", "账号与同步");
            Header.ShowBackButton = Frame.CanGoBack;
            RefreshHero();
            RefreshVault();
            RefreshSwitches();
            RefreshErrors();
            RefreshDevicesVisual();
            RefreshHistoryVisual();
            // 进入状态页即触发一次设备与历史加载（懒加载）。
            ViewModel.RefreshDevicesCommand.Execute(null);
            ViewModel.RefreshHistoryCommand.Execute(null);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ViewModel.PropertyChanged -= OnViewModelChanged;
            ViewModel.RequestSecurityRotate -= OnRequestSecurityRotate;
            ViewModel.Detach(); // O03：VM 挂在应用级 SyncCoordinator 上
            base.OnNavigatedFrom(e);
        }

        private bool RouteAway()
        {
            if (_routed)
            {
                return true;
            }
            _routed = SyncNavigation.GoAfterAuth(Frame, 1, true);
            return _routed;
        }

        private static AccountSyncViewModel CreateViewModel()
        {
            AppServices services = AppServices.Current;
            SyncCoordinator sync = services != null ? services.Sync : null;
            AuthService auth = services != null ? services.AuthService : null;
            return new AccountSyncViewModel(sync, auth);
        }

        // ---------------- 状态卡 ----------------

        private void RefreshHero()
        {
            StatusHeroSpec hero = ViewModel.Hero ?? new StatusHeroSpec();
            bool spin = hero.Spin;
            HeroBadge.Background = AccountSyncVisuals.SoftBrush(hero.Tone);
            HeroGlyph.Glyph = AccountSyncVisuals.Glyph(hero.GlyphKey);
            HeroGlyph.Foreground = AccountSyncVisuals.ToneBrush(hero.Tone);
            HeroGlyph.Visibility = spin ? Visibility.Collapsed : Visibility.Visible;
            HeroSpinner.IsActive = spin;
            HeroSpinner.Visibility = spin ? Visibility.Visible : Visibility.Collapsed;
            HeroTitle.Text = ViewModel.StatusTitle;

            LastSyncedLine.Text = string.IsNullOrEmpty(ViewModel.LastSyncedRelative)
                ? GetString("AccountSync_LastSyncedNone", "Not synced yet")
                : GetString("AccountSync_LastSyncedFormat", "Last synced {0}", ViewModel.LastSyncedRelative);
            if (!string.IsNullOrEmpty(ViewModel.NextRetryRelative))
            {
                NextRetryLine.Text = GetString("AccountSync_NextRetry", "Next retry {0}", ViewModel.NextRetryRelative);
                NextRetryLine.Visibility = Visibility.Visible;
            }
            else
            {
                NextRetryLine.Visibility = Visibility.Collapsed;
            }

            if (ViewModel.HasStatusDetail)
            {
                SyncTone tone = hero.DetailTone;
                HeroDetail.Background = AccountSyncVisuals.SoftBrush(tone);
                HeroDetailGlyph.Glyph = AccountSyncVisuals.Glyph(
                    tone == SyncTone.Danger ? "IconError" : (tone == SyncTone.Warning ? "IconWarning" : "IconInfo"));
                HeroDetailGlyph.Foreground = AccountSyncVisuals.ToneBrush(tone);
                HeroDetailText.Text = ViewModel.StatusDetail;
                HeroDetail.Visibility = Visibility.Visible;
            }
            else
            {
                HeroDetailText.Text = string.Empty;
                HeroDetail.Visibility = Visibility.Collapsed;
            }

            EmailText.Text = ViewModel.Email;
            DeviceText.Text = string.IsNullOrEmpty(ViewModel.DeviceName)
                ? GetString("AccountSync_ThisDevice", "This device")
                : ViewModel.DeviceName;
            MetaLine.Text = GetString("AccountSync_Meta", "Revision r{0} · Key v{1}",
                ViewModel.Revision, ViewModel.KeyVersion.ToString());

            SyncNowButton.IsEnabled = ViewModel.SyncNowCommand.CanExecute(null);
            string hint = ViewModel.SyncNowHint;
            SyncNowHint.Text = hint;
            SyncNowHint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
            ResolveConflictButton.Visibility = ViewModel.HasConflict ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------- 保险库卡（同步密码在这里输入） ----------------

        private void RefreshVault()
        {
            VaultCardSpec card = ViewModel.VaultCard ?? new VaultCardSpec();
            Visibility shown = card.Kind == VaultCardKind.Hidden ? Visibility.Collapsed : Visibility.Visible;
            VaultHeader.Visibility = shown;
            VaultCardBorder.Visibility = shown;
            VaultBadge.Background = AccountSyncVisuals.SoftBrush(card.Tone);
            VaultGlyph.Glyph = AccountSyncVisuals.Glyph(card.GlyphKey);
            VaultGlyph.Foreground = AccountSyncVisuals.ToneBrush(card.Tone);
            VaultTitle.Text = ViewModel.VaultTitle;
            VaultDescription.Text = ViewModel.VaultDescription;
            UnlockVaultButton.Visibility = card.ShowUnlock ? Visibility.Visible : Visibility.Collapsed;
            CreateVaultButton.Visibility = card.ShowCreate ? Visibility.Visible : Visibility.Collapsed;
            VaultReadyPanel.Visibility = card.ShowRemember || card.ShowLock || card.ShowChangePassword
                ? Visibility.Visible : Visibility.Collapsed;
            ChangeSyncPasswordButton.Visibility = card.ShowChangePassword ? Visibility.Visible : Visibility.Collapsed;
            LockVaultButton.Visibility = card.ShowLock ? Visibility.Visible : Visibility.Collapsed;
            LockVaultButton.IsEnabled = ViewModel.LockVaultCommand.CanExecute(null);
            DeleteVaultButton.Visibility = card.ShowDeleteVault ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshSwitches()
        {
            // 解绑事件再赋值，避免 Toggled 递归。
            EnableSwitch.Toggled -= OnEnableSyncToggled;
            AutoSyncSwitch.Toggled -= OnAutoSyncToggled;
            SyncPasswordsSwitch.Toggled -= OnSyncPasswordsToggled;
            RememberVaultKeySwitch.Toggled -= OnRememberVaultKeyToggled;
            RememberVaultKeySwitch.IsOn = ViewModel.RememberVaultKey;
            EnableSwitch.IsOn = ViewModel.EnableSync;
            AutoSyncSwitch.IsOn = ViewModel.AutoSync;
            AutoSyncSwitch.IsEnabled = ViewModel.EnableSync;
            SyncPasswordsSwitch.IsOn = ViewModel.SyncPasswords;
            SyncPrivateKeysSwitch.IsOn = ViewModel.SyncPrivateKeys;
            SyncPrivateKeysSwitch.IsEnabled = ViewModel.SyncPrivateKeysEnabled;
            SyncPrivateKeysDesc.Text = ViewModel.SyncPrivateKeysDescription;
            EnableSwitch.Toggled += OnEnableSyncToggled;
            AutoSyncSwitch.Toggled += OnAutoSyncToggled;
            SyncPasswordsSwitch.Toggled += OnSyncPasswordsToggled;
            RememberVaultKeySwitch.Toggled += OnRememberVaultKeyToggled;
        }

        private void RefreshErrors()
        {
            if (ViewModel.HasError)
            {
                ErrorText.Text = ViewModel.ErrorMessage;
                ErrorPanel.Visibility = Visibility.Visible;
            }
            else
            {
                ErrorText.Text = string.Empty;
                ErrorPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void RefreshBusy()
        {
            bool loggingOut = ViewModel.IsLoggingOut;
            bool locking = ViewModel.IsLockingVault;
            Working.IsActive = loggingOut || locking;
            Working.Message = loggingOut
                ? GetString("AccountSync_LoggingOut", "Signing out…")
                : (locking ? GetString("AccountSync_LockingVault", "Locking the vault…") : string.Empty);
            LockVaultButton.IsEnabled = ViewModel.LockVaultCommand.CanExecute(null);
        }

        // ---------------- 命令接线 ----------------

        private void OnSyncNowClick(object sender, RoutedEventArgs e)
        {
            ViewModel.SyncNowCommand.Execute(null);
        }

        private void OnResolveConflictClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(SyncConflictPage));
        }

        private void OnUnlockVaultClick(object sender, RoutedEventArgs e)
        {
            // 解锁成功后 GoAfterAuth 回到新的状态页，并把返回栈里这一页剪掉（见 SyncNavigation）。
            Frame.Navigate(typeof(VaultUnlockPage));
        }

        private void OnCreateVaultClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(VaultSetupPage));
        }

        private void OnLockVaultClick(object sender, RoutedEventArgs e)
        {
            ConfirmLockVaultAsync().Forget("AccountSyncPage.ConfirmLockVault", AppLog.Logger);
        }

        private void OnEnableSyncToggled(object sender, RoutedEventArgs e)
        {
            ToggleSwitch sw = sender as ToggleSwitch;
            if (sw == null)
            {
                return;
            }
            ViewModel.SetEnableSyncAsync(sw.IsOn).Forget("AccountSyncPage.SetEnableSync", AppLog.Logger);
        }

        private void OnAutoSyncToggled(object sender, RoutedEventArgs e)
        {
            ToggleSwitch sw = sender as ToggleSwitch;
            if (sw == null)
            {
                return;
            }
            ViewModel.SetAutoSyncAsync(sw.IsOn).Forget("AccountSyncPage.SetAutoSync", AppLog.Logger);
        }

        private void OnSyncPasswordsToggled(object sender, RoutedEventArgs e)
        {
            ToggleSwitch sw = sender as ToggleSwitch;
            if (sw == null)
            {
                return;
            }
            ViewModel.ToggleSyncPasswords(sw.IsOn);
        }

        private void OnRememberVaultKeyToggled(object sender, RoutedEventArgs e)
        {
            ToggleSwitch sw = sender as ToggleSwitch;
            if (sw == null)
            {
                return;
            }
            ViewModel.SetRememberVaultKeyAsync(sw.IsOn)
                .Forget("AccountSyncPage.SetRememberVaultKey", AppLog.Logger);
        }

        private void OnChangeSyncPasswordClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(SecurityRotatePage), SecurityRotateMode.ChangeSyncPassword);
        }

        private void OnChangeLoginPasswordClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(ChangeLoginPasswordPage));
        }

        private void OnDeleteVaultClick(object sender, RoutedEventArgs e)
        {
            ConfirmDeleteVaultAsync().Forget("AccountSyncPage.ConfirmDeleteVault", AppLog.Logger);
        }

        private void OnDeleteAccountClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(DeleteAccountPage));
        }

        private void OnLogoutClick(object sender, RoutedEventArgs e)
        {
            ConfirmLogoutAsync(all: false).Forget("AccountSyncPage.ConfirmLogout", AppLog.Logger);
        }

        private void OnLogoutAllClick(object sender, RoutedEventArgs e)
        {
            ConfirmLogoutAsync(all: true).Forget("AccountSyncPage.ConfirmLogout", AppLog.Logger);
        }

        private void OnBackRequested(object sender, EventArgs e)
        {
            if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private void OnRequestSecurityRotate(object sender, EventArgs e)
        {
            Frame.Navigate(typeof(SecurityRotatePage), SecurityRotateMode.DisableSensitiveSync);
        }

        // ---------------- 设备与历史 ----------------

        private void OnRefreshDevicesClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.RefreshDevicesCommand.CanExecute(null))
            {
                ViewModel.RefreshDevicesCommand.Execute(null);
            }
        }

        private void OnRefreshHistoryClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.RefreshHistoryCommand.CanExecute(null))
            {
                ViewModel.RefreshHistoryCommand.Execute(null);
            }
        }

        private void OnDeviceClick(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as DeviceRow;
            if (row != null)
            {
                ShowDeviceMenu(row, DevicesList.ContainerFromItem(row) as FrameworkElement);
            }
        }

        private void OnDeviceMoreClick(object sender, RoutedEventArgs e)
        {
            var anchor = sender as FrameworkElement;
            var row = anchor != null ? anchor.DataContext as DeviceRow : null;
            if (row != null)
            {
                ShowDeviceMenu(row, anchor);
            }
        }

        // 设备操作菜单：重命名（含本机）；撤销仅远端设备（红字 + 二次确认），本机给出灰显说明。
        private void ShowDeviceMenu(DeviceRow row, FrameworkElement anchor)
        {
            var menu = new MenuFlyout();
            var rename = new MenuFlyoutItem
            {
                Text = GetString("AccountSync_RenameMenu", "Rename…"),
                Tag = row
            };
            rename.Click += OnRenameDeviceMenuClick;
            menu.Items.Add(rename);
            menu.Items.Add(new MenuFlyoutSeparator());
            if (row.IsCurrent)
            {
                menu.Items.Add(new MenuFlyoutItem
                {
                    Text = GetString("AccountSync_RevokeCurrentDisabled", "Use Sign out for this device"),
                    IsEnabled = false
                });
            }
            else
            {
                var revoke = new MenuFlyoutItem
                {
                    Text = GetString("AccountSync_Revoke", "Revoke device…"),
                    Tag = row,
                    Style = Application.Current.Resources["DangerMenuItemStyle"] as Style
                };
                revoke.Click += OnRevokeDeviceMenuClick;
                menu.Items.Add(revoke);
            }
            if (anchor != null)
            {
                menu.ShowAt(anchor);
            }
            else
            {
                menu.ShowAt(DevicesList);
            }
        }

        private void OnRenameDeviceMenuClick(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuFlyoutItem;
            var row = item != null ? item.Tag as DeviceRow : null;
            if (row != null)
            {
                PromptRenameDeviceAsync(row).Forget("AccountSyncPage.PromptRenameDevice", AppLog.Logger);
            }
        }

        private void OnRevokeDeviceMenuClick(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuFlyoutItem;
            var row = item != null ? item.Tag as DeviceRow : null;
            if (row != null && !row.IsCurrent)
            {
                ConfirmRevokeDeviceAsync(row).Forget("AccountSyncPage.ConfirmRevokeDevice", AppLog.Logger);
            }
        }

        private void OnHistoryClick(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as HistoryRow;
            // 当前云端版本无需恢复（行上也不显示箭头）。
            if (row != null && !row.IsCurrent)
            {
                ConfirmRestoreHistoryAsync(row).Forget("AccountSyncPage.ConfirmRestoreHistory", AppLog.Logger);
            }
        }

        private void OnClearHistoryClick(object sender, RoutedEventArgs e)
        {
            ConfirmClearHistoryAsync().Forget("AccountSyncPage.ConfirmClearHistory", AppLog.Logger);
        }

        // ---------------- ViewModel 属性变更 ----------------

        private void OnViewModelChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            string name = e.PropertyName;
            if (name == "Screen" && ViewModel.NeedsRouting)
            {
                // 如退出登录 / 登录失效后：转去登录页。
                RouteAway();
                return;
            }
            if (name == "StatusTitle" || name == "StatusDetail" || name == "Hero" || name == "SyncNowHint"
                || name == "IsSyncing" || name == "HasConflict" || name == "IsSyncNowAvailable"
                || name == "Email" || name == "DeviceName" || name == "LastSyncedRelative"
                || name == "NextRetryRelative" || name == "Revision" || name == "KeyVersion")
            {
                RefreshHero();
            }
            if (name == "VaultCard" || name == "VaultTitle" || name == "VaultDescription")
            {
                RefreshVault();
            }
            if (name == "EnableSync" || name == "AutoSync" || name == "SyncPasswords"
                || name == "SyncPrivateKeys" || name == "SyncPrivateKeysEnabled"
                || name == "RememberVaultKey")
            {
                RefreshSwitches();
            }
            if (name == "HasError" || name == "ErrorMessage")
            {
                RefreshErrors();
            }
            if (name == "IsLoggingOut" || name == "IsLockingVault")
            {
                RefreshBusy();
            }
            if (name == "HasDevices" || name == "IsLoadingDevices" || name == "DevicesLoadFailed")
            {
                RefreshDevicesVisual();
            }
            if (name == "HasHistory" || name == "IsLoadingHistory" || name == "HistoryLoadFailed")
            {
                RefreshHistoryVisual();
            }
        }

        private void RefreshDevicesVisual()
        {
            bool loading = ViewModel.IsLoadingDevices;
            bool has = ViewModel.HasDevices;
            bool failed = ViewModel.DevicesLoadFailed;
            DevicesProgress.Visibility = loading && !has ? Visibility.Visible : Visibility.Collapsed;
            RefreshDevicesButton.IsEnabled = !loading;
            if (failed)
            {
                DevicesSummary.Text = GetString("Sync_DeviceListFailed", "Could not load the device list");
            }
            else
            {
                DevicesSummary.Text = has
                    ? GetString("AccountSync_DevicesCount", "{0} devices", ViewModel.Devices.Count.ToString())
                    : string.Empty;
            }
            if (!loading && !has)
            {
                ShowEmpty(DevicesEmpty, failed, "IconDevices",
                    failed ? GetString("Sync_DeviceListFailed", "Could not load the device list")
                           : GetString("AccountSync_DevicesEmpty", "No devices"),
                    failed ? GetString("AccountSync_ListFailedDesc", "Check the network and try again.")
                           : GetString("AccountSync_DevicesEmptyDesc", string.Empty));
                DevicesList.Visibility = Visibility.Collapsed;
            }
            else
            {
                DevicesEmpty.Visibility = Visibility.Collapsed;
                DevicesList.Visibility = Visibility.Visible;
            }
        }

        private void RefreshHistoryVisual()
        {
            bool loading = ViewModel.IsLoadingHistory;
            bool has = ViewModel.HasHistory;
            bool failed = ViewModel.HistoryLoadFailed;
            HistoryProgress.Visibility = loading && !has ? Visibility.Visible : Visibility.Collapsed;
            RefreshHistoryButton.IsEnabled = !loading;
            ClearHistoryButton.IsEnabled = !loading && has;
            if (failed)
            {
                HistorySummary.Text = GetString("Sync_HistoryListFailed", "Could not load history versions");
            }
            else
            {
                HistorySummary.Text = has
                    ? GetString("AccountSync_HistoryCount", "Last {0} versions · tap one to restore", ViewModel.History.Count.ToString())
                    : string.Empty;
            }
            if (!loading && !has)
            {
                ShowEmpty(HistoryEmpty, failed, "IconHistory",
                    failed ? GetString("Sync_HistoryListFailed", "Could not load history versions")
                           : GetString("AccountSync_HistoryEmpty", "No history versions"),
                    failed ? GetString("AccountSync_ListFailedDesc", "Check the network and try again.")
                           : GetString("AccountSync_HistoryEmptyDesc", string.Empty));
                HistoryList.Visibility = Visibility.Collapsed;
            }
            else
            {
                HistoryEmpty.Visibility = Visibility.Collapsed;
                HistoryList.Visibility = Visibility.Visible;
            }
        }

        private void ShowEmpty(EmptyState empty, bool failed, string glyphKey, string title, string description)
        {
            empty.Kind = failed ? EmptyStateKind.Offline : EmptyStateKind.Normal;
            empty.Glyph = failed ? string.Empty : AccountSyncVisuals.Glyph(glyphKey);
            empty.Title = title;
            empty.Description = description;
            empty.PrimaryText = failed ? GetString("AccountSync_Retry", "Retry") : null;
            empty.Visibility = Visibility.Visible;
        }

        // ---------------- 对话框 ----------------

        private async System.Threading.Tasks.Task PromptRenameDeviceAsync(DeviceRow row)
        {
            var renameDialog = new ContentDialog
            {
                Title = GetString("AccountSync_RenameDeviceTitle", "Rename device"),
                PrimaryButtonText = GetString("AccountSync_Rename", "Rename"),
                CloseButtonText = GetString("AccountSync_Cancel", "Cancel")
            };
            // fix/auth-audit：限制到服务端设备名上限（超长此前会被拒为 VALIDATION_ERROR 且无提示）。
            var input = new TextBox
            {
                Text = row.Name ?? string.Empty,
                AcceptsReturn = false,
                MaxLength = AuthService.MaxDeviceNameLength
            };
            renameDialog.Content = input;
            ContentDialogResult result = await Dlg.ShowAsync(renameDialog);
            if (result == ContentDialogResult.Primary)
            {
                string newName = (input.Text ?? string.Empty).Trim();
                if (newName.Length > 0 && !string.Equals(newName, row.Name, StringComparison.Ordinal))
                {
                    ViewModel.RenameDeviceAsync(row.Id, newName).Forget("AccountSyncPage.RenameDevice", AppLog.Logger);
                }
            }
        }

        private async System.Threading.Tasks.Task ConfirmRevokeDeviceAsync(DeviceRow row)
        {
            var result = await ConfirmDialog.ShowAsync(
                title: GetString("AccountSync_RevokeDeviceTitle", "Revoke device"),
                message: GetString("AccountSync_RevokeDeviceMessage",
                    "The device must sign in again to sync. This cannot be undone."),
                confirmText: GetString("AccountSync_RevokeConfirm", "Revoke"),
                cancelText: GetString("AccountSync_Cancel", "Cancel"),
                isDanger: true);
            if (result != null && result.Confirmed)
            {
                ViewModel.RevokeDeviceAsync(row.Id).Forget("AccountSyncPage.RevokeDevice", AppLog.Logger);
            }
        }

        private async System.Threading.Tasks.Task ConfirmLockVaultAsync()
        {
            var result = await ConfirmDialog.ShowAsync(
                title: GetString("AccountSync_LockVaultTitle", "Lock the vault"),
                message: GetString("AccountSync_LockVaultMessage",
                    "This device forgets the sync password and sync pauses until you enter it again. Hosts on this device are kept."),
                confirmText: GetString("AccountSync_LockVaultConfirm", "Lock"),
                cancelText: GetString("AccountSync_Cancel", "Cancel"),
                isDanger: false);
            if (result != null && result.Confirmed && ViewModel.LockVaultCommand.CanExecute(null))
            {
                ViewModel.LockVaultCommand.Execute(null);
            }
        }

        private async System.Threading.Tasks.Task ConfirmRestoreHistoryAsync(HistoryRow row)
        {
            var result = await ConfirmDialog.ShowAsync(
                title: GetString("AccountSync_RestoreTitle", "Restore this version"),
                message: GetString("AccountSync_RestoreMessage",
                    "A new cloud version is created from this one and overwrites local settings."),
                confirmText: GetString("AccountSync_Restore", "Restore"),
                cancelText: GetString("AccountSync_Cancel", "Cancel"),
                isDanger: true);
            if (result != null && result.Confirmed)
            {
                ViewModel.RestoreRevisionAsync(row.Revision).Forget("AccountSyncPage.RestoreRevision", AppLog.Logger);
            }
        }

        private async System.Threading.Tasks.Task ConfirmClearHistoryAsync()
        {
            var result = await ConfirmDialog.ShowAsync(
                title: GetString("AccountSync_ClearHistoryTitle", "Clear history"),
                message: GetString("AccountSync_ClearHistoryMessage",
                    "All cloud history versions will be deleted permanently."),
                confirmText: GetString("AccountSync_Clear", "Clear"),
                cancelText: GetString("AccountSync_Cancel", "Cancel"),
                isDanger: true);
            if (result != null && result.Confirmed)
            {
                ViewModel.ClearHistoryAsync().Forget("AccountSyncPage.ClearHistory", AppLog.Logger);
            }
        }

        private async System.Threading.Tasks.Task ConfirmDeleteVaultAsync()
        {
            var result = await ConfirmDialog.ShowAsync(
                title: GetString("AccountSync_DeleteVaultTitle", "Delete cloud vault"),
                message: GetString("AccountSync_DeleteVaultMessage",
                    "The cloud vault and its history will be deleted; local settings are kept."),
                confirmText: GetString("AccountSync_Delete", "Delete"),
                cancelText: GetString("AccountSync_Cancel", "Cancel"),
                isDanger: true);
            if (result != null && result.Confirmed)
            {
                Frame.Navigate(typeof(DeleteVaultPage));
            }
        }

        private async System.Threading.Tasks.Task ConfirmLogoutAsync(bool all)
        {
            var result = await ConfirmDialog.ShowAsync(
                title: all
                    ? GetString("AccountSync_LogoutAllTitle", "Sign out of all devices")
                    : GetString("AccountSync_LogoutTitle", "Sign out"),
                message: all
                    ? GetString("AccountSync_LogoutAllMessage", "All devices will be signed out and must sign in again to sync.")
                    : GetString("AccountSync_LogoutMessage", "This device will be signed out."),
                confirmText: all
                    ? GetString("AccountSync_LogoutAllLabel", "Sign out all")
                    : GetString("AccountSync_LogoutLabel", "Sign out"),
                cancelText: GetString("AccountSync_Cancel", "Cancel"),
                isDanger: all);
            if (result != null && result.Confirmed)
            {
                if (all)
                {
                    ViewModel.LogoutAllCommand.Execute(null);
                }
                else
                {
                    ViewModel.LogoutCommand.Execute(null);
                }
            }
        }

        // ---------------- resw 工具 ----------------

        private string GetString(string key, string fallback)
        {
            try
            {
                var loader = ResourceLoader.GetForCurrentView();
                string value = loader.GetString(key);
                return string.IsNullOrEmpty(value) ? fallback : value;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private string GetString(string key, string fallback, params object[] args)
        {
            string format = GetString(key, fallback);
            try
            {
                return string.Format(format, args);
            }
            catch (Exception)
            {
                return format;
            }
        }
    }
}
