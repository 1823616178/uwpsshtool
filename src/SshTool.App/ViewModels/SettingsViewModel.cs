using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.Core.Common;
using SshTool.Core.Storage;
using SshTool.Core.Terminal;
using Windows.ApplicationModel;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Provider;

namespace SshTool.App.ViewModels
{
    // U14 诊断探针行（名称 + 可用/不可用）。
    public sealed class DiagnosticProbeRow
    {
        public string Name { get; set; }
        public string Status { get; set; }
    }

    // U14 设置页 ViewModel：SettingsRepository 的直接映射 + 立即生效。
    //
    // 分工：每个 setter 写仓库（非法值先钳制，绝不抛给 UI）并触发副作用；
    // LifecycleService 已订阅仓库 Changed（keepAlive/keepScreenOn 即时纠正），
    // 此处只处理必须在设置页上下文中立即看到的三项：主题、强调色、日志级别。
    // ComboBox 索引映射集中在此（XAML 不做转换器，页码后置代码只调这组静态方法）。
    public sealed class SettingsViewModel : ViewModelBase
    {
        // V04a：各 Slider 的「恢复默认」值。默认值与取值范围只在 SettingDefinitions
        // 定义一次，这里和设置页都从那里取。
        public static int DefaultFontSize { get { return DefaultInt("terminalFontSize"); } }
        public static int DefaultScrollback { get { return DefaultInt("scrollbackLines"); } }
        public static int DefaultReconnectAttempts { get { return DefaultInt("reconnectMaxAttempts"); } }
        public static int DefaultConnectTimeout { get { return DefaultInt("connectTimeoutSeconds"); } }
        public static int DefaultAgentKeyTimeout { get { return DefaultInt("agentKeyTimeoutMinutes"); } }

        private static readonly int[] BackgroundDisconnectOptions = { 0, 5, 15, 30, 60 };
        private static readonly int[] SyncPollOptions = { 0, 60, 300, 900 };

        private readonly SettingsRepository _settings;
        private bool _detached;

        private string _osVersion = string.Empty;
        private string _deviceFamily = string.Empty;
        private string _memoryText = string.Empty;
        private string _appVersion = string.Empty;

        public SettingsViewModel(AppServices services)
            : this(services != null ? services.Settings : null)
        {
        }

        public SettingsViewModel(SettingsRepository settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }
            _settings = settings;
            Probes = new ObservableCollection<DiagnosticProbeRow>();
            // O03：SettingsRepository 是应用级单例，本 VM 随 SettingsPage 重建。
            _settings.Changed += OnSettingsChanged;
            RefreshDiagnostics();
        }

        // O03：页面 OnNavigatedFrom 调用。幂等。
        public void Detach()
        {
            if (_detached)
            {
                return;
            }
            _detached = true;
            _settings.Changed -= OnSettingsChanged;
        }

        public ObservableCollection<DiagnosticProbeRow> Probes { get; private set; }

        // ---------- 通用 ----------

        public string ThemeMode
        {
            get { return _settings.GetString("themeMode"); }
            set
            {
                string normalized = SettingDefinitions.Require("themeMode").NormalizeString(value);
                _settings.Set("themeMode", normalized);
                ApplyThemeNow();
                RaisePropertyChanged(nameof(ThemeMode));
            }
        }

        public bool UseSystemAccent
        {
            get { return _settings.GetBool("useSystemAccent"); }
            set
            {
                _settings.Set("useSystemAccent", value);
                ApplyAccentNow(value);
                RaisePropertyChanged(nameof(UseSystemAccent));
            }
        }

        public string HostSortMode
        {
            get { return _settings.GetString("hostSortMode"); }
            set
            {
                string normalized = SettingDefinitions.Require("hostSortMode").NormalizeString(value);
                _settings.Set("hostSortMode", normalized);
                RaisePropertyChanged(nameof(HostSortMode));
            }
        }

        public bool ShowQuickConnect
        {
            get { return _settings.GetBool("showQuickConnect"); }
            set
            {
                _settings.Set("showQuickConnect", value);
                RaisePropertyChanged(nameof(ShowQuickConnect));
            }
        }

