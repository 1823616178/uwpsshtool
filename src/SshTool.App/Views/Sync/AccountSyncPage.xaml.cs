using System;
using SshTool.App.Controls;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.App.Platform;
using SshTool.App.ViewModels.Sync;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Auth;
using Windows.ApplicationModel.Resources;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views.Sync
{
    // U17/U18 同步状态页（02-UI-DESIGN.md §5.13 状态 Pivot + 设备/历史 Pivot）。
    // 前置路由：未登录/建库/解锁由 ViewModel.NeedsRouting 在本页 OnNavigatedTo 经 SyncNavigation 转出；
    // 到达本页即已登录。危险操作（删除保险库/注销账号/退出所有设备）走 ContentDialog 二次确认。
    public sealed partial class AccountSyncPage : Page
    {
        public AccountSyncPage()
        {
            ViewModel = CreateViewModel();
            this.InitializeComponent();
            ViewModel.PropertyChanged += OnViewModelChanged;
            ViewModel.RequestSecurityRotate += OnRequestSecurityRotate;
        }

        public AccountSyncViewModel ViewModel { get; private set; }

        private NavigationService Nav
        {
            get { return Get<NavigationService>(); }
        }

        private DialogService Dlg
        {
            get { return Get<DialogService>(); }
        }

        private T Get<T>() where T : class
        {
            T service;
            ServiceRegistry.TryGet(out service);
            return service;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (ViewModel.NeedsRouting)
            {
                SyncNavigation.GoAfterAuth(Frame, 1);
                return;
            }
            RefreshHeader();
            BindStaticText();
            RefreshStatusVisual();
            RefreshSwitches();
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

        private static AccountSyncViewModel CreateViewModel()
        {
            AppServices services = AppServices.Current;
            SyncCoordinator sync = services != null ? services.Sync : null;
            AuthService auth = services != null ? services.AuthService : null;
            return new AccountSyncViewModel(sync, auth);
        }

        // ---------------- 页头与状态卡 ----------------

        private void RefreshHeader()
        {
            Header.Title = Localized.Get("AccountSync_Title", "账号与同步");
            Header.ShowBackButton = Frame.CanGoBack;
        }

        private void BindStaticText()
        {
            var state = GetServiceState();
            AccountLine.Text = BuildAccountLine();
            MetaLine.Text = BuildMetaLine(state);
            LastSyncedLine.Text = FormatLastSynced(ViewModel.LastSyncedRelative);
            if (!string.IsNullOrEmpty(ViewModel.NextRetryRelative))
            {
                NextRetryLine.Text = GetString("AccountSync_NextRetry", "下次重试 {0}", ViewModel.NextRetryRelative);
                NextRetryLine.Visibility = Visibility.Visible;
            }
            else
            {
                NextRetryLine.Visibility = Visibility.Collapsed;
            }
        }

        private SyncState GetServiceState()
        {
            AppServices services = AppServices.Current;
            if (services == null || services.Sync == null)
            {
                return null;
            }
            try
            {
                return services.Sync.State;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private string BuildAccountLine()
        {
            string email = ViewModel.Email;
            string device = ViewModel.DeviceName;
            if (!string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(device))
            {
                return email + " · " + device;
            }
            return email ?? device ?? string.Empty;
        }

        private string BuildMetaLine(SyncState state)
        {
            if (state == null)
            {
                return string.Empty;
            }
            string rev = state.Revision ?? "0";
            return GetString("AccountSync_Meta", "版本 r{0} · 密钥 v{1}", rev, state.KeyVersion.ToString());
        }

        private string FormatLastSynced(string relative)
        {
            if (string.IsNullOrEmpty(relative))
            {
                return GetString("AccountSync_LastSyncedNone", "尚未同步");
            }
            return GetString("AccountSync_LastSynced", "上次同步 {0}", relative);
        }

        private void RefreshStatusVisual()
        {
            StatusIcon.Glyph = MapIconGlyph(ViewModel.StatusIconKey);
            StatusIcon.Foreground = MapIconBrush(ViewModel.StatusIconKey);
            StatusPill.Kind = MapPillKind(ViewModel.StatusIconKey);
            StatusPill.Text = ViewModel.StatusText;
            SyncNowButton.IsEnabled = !ViewModel.IsSyncing;
            ResolveConflictButton.Visibility = ViewModel.HasConflict
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshSwitches()
        {
            // 解绑事件再赋值，避免 Toggled 递归。
            EnableSwitch.Toggled -= OnEnableSyncToggled;
            AutoSyncSwitch.Toggled -= OnAutoSyncToggled;
            SyncPasswordsSwitch.Toggled -= OnSyncPasswordsToggled;
            EnableSwitch.IsOn = ViewModel.EnableSync;
            AutoSyncSwitch.IsOn = ViewModel.AutoSync;
            SyncPasswordsSwitch.IsOn = ViewModel.SyncPasswords;
            SyncPrivateKeysSwitch.IsOn = ViewModel.SyncPrivateKeys;
            SyncPrivateKeysSwitch.IsEnabled = ViewModel.SyncPrivateKeysEnabled;
            EnableSwitch.Toggled += OnEnableSyncToggled;
            AutoSyncSwitch.Toggled += OnAutoSyncToggled;
            SyncPasswordsSwitch.Toggled += OnSyncPasswordsToggled;
        }

        private void RefreshErrors()
        {
            if (ViewModel.HasError)
            {
                ErrorText.Text = ViewModel.ErrorMessage;
                ErrorText.Visibility = Visibility.Visible;
            }
            else
            {
                ErrorText.Visibility = Visibility.Collapsed;
            }
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

        private void OnDeviceClick(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as DeviceRow;
            if (row != null)
            {
                ShowDeviceMenuAsync(row).Forget("AccountSyncPage.ShowDeviceMenu", AppLog.Logger);
            }
        }

        private void OnHistoryClick(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as HistoryRow;
            if (row != null)
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
                SyncNavigation.GoAfterAuth(Frame, 1);
                return;
            }
            if (name == "StatusText" || name == "StatusIconKey" || name == "StatusSpin"
                || name == "IsSyncing" || name == "HasConflict"
                || name == "Email" || name == "DeviceName" || name == "LastSyncedRelative"
                || name == "NextRetryRelative" || name == "Revision" || name == "KeyVersion")
            {
                BindStaticText();
                RefreshStatusVisual();
            }
            if (name == "EnableSync" || name == "AutoSync" || name == "SyncPasswords"
                || name == "SyncPrivateKeys" || name == "SyncPrivateKeysEnabled")
            {
                RefreshSwitches();
            }
            if (name == "HasError" || name == "ErrorMessage")
            {
                RefreshErrors();
            }
            if (name == "HasDevices" || name == "IsLoadingDevices")
            {
                RefreshDevicesVisual();
            }
            if (name == "HasHistory" || name == "IsLoadingHistory")
            {
                RefreshHistoryVisual();
            }
        }

        private void RefreshDevicesVisual()
        {
            DevicesList.ItemsSource = ViewModel.Devices;
            bool loading = ViewModel.IsLoadingDevices;
            DevicesProgress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            if (!loading && !ViewModel.HasDevices)
            {
                DevicesEmpty.Glyph = "\uE1C9";
                DevicesEmpty.Title = GetString("AccountSync_DevicesEmpty", "暂无设备");
                DevicesEmpty.Description = string.Empty;
                DevicesEmpty.Visibility = Visibility.Visible;
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
            HistoryList.ItemsSource = ViewModel.History;
            bool loading = ViewModel.IsLoadingHistory;
            HistoryProgress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            ClearHistoryButton.IsEnabled = !loading;
            if (!loading && !ViewModel.HasHistory)
            {
                HistoryEmpty.Glyph = "\uE81C";
                HistoryEmpty.Title = GetString("AccountSync_HistoryEmpty", "暂无历史版本");
                HistoryEmpty.Description = string.Empty;
                HistoryEmpty.Visibility = Visibility.Visible;
                HistoryList.Visibility = Visibility.Collapsed;
            }
            else
            {
                HistoryEmpty.Visibility = Visibility.Collapsed;
                HistoryList.Visibility = Visibility.Visible;
            }
        }

        // ---------------- 对话框 ----------------

        private async System.Threading.Tasks.Task ShowDeviceMenuAsync(DeviceRow row)
        {
            var renameDialog = new ContentDialog
            {
                Title = GetString("AccountSync_RenameDeviceTitle", "重命名设备"),
                PrimaryButtonText = GetString("AccountSync_Rename", "重命名"),
                CloseButtonText = GetString("AccountSync_Cancel", "取消")
            };
            var input = new TextBox { Text = row.Name, AcceptsReturn = false };
            renameDialog.Content = input;
            bool canRevoke = !row.IsCurrent;
            if (canRevoke)
            {
                renameDialog.SecondaryButtonText = GetString("AccountSync_Revoke", "撤销设备…");
            }
            ContentDialogResult result = await Dlg.ShowAsync(renameDialog);
            if (result == ContentDialogResult.Primary)
            {
                string newName = input.Text ?? string.Empty;
                if (!string.IsNullOrEmpty(newName))
                {
                    ViewModel.RenameDeviceAsync(row.Id, newName).Forget("AccountSyncPage.RenameDevice", AppLog.Logger);
                }
            }
            else if (result == ContentDialogResult.Secondary && canRevoke)
            {
                ConfirmRevokeDeviceAsync(row).Forget("AccountSyncPage.ConfirmRevokeDevice", AppLog.Logger);
            }
        }

        private async System.Threading.Tasks.Task ConfirmRevokeDeviceAsync(DeviceRow row)
        {
            var result = await ConfirmDialog.ShowAsync(
                title: GetString("AccountSync_RevokeDeviceTitle", "撤销设备"),
                message: GetString("AccountSync_RevokeDeviceMessage",
                    "撤销后该设备将需要重新登录才能同步。此操作不可撤销。"),
                confirmText: GetString("AccountSync_Revoke", "撤销"),
                cancelText: GetString("AccountSync_Cancel", "取消"),
                isDanger: true);
            if (result != null && result.Confirmed)
            {
                ViewModel.RevokeDeviceAsync(row.Id).Forget("AccountSyncPage.RevokeDevice", AppLog.Logger);
            }
        }

        private async System.Threading.Tasks.Task ConfirmRestoreHistoryAsync(HistoryRow row)
        {
            var result = await ConfirmDialog.ShowAsync(
                title: GetString("AccountSync_RestoreTitle", "恢复到此版本"),
                message: GetString("AccountSync_RestoreMessage",
                    "将以此版本创建新的云端版本并覆盖本机配置。"),
                confirmText: GetString("AccountSync_Restore", "恢复"),
                cancelText: GetString("AccountSync_Cancel", "取消"),
                isDanger: true);
            if (result != null && result.Confirmed)
            {
                ViewModel.RestoreRevisionAsync(row.Revision).Forget("AccountSyncPage.RestoreRevision", AppLog.Logger);
            }
        }

        private async System.Threading.Tasks.Task ConfirmClearHistoryAsync()
        {
            var result = await ConfirmDialog.ShowAsync(
                title: GetString("AccountSync_ClearHistoryTitle", "清空历史"),
                message: GetString("AccountSync_ClearHistoryMessage",
                    "云端历史版本将全部删除，且不可恢复。"),
                confirmText: GetString("AccountSync_Clear", "清空"),
                cancelText: GetString("AccountSync_Cancel", "取消"),
                isDanger: true);
            if (result != null && result.Confirmed)
            {
                ViewModel.ClearHistoryAsync().Forget("AccountSyncPage.ClearHistory", AppLog.Logger);
            }
        }

        private async System.Threading.Tasks.Task ConfirmDeleteVaultAsync()
        {
            var result = await ConfirmDialog.ShowAsync(
                title: GetString("AccountSync_DeleteVaultTitle", "删除云端保险库"),
                message: GetString("AccountSync_DeleteVaultMessage",
                    "云端保险库和历史版本将被删除，本机配置仍保留。"),
                confirmText: GetString("AccountSync_Delete", "删除"),
                cancelText: GetString("AccountSync_Cancel", "取消"),
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
                    ? GetString("AccountSync_LogoutAllTitle", "退出所有设备")
                    : GetString("AccountSync_LogoutTitle", "退出登录"),
                message: all
                    ? GetString("AccountSync_LogoutAllMessage", "所有设备都将退出登录，需要重新登录才能同步。")
                    : GetString("AccountSync_LogoutMessage", "将退出当前设备。"),
                confirmText: all
                    ? GetString("AccountSync_LogoutAll", "退出所有设备")
                    : GetString("AccountSync_Logout", "退出登录"),
                cancelText: GetString("AccountSync_Cancel", "取消"),
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

        // ---------------- 图标映射 ----------------

        private string MapIconGlyph(string iconKey)
        {
            if (string.IsNullOrEmpty(iconKey))
            {
                return "\uE897";
            }
            switch (iconKey)
            {
                case "IconCloudOff": return "\uEC82";
                case "IconCloud": return "\uE753";
                case "IconLock": return "\uE72E";
                case "IconCircleCheck": return "\uE930";
                case "IconWarning": return "\uE7BA";
                case "IconSyncError": return "\uE783";
                default: return "\uE897";
            }
        }

        private Brush MapIconBrush(string iconKey)
        {
            string key;
            switch (iconKey)
            {
                case "IconCircleCheck": key = "AppSuccessBrush"; break;
                case "IconWarning": key = "AppWarningBrush"; break;
                case "IconSyncError": key = "AppDangerBrush"; break;
                default: key = "AppTextDimBrush"; break;
            }
            return ResolveBrush(key);
        }

        private StatusPillKind MapPillKind(string iconKey)
        {
            switch (iconKey)
            {
                case "IconCircleCheck": return StatusPillKind.Success;
                case "IconWarning": return StatusPillKind.Warning;
                case "IconSyncError": return StatusPillKind.Danger;
                default: return StatusPillKind.Neutral;
            }
        }

        private Brush ResolveBrush(string key)
        {
            try
            {
                object resource = Resources[key] ?? Application.Current.Resources[key];
                return resource as Brush;
            }
            catch (Exception)
            {
                return new SolidColorBrush(Colors.Gray);
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
