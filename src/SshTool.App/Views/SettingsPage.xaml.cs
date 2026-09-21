using System;
using System.Globalization;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    public sealed partial class SettingsPage : Page
    {
        // A03：默认外观一律显示名称；解析不到（未知 id / 列表读失败 / 首帧）时显示
        // 这句兜底文案，绝不把 builtin-xxx 这类内部 id 当文案上屏。
        private const string AppearanceFallbackResw = "Settings_AppearanceFallback";
        private const string ReswPt = "Settings_Unit_Pt";
        private const string ReswLines = "Settings_Unit_Lines";
        private const string ReswTimes = "Settings_Unit_Times";
        private const string ReswSeconds = "Settings_Unit_Seconds";
        private const string ReswNever = "Settings_Unit_Never";
        private const string ReswFontSizeDesc = "Settings_FontSize_Description";
        private const string ReswScrollbackDesc = "Settings_Scrollback_Description";
        private const string ReswReconnectDesc = "Settings_Reconnect_Description";
        private const string ReswTimeoutDesc = "Settings_Timeout_Description";
        private const string ReswAgentTimeoutDesc = "Settings_AgentTimeout_Description";

        private readonly ResourceLoader _loader = ResourceLoader.GetForCurrentView();

        // C-07：Slider 不在拖动中连续持久化。统一 debounce 定时器（150 ms）；
        // 指针拖动由 ManipulationCompleted 立刻提交；键盘/点击由定时器到期提交。
        private DispatcherTimer _debounceTimer;
        private Slider _pendingSlider;
        private bool _suppress;
        private bool _isDragging;
        private string _appearanceName = string.Empty;

        private enum SliderCommit
        {
            Font,
            Scrollback,
            Reconnect,
            Timeout,
            AgentTimeout
        }

        public SettingsPage()
        {
            ViewModel = new SettingsViewModel(AppServices.Current);
            this.InitializeComponent();
            _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _debounceTimer.Tick += OnDebounceTick;
            ProbeList.ItemsSource = ViewModel.Probes;
            BindAll();
        }

        public SettingsViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.RefreshDiagnostics();
            BindAll();
            RefreshAppearanceName();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            // 离开页前提交任何 pending 值，防丢失。
            CommitPendingSlider();
            base.OnNavigatedFrom(e);
        }

        private string Load(string key)
        {
            try { return _loader.GetString(key); }
            catch (Exception) { return string.Empty; }
        }

        // ---------- 通用 ----------

        private void BindAll()
        {
            _suppress = true;
            try
            {
                ThemeBox.Items.Clear();
                ThemeBox.Items.Add(Load("Settings_General_Theme_System"));
                ThemeBox.Items.Add(Load("Settings_General_Theme_Dark"));
                ThemeBox.Items.Add(Load("Settings_General_Theme_Light"));
                ThemeBox.SelectedIndex = SettingsViewModel.ThemeModeToIndex(ViewModel.ThemeMode);
                AccentSwitch.IsOn = ViewModel.UseSystemAccent;

                SortBox.Items.Clear();
                SortBox.Items.Add(Load("Settings_General_Sort_Name"));
                SortBox.Items.Add(Load("Settings_General_Sort_Recent"));
                SortBox.SelectedIndex = SettingsViewModel.SortModeToIndex(ViewModel.HostSortMode);
                QuickConnectSwitch.IsOn = ViewModel.ShowQuickConnect;
                HapticsSwitch.IsOn = ViewModel.HapticsEnabled;

                LanguageBox.Items.Clear();
                LanguageBox.Items.Add(Load("Settings_General_Language_System"));
                LanguageBox.Items.Add(Load("Settings_General_Language_Zh"));
                LanguageBox.Items.Add(Load("Settings_General_Language_En"));
                LanguageBox.SelectedIndex = SettingsViewModel.LanguageToIndex(ViewModel.Language);

                // 终端
                AppearanceSection.Header = Load("Settings_Terminal_Appearance_Header");
                AppearanceValue.Text = string.IsNullOrEmpty(_appearanceName)
                    ? Load(AppearanceFallbackResw) : _appearanceName;

                FontSection.Header = Load("Settings_Terminal_FontSize_Header");
                FontSlider.Value = ViewModel.TerminalFontSize;
                UpdateFontSizeDisplay(ViewModel.TerminalFontSize);

                ScrollbackSection.Header = Load("Settings_Terminal_Scrollback_Header");
                ScrollbackSlider.Value = ViewModel.ScrollbackLines;
                UpdateScrollbackDisplay(ViewModel.ScrollbackLines);

                AltScrollBox.Items.Clear();
                AltScrollBox.Items.Add(Load("Settings_Terminal_AltScroll_Arrows"));
                AltScrollBox.Items.Add(Load("Settings_Terminal_AltScroll_Wheel"));
                AltScrollBox.SelectedIndex = SettingsViewModel.AltScrollToIndex(ViewModel.AltScreenScroll);
                PasteConfirmSwitch.IsOn = ViewModel.PasteConfirmMultiline;

                // 键盘
                KeyBarVisibleSwitch.IsOn = ViewModel.KeyBarVisible;
                KeyBarLayoutSection.Header = Load("Settings_Keyboard_KeyBarLayout_Header");
                KeyBarSummary.Text = FormatKeyBarSummary();
                ShortcutsSection.Header = Load("Settings_Keyboard_Shortcuts_Header");
                ShortcutSummary.Text = FormatShortcutSummary();

                // 连接
                KeepScreenBox.Items.Clear();
                KeepScreenBox.Items.Add(Load("Settings_Connection_KeepScreen_Never"));
                KeepScreenBox.Items.Add(Load("Settings_Connection_KeepScreen_Session"));
                KeepScreenBox.Items.Add(Load("Settings_Connection_KeepScreen_Always"));
                KeepScreenBox.SelectedIndex = SettingsViewModel.KeepScreenOnToIndex(ViewModel.KeepScreenOn);
                KeepAliveSwitch.IsOn = ViewModel.KeepAliveInBackground;

                BgDiscBox.Items.Clear();
                BgDiscBox.Items.Add(Load("Settings_Connection_BgDisc_Off"));
                BgDiscBox.Items.Add(Load("Settings_Connection_BgDisc_5"));
                BgDiscBox.Items.Add(Load("Settings_Connection_BgDisc_15"));
                BgDiscBox.Items.Add(Load("Settings_Connection_BgDisc_30"));
                BgDiscBox.Items.Add(Load("Settings_Connection_BgDisc_60"));
                BgDiscBox.SelectedIndex =
                    SettingsViewModel.BackgroundDisconnectToIndex(ViewModel.BackgroundDisconnectMinutes);

                ReconnectSection.Header = Load("Settings_Connection_Reconnect_Header");
                ReconnectSlider.Value = ViewModel.ReconnectMaxAttempts;
                UpdateReconnectDisplay(ViewModel.ReconnectMaxAttempts);

                TimeoutSection.Header = Load("Settings_Connection_Timeout_Header");
                TimeoutSlider.Value = ViewModel.ConnectTimeoutSeconds;
                UpdateTimeoutDisplay(ViewModel.ConnectTimeoutSeconds);

                AgentTimeoutSection.Header = Load("Settings_Connection_AgentTimeout_Header");
                AgentTimeoutSlider.Value = ViewModel.AgentKeyTimeoutMinutes;
                UpdateAgentTimeoutDisplay(ViewModel.AgentKeyTimeoutMinutes);

                PollBox.Items.Clear();
                PollBox.Items.Add(Load("Settings_Connection_Poll_Off"));
                PollBox.Items.Add(Load("Settings_Connection_Poll_1"));
                PollBox.Items.Add(Load("Settings_Connection_Poll_5"));
                PollBox.Items.Add(Load("Settings_Connection_Poll_15"));
                PollBox.SelectedIndex =
                    SettingsViewModel.SyncPollToIndex(ViewModel.SyncPollForegroundSeconds);

                // 关于
                VersionSection.Header = Load("Settings_About_Version_Header");
                AppVersionValue.Text = ViewModel.AppVersionText;

                LogLevelBox.Items.Clear();
                LogLevelBox.Items.Add(Load("Settings_About_LogLevel_Debug"));
                LogLevelBox.Items.Add(Load("Settings_About_LogLevel_Info"));
                LogLevelBox.Items.Add(Load("Settings_About_LogLevel_Warn"));
                LogLevelBox.Items.Add(Load("Settings_About_LogLevel_Error"));
                LogLevelBox.SelectedIndex = SettingsViewModel.LogLevelToIndex(ViewModel.LogLevel);

                DiagnosticsSection.Header = Load("Settings_About_Diagnostics_Header");
                BindDiagnostics();
            }
            finally
            {
                _suppress = false;
            }
        }

        private void BindDiagnostics()
        {
            OsVersionValue.Text = string.Format(
                Load("Settings_Diagnostics_OsVersion"), ViewModel.OsVersionText);
            DeviceFamilyValue.Text = string.Format(
                Load("Settings_Diagnostics_DeviceFamily"), ViewModel.DeviceFamilyText);
            MemoryValue.Text = string.Format(
                Load("Settings_Diagnostics_Memory"), ViewModel.MemoryText);
        }

        // ---------- 显示更新（不写 VM，仅刷新 UI 文本）----------

        private void UpdateFontSizeDisplay(int size)
        {
            FontValue.Text = string.Format(CultureInfo.InvariantCulture, Load(ReswPt), size);
            FontSection.Description = string.Format(
                CultureInfo.InvariantCulture, Load(ReswFontSizeDesc),
                string.Format(CultureInfo.InvariantCulture, Load(ReswPt), size));
        }

        private void UpdateScrollbackDisplay(int lines)
        {
            ScrollbackValue.Text = string.Format(CultureInfo.InvariantCulture, Load(ReswLines), lines);
            ScrollbackSection.Description = string.Format(
                CultureInfo.InvariantCulture, Load(ReswScrollbackDesc),
                string.Format(CultureInfo.InvariantCulture, Load(ReswLines), lines));
        }

        private void UpdateReconnectDisplay(int attempts)
        {
            ReconnectValue.Text = string.Format(CultureInfo.InvariantCulture, Load(ReswTimes), attempts);
            ReconnectSection.Description = string.Format(
                CultureInfo.InvariantCulture, Load(ReswReconnectDesc),
                string.Format(CultureInfo.InvariantCulture, Load(ReswTimes), attempts));
        }

        private void UpdateTimeoutDisplay(int seconds)
        {
            TimeoutValue.Text = string.Format(CultureInfo.InvariantCulture, Load(ReswSeconds), seconds);
            TimeoutSection.Description = string.Format(
                CultureInfo.InvariantCulture, Load(ReswTimeoutDesc),
                string.Format(CultureInfo.InvariantCulture, Load(ReswSeconds), seconds));
        }

        private void UpdateAgentTimeoutDisplay(int minutes)
        {
            string text = minutes <= 0
                ? Load(ReswNever)
                : string.Format(CultureInfo.InvariantCulture, Load("Settings_Unit_Minutes"), minutes);
            AgentTimeoutValue.Text = text;
            AgentTimeoutSection.Description = string.Format(
                CultureInfo.InvariantCulture, Load(ReswAgentTimeoutDesc), text);
        }

        private string FormatKeyBarSummary()
        {
            int count = SshTool.Core.Terminal.KeyBarLayout.Parse(ViewModel.KeyBarLayout).Count;
            return string.Format(CultureInfo.InvariantCulture, "{0} {1}", count, Load("Settings_Unit_Keys"));
        }

        private string FormatShortcutSummary()
        {
            var map = ViewModel.CurrentShortcutMap;
            if (map.Conflicts.Count > 0)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0} {1}", map.Conflicts.Count, Load("Settings_Unit_Conflicts"));
            }
            return string.Format(CultureInfo.InvariantCulture, "{0} {1}", map.Bindings.Count, Load("Settings_Unit_Actions"));
        }

        // ---------- C-07：Slider 写入时机控制 ----------

        private void ScheduleSliderCommit(Slider slider)
        {
            if (_isDragging) { return; }
            _pendingSlider = slider;
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void OnDebounceTick(object sender, object e)
        {
            _debounceTimer.Stop();
            CommitPendingSlider();
        }

        private void CommitPendingSlider()
        {
            _debounceTimer.Stop();
            if (_pendingSlider == null) { return; }
            Slider slider = _pendingSlider;
            _pendingSlider = null;
            try
            {
                if (slider == FontSlider)
                {
                    ViewModel.TerminalFontSize = (int)Math.Round(FontSlider.Value);
                }
                else if (slider == ScrollbackSlider)
                {
                    ViewModel.ScrollbackLines = (int)Math.Round(ScrollbackSlider.Value);
                }
                else if (slider == ReconnectSlider)
                {
                    ViewModel.ReconnectMaxAttempts = (int)Math.Round(ReconnectSlider.Value);
                }
                else if (slider == TimeoutSlider)
                {
                    ViewModel.ConnectTimeoutSeconds = (int)Math.Round(TimeoutSlider.Value);
                }
                else if (slider == AgentTimeoutSlider)
                {
                    ViewModel.AgentKeyTimeoutMinutes = (int)Math.Round(AgentTimeoutSlider.Value);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Settings", "commit-slider", ex);
            }
        }

        private void OnSliderManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
        {
            _isDragging = true;
        }

        private void CompleteManipulation(Slider slider)
        {
            _isDragging = false;
            CommitPendingSlider();
        }

        // ---------- Slider 事件（仅更新显示，不直接写 VM）----------

        private void OnFontChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) { return; }
            int size = (int)Math.Round(e.NewValue);
            UpdateFontSizeDisplay(size);
            ScheduleSliderCommit(FontSlider);
        }

        private void OnFontManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            CompleteManipulation(FontSlider);
        }

        private void OnScrollbackChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) { return; }
            int lines = (int)Math.Round(e.NewValue);
            UpdateScrollbackDisplay(lines);
            ScheduleSliderCommit(ScrollbackSlider);
        }

        private void OnScrollbackManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            CompleteManipulation(ScrollbackSlider);
        }

        private void OnReconnectChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) { return; }
            int attempts = (int)Math.Round(e.NewValue);
            UpdateReconnectDisplay(attempts);
            ScheduleSliderCommit(ReconnectSlider);
        }

        private void OnReconnectManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            CompleteManipulation(ReconnectSlider);
        }

        private void OnTimeoutChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) { return; }
            int seconds = (int)Math.Round(e.NewValue);
            UpdateTimeoutDisplay(seconds);
            ScheduleSliderCommit(TimeoutSlider);
        }

        private void OnTimeoutManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            CompleteManipulation(TimeoutSlider);
        }

        private void OnAgentTimeoutChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) { return; }
            int minutes = (int)Math.Round(e.NewValue);
            UpdateAgentTimeoutDisplay(minutes);
            ScheduleSliderCommit(AgentTimeoutSlider);
        }

        private void OnAgentTimeoutManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            CompleteManipulation(AgentTimeoutSlider);
        }

        // ---------- 恢复默认 ----------

        private void OnResetFontClick(object sender, RoutedEventArgs e)
        {
            CommitPendingSlider();
            ViewModel.TerminalFontSize = SettingsViewModel.DefaultFontSize;
            FontSlider.Value = ViewModel.TerminalFontSize;
            UpdateFontSizeDisplay(ViewModel.TerminalFontSize);
        }

        private void OnResetScrollbackClick(object sender, RoutedEventArgs e)
        {
            CommitPendingSlider();
            ViewModel.ScrollbackLines = SettingsViewModel.DefaultScrollback;
            ScrollbackSlider.Value = ViewModel.ScrollbackLines;
            UpdateScrollbackDisplay(ViewModel.ScrollbackLines);
        }

        private void OnResetReconnectClick(object sender, RoutedEventArgs e)
        {
            CommitPendingSlider();
            ViewModel.ReconnectMaxAttempts = SettingsViewModel.DefaultReconnectAttempts;
            ReconnectSlider.Value = ViewModel.ReconnectMaxAttempts;
            UpdateReconnectDisplay(ViewModel.ReconnectMaxAttempts);
        }

        private void OnResetTimeoutClick(object sender, RoutedEventArgs e)
        {
            CommitPendingSlider();
            ViewModel.ConnectTimeoutSeconds = SettingsViewModel.DefaultConnectTimeout;
            TimeoutSlider.Value = ViewModel.ConnectTimeoutSeconds;
            UpdateTimeoutDisplay(ViewModel.ConnectTimeoutSeconds);
        }

        private void OnResetAgentTimeoutClick(object sender, RoutedEventArgs e)
        {
            CommitPendingSlider();
            ViewModel.AgentKeyTimeoutMinutes = SettingsViewModel.DefaultAgentKeyTimeout;
            AgentTimeoutSlider.Value = ViewModel.AgentKeyTimeoutMinutes;
            UpdateAgentTimeoutDisplay(ViewModel.AgentKeyTimeoutMinutes);
        }

        // ---------- 其他控件事件 ----------

        private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || ThemeBox.SelectedIndex < 0) { return; }
            ViewModel.ThemeMode = SettingsViewModel.IndexToThemeMode(ThemeBox.SelectedIndex);
            Toast.Show(Load("Settings_Toast_ThemeChanged"));
        }

        private void OnAccentToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress) { return; }
            ViewModel.UseSystemAccent = AccentSwitch.IsOn;
            Toast.Show(Load("Settings_Toast_AccentChanged"));
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
            Toast.Show(Load("Settings_Toast_LanguageChanged"));
        }

        private void OnAppearanceClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(AppearanceListPage));
        }

        // A03：默认外观显示名称（解析失败兜底「系统默认」，不显示内部 id）；
        // 从外观列表返回时刷新。
        private async void RefreshAppearanceName()
        {
            string id = ViewModel.DefaultAppearanceId;
            string resolved = Load(AppearanceFallbackResw);
            if (!string.IsNullOrEmpty(id))
            {
                try
                {
                    System.Collections.Generic.IReadOnlyList<SshTool.Core.Models.AppearanceProfile> all =
                        await AppServices.Current.AppearanceService.ListAsync().ConfigureAwait(true);
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (string.Equals(all[i].Id, id, System.StringComparison.Ordinal)
                            && !string.IsNullOrEmpty(all[i].Name))
                        {
                            resolved = all[i].Name;
                            break;
                        }
                    }
                }
                catch (Exception)
                {
                    resolved = Load(AppearanceFallbackResw);
                }
            }
            _appearanceName = resolved;
            if (AppearanceValue != null)
            {
                AppearanceValue.Text = resolved;
            }
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
            Toast.Show(Load("Settings_Toast_KeepScreenChanged"));
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

        private void OnPollChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || PollBox.SelectedIndex < 0) { return; }
            ViewModel.SyncPollForegroundSeconds = SettingsViewModel.IndexToSyncPoll(PollBox.SelectedIndex);
        }

        private void OnLicensesClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(PlaceholderPage), new PlaceholderArgs(
                Load("Settings_About_Licenses"), "M8"));
        }

        private void OnLogLevelChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || LogLevelBox.SelectedIndex < 0) { return; }
            ViewModel.LogLevel = SettingsViewModel.IndexToLogLevel(LogLevelBox.SelectedIndex);
            Toast.Show(Load("Settings_Toast_LogLevelChanged"));
        }

        private async void OnExportLogsClick(object sender, RoutedEventArgs e)
        {
            string result = await ViewModel.ExportLogsAsync();
            Toast.Show(result);
        }

        private async void OnClearLogsClick(object sender, RoutedEventArgs e)
        {
            var confirm = await Dialogs.ConfirmDialog.ShowAsync(
                Load("Settings_About_ClearLogs"),
                Load("Settings_Confirm_ClearLogs"),
                Load("Settings_About_ClearLogs"),
                Load("Dialog_Cancel"), true);
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
            Toast.Show(Load("Settings_Toast_DiagnosticsRefreshed"));
        }

        private void OnCopyDiagClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                package.SetText(ViewModel.BuildDiagnosticsText());
                Clipboard.SetContent(package);
                Toast.Show(Load("Settings_Toast_DiagnosticsCopied"));
            }
            catch (Exception)
            {
                Toast.Show(Load("Settings_Toast_CopyFailed"));
            }
        }
    }
}