        public bool HapticsEnabled
        {
            get { return _settings.GetBool("hapticsEnabled"); }
            set
            {
                _settings.Set("hapticsEnabled", value);
                RaisePropertyChanged(nameof(HapticsEnabled));
            }
        }

        public string Language
        {
            get { return _settings.GetString("language"); }
            set
            {
                string normalized = SettingDefinitions.Require("language").NormalizeString(value);
                _settings.Set("language", normalized);
                RaisePropertyChanged(nameof(Language));
            }
        }

        // ---------- 终端 ----------

        public string DefaultAppearanceId
        {
            get { return _settings.GetString("defaultAppearanceId"); }
            set
            {
                _settings.Set("defaultAppearanceId", value ?? string.Empty);
                RaisePropertyChanged(nameof(DefaultAppearanceId));
            }
        }

        public int TerminalFontSize
        {
            get { return _settings.GetInt("terminalFontSize"); }
            set
            {
                int clamped = SettingDefinitions.Require("terminalFontSize").Clamp(value);
                _settings.Set("terminalFontSize", clamped);
                RaisePropertyChanged(nameof(TerminalFontSize));
            }
        }

        public int ScrollbackLines
        {
            get { return _settings.GetInt("scrollbackLines"); }
            set
            {
                int clamped = SettingDefinitions.Require("scrollbackLines").Clamp(value);
                _settings.Set("scrollbackLines", clamped);
                RaisePropertyChanged(nameof(ScrollbackLines));
            }
        }

        public string AltScreenScroll
        {
            get { return _settings.GetString("altScreenScroll"); }
            set
            {
                string normalized = SettingDefinitions.Require("altScreenScroll").NormalizeString(value);
                _settings.Set("altScreenScroll", normalized);
                RaisePropertyChanged(nameof(AltScreenScroll));
            }
        }

        public bool PasteConfirmMultiline
        {
            get { return _settings.GetBool("pasteConfirmMultiline"); }
            set
            {
                _settings.Set("pasteConfirmMultiline", value);
                RaisePropertyChanged(nameof(PasteConfirmMultiline));
            }
        }

        // ---------- 键盘 ----------

        public bool KeyBarVisible
        {
            get { return _settings.GetBool("keyBarVisible"); }
            set
            {
                _settings.Set("keyBarVisible", value);
                RaisePropertyChanged(nameof(KeyBarVisible));
            }
        }

        public string KeyBarLayout
        {
            get { return _settings.GetString("keyBarLayout"); }
            set
            {
                string layout = string.IsNullOrWhiteSpace(value)
                    ? SshTool.Core.Terminal.KeyBarLayout.DefaultString : value;
                _settings.Set("keyBarLayout", layout);
                RaisePropertyChanged(nameof(KeyBarLayout));
                RaisePropertyChanged(nameof(KeyBarSummary));
            }
        }

        public string KeyBarSummary
        {
            get
            {
                int count = SshTool.Core.Terminal.KeyBarLayout.Parse(KeyBarLayout).Count;
                return Localized.Format("Settings_KeyBarSummary", "{0} 个键",
                    count.ToString(CultureInfo.InvariantCulture));
            }
        }

        public string ShortcutsJson
        {
            get { return _settings.GetString("shortcuts"); }
            set
            {
                _settings.Set("shortcuts", string.IsNullOrEmpty(value) ? "{}" : value);
                RaisePropertyChanged(nameof(ShortcutsJson));
                RaisePropertyChanged(nameof(ShortcutSummary));
            }
        }

        public ShortcutMap CurrentShortcutMap
        {
            get { return ShortcutMap.Parse(ShortcutsJson); }
        }

        public string ShortcutSummary
        {
            get
            {
                ShortcutMap map = CurrentShortcutMap;
                if (map.Conflicts.Count > 0)
                {
                    return Localized.Format("Settings_ShortcutConflicts", "{0} 处冲突",
                        map.Conflicts.Count.ToString(CultureInfo.InvariantCulture));
                }
                return Localized.Format("Settings_ShortcutActions", "{0} 个动作",
                    map.Bindings.Count.ToString(CultureInfo.InvariantCulture));
            }
        }

        public void ResetKeyBarLayout()
        {
            KeyBarLayout = SshTool.Core.Terminal.KeyBarLayout.DefaultString;
        }

