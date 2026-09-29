using System;
using System.Globalization;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using SshTool.App.Views.Settings;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    public sealed partial class SettingsPage : Page
    {
        private readonly ResourceLoader _loader = ResourceLoader.GetForCurrentView();

        public SettingsPage()
        {
            ViewModel = new SettingsViewModel(AppServices.Current);
            this.InitializeComponent();
        }

        public SettingsViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            UpdateSummaries();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ViewModel.Detach();
            base.OnNavigatedFrom(e);
        }

        private string Load(string key)
        {
            try { return _loader.GetString(key); }
            catch (Exception) { return string.Empty; }
        }

        private void UpdateSummaries()
        {
            // 通用：主题 · 语言
            string themeText = ViewModel.ThemeMode == "dark" ? Load("Settings_General_Theme_Dark")
                : ViewModel.ThemeMode == "light" ? Load("Settings_General_Theme_Light")
                : Load("Settings_General_Theme_System");
            string langText = ViewModel.Language == "zh-CN" ? Load("Settings_General_Language_Zh")
                : ViewModel.Language == "en-US" ? Load("Settings_General_Language_En")
                : Load("Settings_General_Language_System");
            GeneralRow.Subtitle = themeText + " · " + langText;

            // 终端：字号 · 回滚行数
            TerminalRow.Subtitle = string.Format(CultureInfo.InvariantCulture, "{0} · {1}",
                string.Format(CultureInfo.InvariantCulture, Load("Settings_Unit_Pt"), ViewModel.TerminalFontSize),
                string.Format(CultureInfo.InvariantCulture, Load("Settings_Unit_Lines"), ViewModel.ScrollbackLines));

            // 键盘：按键栏开关 · 快捷键
            string keyBarText = ViewModel.KeyBarVisible
                ? Load("Settings_Keyboard_KeyBar_On")
                : Load("Settings_Keyboard_KeyBar_Off");
            KeyboardRow.Subtitle = keyBarText + " · " + ViewModel.ShortcutSummary;

            // 连接与安全：常亮 · 重试次数
            string screenText = ViewModel.KeepScreenOn == "always" ? Load("Settings_Connection_KeepScreen_Always")
                : ViewModel.KeepScreenOn == "never" ? Load("Settings_Connection_KeepScreen_Never")
                : Load("Settings_Connection_KeepScreen_Session");
            string reconnectText = string.Format(CultureInfo.InvariantCulture, Load("Settings_Unit_Times"), ViewModel.ReconnectMaxAttempts);
            ConnectionRow.Subtitle = screenText + " · " + reconnectText;

            // 同步：前台轮询
            string pollText = ViewModel.SyncPollForegroundSeconds <= 0
                ? Load("Settings_Connection_Poll_Off")
                : string.Format(CultureInfo.InvariantCulture, Load("Settings_Unit_Minutes"), ViewModel.SyncPollForegroundSeconds / 60);
            SyncRow.Subtitle = Load("Settings_Connection_Poll_Header") + "：" + pollText;

            // 关于：版本 · 日志级别
            string levelText = ViewModel.LogLevel == "debug" ? Load("Settings_About_LogLevel_Debug")
                : ViewModel.LogLevel == "warn" ? Load("Settings_About_LogLevel_Warn")
                : ViewModel.LogLevel == "error" ? Load("Settings_About_LogLevel_Error")
                : Load("Settings_About_LogLevel_Info");
            string ver = ViewModel.AppVersionText;
            int spaceIdx = ver.IndexOf(' ');
            if (spaceIdx > 0)
            {
                ver = ver.Substring(0, spaceIdx);
            }
            AboutRow.Subtitle = "v" + ver + " · " + levelText;
        }

        private void OnGeneralClick(object sender, EventArgs e)
        {
            Frame.Navigate(typeof(SettingsGeneralPage));
        }

        private void OnTerminalClick(object sender, EventArgs e)
        {
            Frame.Navigate(typeof(SettingsTerminalPage));
        }

        private void OnKeyboardClick(object sender, EventArgs e)
        {
            Frame.Navigate(typeof(SettingsKeyboardPage));
        }

        private void OnConnectionClick(object sender, EventArgs e)
        {
            Frame.Navigate(typeof(SettingsConnectionPage));
        }

        private void OnSyncClick(object sender, EventArgs e)
        {
            Frame.Navigate(typeof(SettingsSyncPage));
        }

        private void OnAboutClick(object sender, EventArgs e)
        {
            Frame.Navigate(typeof(SettingsAboutPage));
        }

        private void OnHeaderBackRequested(object sender, EventArgs e)
        {
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.RequestBack();
            }
            else if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }
    }
}
