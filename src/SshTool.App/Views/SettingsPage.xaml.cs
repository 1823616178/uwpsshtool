using System;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    public sealed partial class SettingsPage : Page
    {
        private bool _suppress;

        public SettingsPage()
        {
            ViewModel = new SettingsViewModel(AppServices.Current);
            this.InitializeComponent();
            ProbeList.ItemsSource = ViewModel.Probes;
            BindAll();
        }

        public SettingsViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.RefreshDiagnostics();
            BindAll();
        }

        private void BindAll()
        {
            _suppress = true;
            try
            {
                ThemeBox.SelectedIndex = SettingsViewModel.ThemeModeToIndex(ViewModel.ThemeMode);
                AccentSwitch.IsOn = ViewModel.UseSystemAccent;
                SortBox.SelectedIndex = SettingsViewModel.SortModeToIndex(ViewModel.HostSortMode);
                QuickConnectSwitch.IsOn = ViewModel.ShowQuickConnect;
                HapticsSwitch.IsOn = ViewModel.HapticsEnabled;
                LanguageBox.SelectedIndex = SettingsViewModel.LanguageToIndex(ViewModel.Language);

                AppearanceValue.Text = ViewModel.DefaultAppearanceId;
                FontSlider.Value = ViewModel.TerminalFontSize;
                FontValue.Text = ViewModel.TerminalFontSize.ToString() + " pt";
                ScrollbackSlider.Value = ViewModel.ScrollbackLines;
                ScrollbackValue.Text = ViewModel.ScrollbackLines.ToString() + " 行";
                AltScrollBox.SelectedIndex = SettingsViewModel.AltScrollToIndex(ViewModel.AltScreenScroll);
                PasteConfirmSwitch.IsOn = ViewModel.PasteConfirmMultiline;

                KeyBarVisibleSwitch.IsOn = ViewModel.KeyBarVisible;
                KeyBarSummary.Text = ViewModel.KeyBarSummary;
                ShortcutSummary.Text = ViewModel.ShortcutSummary;

                KeepScreenBox.SelectedIndex = SettingsViewModel.KeepScreenOnToIndex(ViewModel.KeepScreenOn);
                KeepAliveSwitch.IsOn = ViewModel.KeepAliveInBackground;
                BgDiscBox.SelectedIndex =
                    SettingsViewModel.BackgroundDisconnectToIndex(ViewModel.BackgroundDisconnectMinutes);
                ReconnectSlider.Value = ViewModel.ReconnectMaxAttempts;
                ReconnectValue.Text = ViewModel.ReconnectMaxAttempts.ToString() + " 次";
                TimeoutSlider.Value = ViewModel.ConnectTimeoutSeconds;
                TimeoutValue.Text = ViewModel.ConnectTimeoutSeconds.ToString() + " 秒";
                PollBox.SelectedIndex =
                    SettingsViewModel.SyncPollToIndex(ViewModel.SyncPollForegroundSeconds);

                AppVersionValue.Text = ViewModel.AppVersionText;
                LogLevelBox.SelectedIndex = SettingsViewModel.LogLevelToIndex(ViewModel.LogLevel);
                BindDiagnostics();
            }
            finally
            {
                _suppress = false;
            }
        }

        private void BindDiagnostics()
        {
            OsVersionValue.Text = "系统版本：" + ViewModel.OsVersionText;
            DeviceFamilyValue.Text = "设备系列：" + ViewModel.DeviceFamilyText;
            MemoryValue.Text = "内存：" + ViewModel.MemoryText;
        }

        private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || ThemeBox.SelectedIndex < 0) { return; }
            ViewModel.ThemeMode = SettingsViewModel.IndexToThemeMode(ThemeBox.SelectedIndex);
            Toast.Show("已切换主题");
        }

        private void OnAccentToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress) { return; }
            ViewModel.UseSystemAccent = AccentSwitch.IsOn;
        }

        private void OnSortChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || SortBox.SelectedIndex < 0) { return; }
            ViewModel.HostSortMode = SettingsViewModel.IndexToSortMode(SortBox.SelectedIndex);
        }

        private void OnQuickConnectToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress) { return; }
            ViewModel.ShowQuickConnect = QuickConnectSwitch.IsOn;
        }

        private void OnHapticsToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress) { return; }
            ViewModel.HapticsEnabled = HapticsSwitch.IsOn;
        }

        private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || LanguageBox.SelectedIndex < 0) { return; }
            ViewModel.Language = SettingsViewModel.IndexToLanguage(LanguageBox.SelectedIndex);
            Toast.Show("重启应用后生效");
        }

        private void OnAppearanceClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(PlaceholderPage), new PlaceholderArgs("外观", "M6"));
        }

        private void OnFontChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) { return; }
            int size = (int)Math.Round(e.NewValue);
            ViewModel.TerminalFontSize = size;
            FontValue.Text = ViewModel.TerminalFontSize.ToString() + " pt";
        }

        private void OnScrollbackChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) { return; }
            int lines = (int)Math.Round(e.NewValue);
            ViewModel.ScrollbackLines = lines;
            ScrollbackValue.Text = ViewModel.ScrollbackLines.ToString() + " 行";
        }

        private void OnAltScrollChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || AltScrollBox.SelectedIndex < 0) { return; }
            ViewModel.AltScreenScroll = SettingsViewModel.IndexToAltScroll(AltScrollBox.SelectedIndex);
        }

        private void OnPasteConfirmToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress) { return; }
            ViewModel.PasteConfirmMultiline = PasteConfirmSwitch.IsOn;
        }

        private void OnKeyBarVisibleToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress) { return; }
            ViewModel.KeyBarVisible = KeyBarVisibleSwitch.IsOn;
        }

        private void OnEditKeyBarClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(KeyBarLayoutEditorPage));
        }

        private void OnEditShortcutsClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(ShortcutEditorPage));
        }

        private void OnKeepScreenChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || KeepScreenBox.SelectedIndex < 0) { return; }
            ViewModel.KeepScreenOn = SettingsViewModel.IndexToKeepScreenOn(KeepScreenBox.SelectedIndex);
            Toast.Show("常亮设置已生效");
        }

        private void OnKeepAliveToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress) { return; }
            ViewModel.KeepAliveInBackground = KeepAliveSwitch.IsOn;
        }

        private void OnBgDiscChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || BgDiscBox.SelectedIndex < 0) { return; }
            ViewModel.BackgroundDisconnectMinutes =
                SettingsViewModel.IndexToBackgroundDisconnect(BgDiscBox.SelectedIndex);
        }

        private void OnReconnectChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) { return; }
            int attempts = (int)Math.Round(e.NewValue);
            ViewModel.ReconnectMaxAttempts = attempts;
            ReconnectValue.Text = ViewModel.ReconnectMaxAttempts.ToString() + " 次";
        }

        private void OnTimeoutChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) { return; }
            int seconds = (int)Math.Round(e.NewValue);
            ViewModel.ConnectTimeoutSeconds = seconds;
            TimeoutValue.Text = ViewModel.ConnectTimeoutSeconds.ToString() + " 秒";
        }

        private void OnPollChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || PollBox.SelectedIndex < 0) { return; }
            ViewModel.SyncPollForegroundSeconds = SettingsViewModel.IndexToSyncPoll(PollBox.SelectedIndex);
        }

        private void OnLicensesClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(PlaceholderPage), new PlaceholderArgs("开源许可", "M8"));
        }

        private void OnLogLevelChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || LogLevelBox.SelectedIndex < 0) { return; }
            ViewModel.LogLevel = SettingsViewModel.IndexToLogLevel(LogLevelBox.SelectedIndex);
            Toast.Show("日志级别已生效");
        }

        private async void OnExportLogsClick(object sender, RoutedEventArgs e)
        {
            string result = await ViewModel.ExportLogsAsync();
            Toast.Show(result);
        }

        private async void OnClearLogsClick(object sender, RoutedEventArgs e)
        {
            var confirm = await Dialogs.ConfirmDialog.ShowAsync(
                "清空日志", "删除本机全部日志文件？", "清空", "取消", true);
            if (!confirm.Confirmed)
            {
                return;
            }
            string result = await ViewModel.ClearLogsAsync();
            Toast.Show(result);
        }

        private void OnRefreshDiagClick(object sender, RoutedEventArgs e)
        {
            ViewModel.RefreshDiagnostics();
            AppVersionValue.Text = ViewModel.AppVersionText;
            BindDiagnostics();
            Toast.Show("诊断信息已刷新");
        }

        private void OnCopyDiagClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                package.SetText(ViewModel.BuildDiagnosticsText());
                Clipboard.SetContent(package);
                Toast.Show("诊断信息已复制");
            }
            catch (Exception)
            {
                Toast.Show("复制失败");
            }
        }
    }
}