        public void ResetShortcuts()
        {
            ShortcutsJson = "{}";
        }

        // ---------- 连接 ----------

        public string KeepScreenOn
        {
            get { return _settings.GetString("keepScreenOn"); }
            set
            {
                string normalized = SettingDefinitions.Require("keepScreenOn").NormalizeString(value);
                _settings.Set("keepScreenOn", normalized);
                RefreshKeepAwake();
                RaisePropertyChanged(nameof(KeepScreenOn));
            }
        }

        public bool KeepAliveInBackground
        {
            get { return _settings.GetBool("keepAliveInBackground"); }
            set
            {
                _settings.Set("keepAliveInBackground", value);
                RaisePropertyChanged(nameof(KeepAliveInBackground));
            }
        }

        public int BackgroundDisconnectMinutes
        {
            get { return _settings.GetInt("backgroundDisconnectMinutes"); }
            set
            {
                int clamped = value < 0 ? 0 : value;
                _settings.Set("backgroundDisconnectMinutes", clamped);
                RaisePropertyChanged(nameof(BackgroundDisconnectMinutes));
            }
        }

        public int ReconnectMaxAttempts
        {
            get { return _settings.GetInt("reconnectMaxAttempts"); }
            set
            {
                int clamped = SettingDefinitions.Require("reconnectMaxAttempts").Clamp(value);
                _settings.Set("reconnectMaxAttempts", clamped);
                RaisePropertyChanged(nameof(ReconnectMaxAttempts));
            }
        }

        public int ConnectTimeoutSeconds
        {
            get { return _settings.GetInt("connectTimeoutSeconds"); }
            set
            {
                int clamped = SettingDefinitions.Require("connectTimeoutSeconds").Clamp(value);
                _settings.Set("connectTimeoutSeconds", clamped);
                RaisePropertyChanged(nameof(ConnectTimeoutSeconds));
            }
        }

        // K03：Agent 密钥保留时间（分钟）。0 = 永不超时（native 惰性语义）。
        public int AgentKeyTimeoutMinutes
        {
            get { return _settings.GetInt("agentKeyTimeoutMinutes"); }
            set
            {
                int clamped = SettingDefinitions.Require("agentKeyTimeoutMinutes").Clamp(value);
                _settings.Set("agentKeyTimeoutMinutes", clamped);
                RaisePropertyChanged(nameof(AgentKeyTimeoutMinutes));
            }
        }

        public int SyncPollForegroundSeconds
        {
            get { return _settings.GetInt("syncPollForegroundSeconds"); }
            set
            {
                int clamped = value < 0 ? 0 : value;
                _settings.Set("syncPollForegroundSeconds", clamped);
                RaisePropertyChanged(nameof(SyncPollForegroundSeconds));
            }
        }

        // ---------- 关于 ----------

        public string LogLevel
        {
            get { return _settings.GetString("logLevel"); }
            set
            {
                string normalized = SettingDefinitions.Require("logLevel").NormalizeString(value);
                _settings.Set("logLevel", normalized);
                ApplyLogLevelNow(normalized);
                RaisePropertyChanged(nameof(LogLevel));
            }
        }

        public string OsVersionText
        {
            get { return _osVersion; }
            private set { SetProperty(ref _osVersion, value); }
        }

        public string DeviceFamilyText
        {
            get { return _deviceFamily; }
            private set { SetProperty(ref _deviceFamily, value); }
        }

        public string MemoryText
        {
            get { return _memoryText; }
            private set { SetProperty(ref _memoryText, value); }
        }

        public string AppVersionText
        {
            get { return _appVersion; }
            private set { SetProperty(ref _appVersion, value); }
        }

        // ---------- ComboBox 索引映射（页码后置代码只调这里） ----------

        public static int ThemeModeToIndex(string value)
        {
            if (string.Equals(value, "system", StringComparison.Ordinal)) { return 0; }
            if (string.Equals(value, "light", StringComparison.Ordinal)) { return 2; }
            return 1;
        }

        public static string IndexToThemeMode(int index)
        {
            if (index == 0) { return "system"; }
            if (index == 2) { return "light"; }
            return "dark";
        }

