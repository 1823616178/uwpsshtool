using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using SshTool.App.Controls;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.App.Terminal;
using SshTool.Core.Common;
using SshTool.Core.Sessions;
using SshTool.Core.Terminal;
using Windows.ApplicationModel.Resources;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views.Debug
{
    // N10：M1 出口演示。走 Core ISshSession（NativeSshSession 适配），
    // HostKeyCheck 把指纹打到页面后自动 Accept；密码认证后 ExecAsync。
    public sealed partial class DebugConnectPage : Page
    {
        private const string SettingPrefix = "debug.n10.";
        private const string ExecCommand = "uname -a; whoami";

        private readonly ResourceLoader _loader = ResourceLoader.GetForCurrentView();
        private readonly NativeSshSessionFactory _factory = new NativeSshSessionFactory();
        private readonly object _logLock = new object();
        private string _log = string.Empty;
        private ISshSession _session;
        private Stopwatch _watch;
        private string _lastReport = string.Empty;

        public DebugConnectPage()
        {
            this.InitializeComponent();
            HostBox.Text = LoadSetting("host", DebugSshDefaults.Host);
            PortBox.Text = LoadSetting("port", DebugSshDefaults.PortText);
            UserBox.Text = LoadSetting("user", DebugSshDefaults.User);
            PasswordBox.Password = DebugSshDefaults.Password;
            TermView.Input += OnTerminalInput;
            TermView.Shortcut += OnTerminalShortcut;
            TermKeyBar.Sticky = TermView.StickyModifiers;
            TermKeyBar.Input += OnKeyBarInput;
            TermKeyBar.Action += OnKeyBarAction;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            TermView.Input -= OnTerminalInput;
            TermView.Shortcut -= OnTerminalShortcut;
            TermKeyBar.Input -= OnKeyBarInput;
            TermKeyBar.Action -= OnKeyBarAction;
            ReleaseSession();
            base.OnNavigatedFrom(e);
        }

        private async void OnConnectClick(object sender, RoutedEventArgs e)
        {
            int port;
            if (!int.TryParse(PortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                || port <= 0 || port > 65535)
            {
                ShowBanner(BannerSeverity.Error, "端口不合法", "请输入 1–65535。");
                return;
            }

            SaveSetting("host", HostBox.Text);
            SaveSetting("port", PortBox.Text);
            SaveSetting("user", UserBox.Text);

            string host = HostBox.Text ?? string.Empty;
            string user = UserBox.Text ?? string.Empty;
            string password = PasswordBox.Password ?? string.Empty;

            ConnectButton.IsEnabled = false;
            BusyOverlay.IsActive = true;
            BusyOverlay.Message = "连接中…";
            ResultBanner.Visibility = Visibility.Collapsed;
            EventLogBox.Text = string.Empty;
            lock (_logLock) { _log = string.Empty; }
            KeyTypeText.Text = "握手中…";
            FingerprintText.Text = string.Empty;
            ArtView.Art = string.Empty;
            ConnDot.State = StatusDotState.Connecting;
            _watch = Stopwatch.StartNew();
            _lastReport = string.Empty;

            try
            {
                await RunSessionAsync(host, port, user, password);
            }
            catch (Exception ex)
            {
                AppendLog("异常 " + ex.GetType().Name);
                DispatcherHelper.Post(() =>
                {
                    ShowBanner(BannerSeverity.Error, "会话异常", ex.GetType().Name);
                    ConnDot.State = StatusDotState.Error;
                });
            }
            finally
            {
                password = null;
                DispatcherHelper.Post(() =>
                {
                    ConnectButton.IsEnabled = true;
                    BusyOverlay.IsActive = false;
                    BusyOverlay.Message = string.Empty;
                });
            }
        }

        private async Task RunSessionAsync(string host, int port, string user, string password)
        {
            ReleaseSession();
            ISshSession session = _factory.Create();
            _session = session;
            session.StateChanged += OnStateChanged;
            session.HostKeyCheck += OnHostKeyCheck;
            session.AuthPrompt += OnAuthPrompt;
            session.ContentDirty += OnContentDirty;

            try
            {
                AppendLog("Connect " + user + "@" + host + ":" + port.ToString(CultureInfo.InvariantCulture));

                var request = new SshConnectRequest
                {
                    Host = host,
                    Port = port,
                    Username = user,
                };
                SshErrorCode connectCode = await session.ConnectAsync(request);
                AppendLog("Connect → " + FormatCode(connectCode));
                if (connectCode != SshErrorCode.None)
                {
                    Fail(connectCode, "连接失败");
                    ReleaseSession();
                    return;
                }

                SshErrorCode authCode = await session.AuthenticatePasswordAsync(password);
                AppendLog("AuthPassword → " + FormatCode(authCode));
                if (authCode != SshErrorCode.None)
                {
                    Fail(authCode, "密码认证失败");
                    ReleaseSession();
                    return;
                }

                SshErrorCode shellCode = await session.OpenShellAsync(80, 24);
                AppendLog("OpenShell → " + FormatCode(shellCode));
                if (shellCode != SshErrorCode.None)
                {
                    Fail(shellCode, "打开 shell 失败");
                    ReleaseSession();
                    return;
                }

                DispatcherHelper.Post(() =>
                {
                    TermView.Session = session;
                    ConnDot.State = StatusDotState.Connected;
                    TermView.FocusInput();
                    ShowBanner(BannerSeverity.Success, "已连接",
                        "点终端弹出键盘；耗时 " + ElapsedMs() + " ms");
                });
                FrameScheduler.Instance.Wake();
                session.Write(Encoding.UTF8.GetBytes(ExecCommand + "\n"));
                AppendLog("Write " + ExecCommand);

                string report = DebugReport.EnvironmentHeader()
                    + "\nN10/T05 调试连接\n目标 " + user + "@" + host + ":"
                    + port.ToString(CultureInfo.InvariantCulture)
                    + "\nOpenShell 80x24 · " + ElapsedMs() + " ms"
                    + "\n\n事件\n" + EventLogSnapshot();
                _lastReport = report;
                await DebugReport.PublishAsync("n10-connect", "N10", report);
            }
            catch
            {
                ReleaseSession();
                throw;
            }
        }

        private void Fail(SshErrorCode code, string title)
        {
            string detail = FormatCode(code);
            DispatcherHelper.Post(() =>
            {
                ConnDot.State = StatusDotState.Error;
                ShowBanner(BannerSeverity.Error, title, detail);
            });
        }

        private void OnStateChanged(object sender, SessionStateChangedEventArgs e)
        {
            AppendLog("State=" + e.State
                + (e.ErrorCode == SshErrorCode.None ? string.Empty : " " + FormatCode(e.ErrorCode))
                + (string.IsNullOrEmpty(e.Detail) ? string.Empty : " " + e.Detail));
            DispatcherHelper.Post(() => { ConnDot.State = MapDot(e.State); });
        }

        private void OnHostKeyCheck(object sender, HostKeyCheckEventArgs e)
        {
            HostKeyInfo info = e.Info;
            // 要点：弹出指纹后自动接受。先记下指纹再 Accept，不挡握手。
            e.Accept();
            DispatcherHelper.Post(() =>
            {
                KeyTypeText.Text = "密钥类型：" + info.KeyType;
                FingerprintText.Text = info.FingerprintSha256;
                ArtView.Art = info.RandomArt;
            });
            AppendLog("HostKey " + info.KeyType + " " + info.FingerprintSha256 + " (自动接受)");
        }

        private void OnAuthPrompt(object sender, AuthPromptEventArgs e)
        {
            AppendLog("AuthPrompt（本页走密码认证，取消 KI）");
            e.Cancel();
        }

        private void OnContentDirty(object sender, EventArgs e)
        {
            AppendLog("ContentDirty");
            FrameScheduler.Instance.Wake();
        }

        private void OnTerminalInput(object sender, TerminalInputEventArgs e)
        {
            int n = e != null && e.Data != null ? e.Data.Length : 0;
            AppendLog("Input " + n.ToString(CultureInfo.InvariantCulture) + " bytes");
        }

        private void OnTerminalShortcut(object sender, ShortcutActionEventArgs e)
        {
            if (e == null)
            {
                return;
            }
            AppendLog("Shortcut " + e.Action);
        }

        private void OnKeyBarInput(object sender, TerminalInputEventArgs e)
        {
            TermView.SendInput(e != null ? e.Data : null);
        }

        private void OnKeyBarAction(object sender, KeyBarActionEventArgs e)
        {
            if (e == null)
            {
                return;
            }
            if (e.Action == KeyBarAction.HideKeyboard)
            {
                TermView.ToggleSoftKeyboard();
                AppendLog("KeyBar hidekb");
                return;
            }
            if (e.Action == KeyBarAction.Paste)
            {
                TermView.PasteFromClipboard();
                AppendLog("KeyBar paste");
                return;
            }
            AppendLog("KeyBar action " + e.Action);
        }

        private void OnSendClick(object sender, RoutedEventArgs e)
        {
            SendInput();
        }

        private void OnSendKeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                SendInput();
                e.Handled = true;
            }
        }

        private void SendInput()
        {
            if (_session == null || SendBox == null)
            {
                return;
            }
            string line = SendBox.Text ?? string.Empty;
            SendBox.Text = string.Empty;
            _session.Write(Encoding.UTF8.GetBytes(line + "\n"));
            AppendLog("Write " + line);
            FrameScheduler.Instance.Wake();
        }

        private async void OnCopyClick(object sender, RoutedEventArgs e)
        {
            string report = _lastReport;
            if (string.IsNullOrEmpty(report))
            {
                report = DebugReport.EnvironmentHeader() + "\nN10 事件\n" + EventLogSnapshot();
            }
            await DebugReport.PublishAsync("n10-connect", "N10", report);
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private void ReleaseSession()
        {
            ISshSession session = _session;
            _session = null;
            if (session == null)
            {
                return;
            }
            session.StateChanged -= OnStateChanged;
            session.HostKeyCheck -= OnHostKeyCheck;
            session.AuthPrompt -= OnAuthPrompt;
            session.ContentDirty -= OnContentDirty;
            DispatcherHelper.Post(() => { TermView.Session = null; });
            session.Dispose();
        }

        private void AppendLog(string message)
        {
            string line = "[+" + ElapsedMs() + "ms] " + message;
            lock (_logLock)
            {
                _log = string.IsNullOrEmpty(_log) ? line : _log + "\n" + line;
            }
            DispatcherHelper.Post(() => { EventLogBox.Text = EventLogSnapshot(); });
        }

        private string EventLogSnapshot()
        {
            lock (_logLock)
            {
                return _log;
            }
        }

        private string ElapsedMs()
        {
            long ms = _watch != null ? _watch.ElapsedMilliseconds : 0;
            return ms.ToString(CultureInfo.InvariantCulture);
        }

        private string FormatCode(SshErrorCode code)
        {
            int n = (int)code;
            string key = "Error_" + n.ToString(CultureInfo.InvariantCulture);
            string text = null;
            try
            {
                text = _loader.GetString(key);
            }
            catch (Exception)
            {
                text = null;
            }
            if (string.IsNullOrEmpty(text))
            {
                text = code.ToString();
            }
            return n.ToString(CultureInfo.InvariantCulture) + " " + text;
        }

        private void ShowBanner(BannerSeverity severity, string title, string message)
        {
            ResultBanner.Severity = severity;
            ResultBanner.Title = title;
            ResultBanner.Message = message;
            ResultBanner.Visibility = Visibility.Visible;
        }

        private static StatusDotState MapDot(SessionStateKind state)
        {
            switch (state)
            {
                case SessionStateKind.Connecting:
                case SessionStateKind.Handshaking:
                case SessionStateKind.Authenticating:
                    return StatusDotState.Connecting;
                case SessionStateKind.Established:
                    return StatusDotState.Connected;
                case SessionStateKind.Error:
                    return StatusDotState.Error;
                default:
                    return StatusDotState.Disconnected;
            }
        }

        private static string LoadSetting(string key, string fallback)
        {
            object value;
            string text = ApplicationData.Current.LocalSettings.Values.TryGetValue(SettingPrefix + key, out value)
                ? value as string : null;
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }

        private static void SaveSetting(string key, string value)
        {
            ApplicationData.Current.LocalSettings.Values[SettingPrefix + key] = value ?? string.Empty;
        }
    }
}