        public static int SortModeToIndex(string value)
        {
            return string.Equals(value, "recent", StringComparison.Ordinal) ? 1 : 0;
        }

        public static string IndexToSortMode(int index)
        {
            return index == 1 ? "recent" : "name";
        }

        public static int LanguageToIndex(string value)
        {
            if (string.Equals(value, "zh-CN", StringComparison.Ordinal)) { return 1; }
            if (string.Equals(value, "en-US", StringComparison.Ordinal)) { return 2; }
            return 0;
        }

        public static string IndexToLanguage(int index)
        {
            if (index == 1) { return "zh-CN"; }
            if (index == 2) { return "en-US"; }
            return "system";
        }

        public static int KeepScreenOnToIndex(string value)
        {
            if (string.Equals(value, "never", StringComparison.Ordinal)) { return 0; }
            if (string.Equals(value, "always", StringComparison.Ordinal)) { return 2; }
            return 1;
        }

        public static string IndexToKeepScreenOn(int index)
        {
            if (index == 0) { return "never"; }
            if (index == 2) { return "always"; }
            return "session";
        }

        public static int AltScrollToIndex(string value)
        {
            return string.Equals(value, "wheel", StringComparison.Ordinal) ? 1 : 0;
        }

        public static string IndexToAltScroll(int index)
        {
            return index == 1 ? "wheel" : "arrows";
        }

        public static int LogLevelToIndex(string value)
        {
            if (string.Equals(value, "debug", StringComparison.Ordinal)) { return 0; }
            if (string.Equals(value, "warn", StringComparison.Ordinal)) { return 2; }
            if (string.Equals(value, "error", StringComparison.Ordinal)) { return 3; }
            return 1;
        }

        public static string IndexToLogLevel(int index)
        {
            if (index == 0) { return "debug"; }
            if (index == 2) { return "warn"; }
            if (index == 3) { return "error"; }
            return "info";
        }

        public static int BackgroundDisconnectToIndex(int minutes)
        {
            int best = 0;
            for (int i = 0; i < BackgroundDisconnectOptions.Length; i++)
            {
                if (minutes == BackgroundDisconnectOptions[i]) { return i; }
                if (Math.Abs(minutes - BackgroundDisconnectOptions[i])
                    < Math.Abs(minutes - BackgroundDisconnectOptions[best]))
                {
                    best = i;
                }
            }
            return best;
        }

        public static int IndexToBackgroundDisconnect(int index)
        {
            if (index < 0 || index >= BackgroundDisconnectOptions.Length) { return 0; }
            return BackgroundDisconnectOptions[index];
        }

        public static int SyncPollToIndex(int seconds)
        {
            int best = 1;
            for (int i = 0; i < SyncPollOptions.Length; i++)
            {
                if (seconds == SyncPollOptions[i]) { return i; }
                if (Math.Abs(seconds - SyncPollOptions[i]) < Math.Abs(seconds - SyncPollOptions[best]))
                {
                    best = i;
                }
            }
            return best;
        }

        public static int IndexToSyncPoll(int index)
        {
            if (index < 0 || index >= SyncPollOptions.Length) { return 60; }
            return SyncPollOptions[index];
        }

        // ---------- 快捷键显示名（§5.16） ----------

        public static string ShortcutDisplayName(ShortcutAction action)
        {
            switch (action)
            {
                case ShortcutAction.NewTab: return Localized.Get("ShortcutAction_NewTab", "新标签");
                case ShortcutAction.ClosePane: return Localized.Get("ShortcutAction_ClosePane", "关闭窗格/标签");
                case ShortcutAction.NextTab: return Localized.Get("ShortcutAction_NextTab", "下一个标签");
                case ShortcutAction.PrevTab: return Localized.Get("ShortcutAction_PrevTab", "上一个标签");
                case ShortcutAction.SplitRight: return Localized.Get("Workspace_SplitRight", "向右分屏");
                case ShortcutAction.SplitDown: return Localized.Get("Workspace_SplitDown", "向下分屏");
                case ShortcutAction.FocusLeft: return Localized.Get("ShortcutAction_FocusLeft", "聚焦左侧窗格");
                case ShortcutAction.FocusUp: return Localized.Get("ShortcutAction_FocusUp", "聚焦上方窗格");
                case ShortcutAction.FocusRight: return Localized.Get("ShortcutAction_FocusRight", "聚焦右侧窗格");
                case ShortcutAction.FocusDown: return Localized.Get("ShortcutAction_FocusDown", "聚焦下方窗格");
                case ShortcutAction.Copy: return Localized.Get("ShortcutAction_Copy", "复制");
                case ShortcutAction.Paste: return Localized.Get("ShortcutAction_Paste", "粘贴");
                case ShortcutAction.FontIncrease: return Localized.Get("ShortcutAction_FontIncrease", "字号增大");
                case ShortcutAction.FontDecrease: return Localized.Get("ShortcutAction_FontDecrease", "字号减小");
                case ShortcutAction.FontReset: return Localized.Get("ShortcutAction_FontReset", "字号重置");
                case ShortcutAction.Find: return Localized.Get("ShortcutAction_Find", "查找");
                default: return action.ToString();
            }
        }

        // ---------- 诊断 ----------

        public void RefreshDiagnostics()
        {
            OsVersionText = DecodeOsVersion();
            DeviceFamilyText = ReadDeviceFamily();
            MemoryText = ReadMemoryText();
            AppVersionText = ReadAppVersion();
            RefreshProbes();
        }

        public string BuildDiagnosticsText()
        {
            var sb = new StringBuilder();
            sb.Append(Localized.Get("Settings_DiagAppVersion", "应用版本：")).Append(AppVersionText).Append("\r\n");
            sb.Append(Localized.Get("Settings_DiagDeviceFamily", "设备系列：")).Append(DeviceFamilyText).Append("\r\n");
            sb.Append(Localized.Get("Settings_DiagOsVersion", "系统版本：")).Append(OsVersionText).Append("\r\n");
            sb.Append(Localized.Get("Settings_DiagMemory", "内存：")).Append(MemoryText).Append("\r\n");
            for (int i = 0; i < Probes.Count; i++)
            {
                sb.Append(Probes[i].Name).Append("：").Append(Probes[i].Status).Append("\r\n");
            }
            return sb.ToString();
        }

        // ---------- 日志导出与清空 ----------

        // 合并 logs/*.log（按文件名排序，段首加文件名头），经 FileSavePicker 落盘。
        // 返回给 UI 显示的一句话；只记文件数与字节数，绝不记内容（脱敏）。
        public async Task<string> ExportLogsAsync()
        {
            StorageFolder logs;
            try
            {
                logs = await ApplicationData.Current.LocalFolder
                    .CreateFolderAsync("logs", CreationCollisionOption.OpenIfExists);
            }
            catch (Exception ex)
            {
                LogFailure(SshTool.Core.Common.LogLevel.Error, "打开日志目录失败", ex);
                return Localized.Get("Settings_LogPathNotFound", "找不到日志位置，请稍后重试");
            }

            List<StorageFile> logFiles = new List<StorageFile>();
            try
            {
                foreach (StorageFile f in await logs.GetFilesAsync())
                {
                    if (f.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                    {
                        logFiles.Add(f);
                    }
                }
            }
            catch (Exception ex)
            {
                LogFailure(SshTool.Core.Common.LogLevel.Error, "枚举日志文件失败", ex);
                return Localized.Get("Settings_LogListReadFailed", "读取日志列表失败，请稍后重试");
            }

            logFiles.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            if (logFiles.Count == 0)
            {
                return Localized.Get("Settings_LogNothingToExport", "没有可导出的日志");
            }

            var sb = new StringBuilder();
            long totalBytes = 0;
            for (int i = 0; i < logFiles.Count; i++)
            {
                string content = string.Empty;
                try
                {
                    content = await FileIO.ReadTextAsync(logFiles[i]);
                }
                catch (Exception)
                {
                    content = string.Empty;
                }
                totalBytes += content.Length;
                sb.Append("===== ").Append(logFiles[i].Name).Append(" =====\r\n");
                sb.Append(content);
                if (!content.EndsWith("\r\n", StringComparison.Ordinal)
                    && !content.EndsWith("\n", StringComparison.Ordinal))
                {
                    sb.Append("\r\n");
                }
            }

            FileSavePicker picker = new FileSavePicker();
            picker.SuggestedFileName = string.Format(
                CultureInfo.InvariantCulture, "lumia-ssh-logs-{0:yyyyMMdd-HHmmss}", DateTime.Now);
            picker.FileTypeChoices.Add(Localized.Get("Settings_LogFileType", "日志文件"), new List<string> { ".log" });
            StorageFile target;
            try
            {
                target = await picker.PickSaveFileAsync();
            }
            catch (Exception ex)
            {
                LogFailure(SshTool.Core.Common.LogLevel.Error, "打开保存对话框失败", ex);
                return Localized.Get("Export_OpenLocationFailed", "无法打开保存位置，请重试或换个文件夹");
            }
            if (target == null)
            {
                return Localized.Get("Export_Cancelled", "已取消导出");
            }

            try
            {
                CachedFileManager.DeferUpdates(target);
                await FileIO.WriteTextAsync(target, sb.ToString());
                FileUpdateStatus status = await CachedFileManager.CompleteUpdatesAsync(target);
                if (status != FileUpdateStatus.Complete)
                {
                    LogFailure(SshTool.Core.Common.LogLevel.Warning,
                        "日志导出未确认完成 status=" + status.ToString(), null);
                    return Localized.Get("Export_WriteFailed", "未能写入所选位置，请关闭占用该文件的程序后重试");
                }
            }
            catch (Exception ex)
            {
                LogFailure(SshTool.Core.Common.LogLevel.Error, "导出日志失败", ex);
                return Localized.Get("Export_Failed", "导出失败，请重试");
            }

            try
            {
                Logger.Log(SshTool.Core.Common.LogLevel.Info, "Settings",
                    "导出日志 " + logFiles.Count.ToString(CultureInfo.InvariantCulture)
                    + " 个文件，约 " + totalBytes.ToString(CultureInfo.InvariantCulture) + " 字符");
            }
            catch (Exception)
            {
            }
            return Localized.Format("Settings_ExportedCount", "已导出 {0} 个日志文件",
                logFiles.Count.ToString(CultureInfo.InvariantCulture));
        }

        public async Task<string> ClearLogsAsync()
        {
            int deleted = 0;
            try
            {
                StorageFolder logs = await ApplicationData.Current.LocalFolder
                    .CreateFolderAsync("logs", CreationCollisionOption.OpenIfExists);
                foreach (StorageFile f in await logs.GetFilesAsync())
                {
                    if (!f.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    try
                    {
                        await f.DeleteAsync(StorageDeleteOption.PermanentDelete);
                        deleted++;
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                LogFailure(SshTool.Core.Common.LogLevel.Error, "清空日志失败", ex);
                return Localized.Get("Settings_ClearFailed", "清空失败，请稍后重试");
            }

            try
            {
                Logger.Log(SshTool.Core.Common.LogLevel.Info, "Settings",
                    "清空日志 " + deleted.ToString(CultureInfo.InvariantCulture) + " 个文件");
            }
            catch (Exception)
            {
            }
            return deleted == 0
                ? Localized.Get("Settings_ClearNone", "没有可清空的日志")
                : Localized.Get("Settings_Cleared", "已清空日志");
        }

        // ---------- 内部 ----------

        private void OnSettingsChanged(object sender, SettingChangedEventArgs e)
        {
            // 外部写入（如键条编辑页）后刷新绑定；本类 setter 已逐个 Raise，无需映射 key。
            RaisePropertyChanged(nameof(ThemeMode));
            RaisePropertyChanged(nameof(UseSystemAccent));
            RaisePropertyChanged(nameof(HostSortMode));
            RaisePropertyChanged(nameof(ShowQuickConnect));
            RaisePropertyChanged(nameof(HapticsEnabled));
            RaisePropertyChanged(nameof(Language));
            RaisePropertyChanged(nameof(DefaultAppearanceId));
            RaisePropertyChanged(nameof(TerminalFontSize));
            RaisePropertyChanged(nameof(ScrollbackLines));
            RaisePropertyChanged(nameof(AltScreenScroll));
            RaisePropertyChanged(nameof(PasteConfirmMultiline));
            RaisePropertyChanged(nameof(KeyBarVisible));
            RaisePropertyChanged(nameof(KeyBarLayout));
            RaisePropertyChanged(nameof(KeyBarSummary));
            RaisePropertyChanged(nameof(ShortcutsJson));
            RaisePropertyChanged(nameof(ShortcutSummary));
            RaisePropertyChanged(nameof(KeepScreenOn));
            RaisePropertyChanged(nameof(KeepAliveInBackground));
            RaisePropertyChanged(nameof(BackgroundDisconnectMinutes));
            RaisePropertyChanged(nameof(ReconnectMaxAttempts));
            RaisePropertyChanged(nameof(ConnectTimeoutSeconds));
            RaisePropertyChanged(nameof(AgentKeyTimeoutMinutes));
            RaisePropertyChanged(nameof(SyncPollForegroundSeconds));
            RaisePropertyChanged(nameof(LogLevel));
        }

        private static int DefaultInt(string key)
        {
            return (int)SettingDefinitions.Require(key).DefaultValue;
        }

        private void ApplyThemeNow()
        {
            try
            {
                AppThemeMode mode = ParseTheme(ThemeMode);
                if (DispatcherHelper.HasThreadAccess)
                {
                    ThemeService.Apply(mode);
                }
                else
                {
                    DispatcherHelper.Post(() => ThemeService.Apply(mode));
                }
            }
            catch (Exception ex)
            {
                LogFailure(SshTool.Core.Common.LogLevel.Warning, "应用主题失败", ex);
            }
        }

        private static void ApplyAccentNow(bool useSystem)
        {
            try
            {
                if (DispatcherHelper.HasThreadAccess)
                {
                    ThemeService.UseSystemAccent = useSystem;
                }
                else
                {
                    DispatcherHelper.Post(() => { ThemeService.UseSystemAccent = useSystem; });
                }
            }
            catch (Exception)
            {
            }
        }

        private void ApplyLogLevelNow(string level)
        {
            try
            {
                FileLogger.Instance.MinLevel = ParseLogLevel(level);
            }
            catch (Exception ex)
            {
                LogFailure(SshTool.Core.Common.LogLevel.Warning, "应用日志级别失败", ex);
            }
        }

        private void RefreshKeepAwake()
        {
            try
            {
                KeepAwakeService keepAwake;
                if (ServiceRegistry.TryGet(out keepAwake))
                {
                    keepAwake.RefreshSettings();
                }
            }
            catch (Exception ex)
            {
                LogFailure(SshTool.Core.Common.LogLevel.Warning, "刷新常亮失败", ex);
            }
        }

        // 异常细节只进日志（类型 + 消息，落盘前由 FileLogger 过 Redactor），
        // 上屏一律是一句能看懂的中文短句。ex 允许为 null（仅记录状态说明）。
        private void LogFailure(SshTool.Core.Common.LogLevel level, string what, Exception ex)
        {
            try
            {
                Logger.Log(level, "Settings", what + "：" + Describe(ex));
            }
            catch (Exception)
            {
            }
        }

        // 仅进日志（LogFailure 唯一调用方），不进 UI：不本地化，门禁基线登记豁免。
        private static string Describe(Exception ex)
        {
            if (ex == null)
            {
                return "无异常对象";
            }
            return ex.GetType().Name + " " + ex.Message;
        }

        internal static AppThemeMode ParseTheme(string mode)
        {
            if (string.Equals(mode, "light", StringComparison.OrdinalIgnoreCase)) { return AppThemeMode.Light; }
            if (string.Equals(mode, "dark", StringComparison.OrdinalIgnoreCase)) { return AppThemeMode.Dark; }
            return AppThemeMode.System;
        }

        internal static SshTool.Core.Common.LogLevel ParseLogLevel(string level)
        {
            if (string.Equals(level, "debug", StringComparison.OrdinalIgnoreCase))
            {
                return SshTool.Core.Common.LogLevel.Debug;
            }
            if (string.Equals(level, "warn", StringComparison.OrdinalIgnoreCase))
            {
                return SshTool.Core.Common.LogLevel.Warning;
            }
            if (string.Equals(level, "error", StringComparison.OrdinalIgnoreCase))
            {
                return SshTool.Core.Common.LogLevel.Error;
            }
            return SshTool.Core.Common.LogLevel.Info;
        }

        private static string DecodeOsVersion()
        {
            try
            {
                if (!ApiInformation.IsTypePresent("Windows.System.Profile.AnalyticsInfo"))
                {
                    return "unknown";
                }
                string rawText = Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamilyVersion;
                ulong raw;
                if (!ulong.TryParse(rawText, NumberStyles.None, CultureInfo.InvariantCulture, out raw))
                {
                    return "unknown";
                }
                return string.Format(
                    CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}",
                    (raw & 0xFFFF000000000000UL) >> 48,
                    (raw & 0x0000FFFF00000000UL) >> 32,
                    (raw & 0x00000000FFFF0000UL) >> 16,
                    raw & 0x000000000000FFFFUL);
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private static string ReadDeviceFamily()
        {
            try
            {
                if (!ApiInformation.IsTypePresent("Windows.System.Profile.AnalyticsInfo"))
                {
                    return "unknown";
                }
                string family = Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily;
                return string.IsNullOrEmpty(family) ? "unknown" : family;
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private static string ReadMemoryText()
        {
            try
            {
                if (!ApiInformation.IsTypePresent("Windows.System.MemoryManager"))
                {
                    return Localized.Get("Common_Unavailable", "不可用");
                }
                ulong limit = Windows.System.MemoryManager.AppMemoryUsageLimit;
                ulong usage = Windows.System.MemoryManager.AppMemoryUsage;
                return Localized.Format("Settings_MemoryUsage", "已用 {0} MB / 上限 {1} MB",
                    (limit == 0 ? 0 : usage / 1048576UL).ToString(CultureInfo.InvariantCulture),
                    (limit == 0 ? 0 : limit / 1048576UL).ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
                return Localized.Get("Common_Unavailable", "不可用");
            }
        }

        private static string ReadAppVersion()
        {
            try
            {
                PackageVersion v = Package.Current.Id.Version;
                string arch = Package.Current.Id.Architecture.ToString();
#if NET_NATIVE
                const string toolchain = ".NET Native";
#else
                const string toolchain = "CoreCLR";
#endif
                return string.Format(
                    CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3} {4} {5}",
                    v.Major, v.Minor, v.Build, v.Revision, arch, toolchain);
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private void RefreshProbes()
        {
            Probes.Clear();
            // gate:resw-fallback-start —— 第二参是 AddProbe 内部 Localized.Get 的中文兜底
            AddProbe("Settings_ProbeHaptics", "触感", "Windows.Phone.Devices.Notification.VibrationDevice");
            AddProbe("Settings_ProbeStatusBar", "状态栏", "Windows.UI.ViewManagement.StatusBar");
            AddProbe("Settings_ProbeKeepAlive", "后台保活", "Windows.ApplicationModel.ExtendedExecution.ExtendedExecutionSession");
            AddProbe("Settings_ProbeDisplayRequest", "屏幕常亮", "Windows.System.Display.DisplayRequest");
            AddProbe("Settings_ProbeDeviceInfo", "设备信息", "Windows.System.Profile.AnalyticsInfo");
            AddProbe("Settings_ProbeMemory", "内存诊断", "Windows.System.MemoryManager");
            AddProbe("Settings_ProbeLogExport", "日志导出", "Windows.Storage.Pickers.FileSavePicker");
            AddProbe("Settings_ProbeCredentialEncryption", "凭据加密", "Windows.Security.Cryptography.DataProtection.DataProtectionProvider");
            AddProbe("Settings_ProbeDeviceName", "设备名称", "Windows.Security.ExchangeActiveSyncProvisioning.EasClientDeviceInformation");
            // gate:resw-fallback-end
        }

        private void AddProbe(string key, string fallback, string typeName)
        {
            bool present = false;
            try
            {
                present = ApiInformation.IsTypePresent(typeName);
            }
            catch (Exception)
            {
                present = false;
            }
            Probes.Add(new DiagnosticProbeRow
            {
                Name = Localized.Get(key, fallback),
                Status = present
                    ? Localized.Get("Common_Available", "可用")
                    : Localized.Get("Common_Unavailable", "不可用")
            });
        }
    }
}
