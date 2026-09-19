using System;
using System.Globalization;
using SshTool.Core.Spikes;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Views.Debug
{
    public sealed partial class SpikePage : Page
    {
        private string _lastReport;

        // 连接参数记在 LocalSettings 里，免得每次在手机上重打；密码不持久化，
        // 每次打开页面恢复为 DebugSshDefaults 中的开发调试值。
        private const string SettingPrefix = "debug.sp03.";

        public SpikePage()
        {
            this.InitializeComponent();
            Libssh2Text.Text = "libssh2: " + SshTool.Native.SshSpike.Version();
            HostBox.Text = LoadSetting("host", DebugSshDefaults.Host);
            PortBox.Text = LoadSetting("port", DebugSshDefaults.PortText);
            UserBox.Text = LoadSetting("user", DebugSshDefaults.User);
            PasswordBox.Password = DebugSshDefaults.Password;
            CommandBox.Text = LoadSetting("command", "uname -a");
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

        private async void OnSshRunClick(object sender, RoutedEventArgs e)
        {
            int port;
            if (!int.TryParse(PortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port <= 0)
            {
                StatusText.Text = "端口不合法";
                return;
            }
            SaveSetting("host", HostBox.Text);
            SaveSetting("port", PortBox.Text);
            SaveSetting("user", UserBox.Text);
            SaveSetting("command", CommandBox.Text);

            SshRunButton.IsEnabled = false;
            StatusText.Text = "连接中…";
            ReportText.Text = "连接中…";
            try
            {
                // 阻塞式 socket 在 native 侧的后台线程跑（§4.2）
                string result = await SshTool.Native.SshSpike.ExecAsync(
                    HostBox.Text, port, UserBox.Text, PasswordBox.Password, CommandBox.Text);

                // 报告里带上目标与命令，但**绝不带密码**（§12.2）
                _lastReport = DebugReport.EnvironmentHeader()
                    + "\nSP03 libssh2 连接测试\n目标 " + UserBox.Text + "@" + HostBox.Text + ":" + port
                    + "\n命令 " + CommandBox.Text
                    + "\nlibssh2 " + SshTool.Native.SshSpike.Version() + "\n\n"
                    + result;
                ReportText.Text = _lastReport;
                CopyButton.IsEnabled = true;
                StatusText.Text = await DebugReport.PublishAsync("sp03-ssh", "SP03", _lastReport);
            }
            catch (Exception ex)
            {
                ReportText.Text = "异常：" + ex.GetType().Name + " " + ex.Message;
                StatusText.Text = "失败";
            }
            finally
            {
                SshRunButton.IsEnabled = true;
            }
        }

        private async void OnUdpProbeClick(object sender, RoutedEventArgs e)
        {
            string result = SshTool.Native.SshSpike.LoopbackUdpProbe();
            _lastReport = DebugReport.EnvironmentHeader() + "\nSP03 回环 UDP 唤醒探测\n\n" + result;
            ReportText.Text = _lastReport;
            CopyButton.IsEnabled = true;
            StatusText.Text = await DebugReport.PublishAsync("sp03-udp", "SP03", _lastReport);
        }

        private async void OnRunClick(object sender, RoutedEventArgs e)
        {
            // 报告首行自报运行环境：贴出来的结果必须能自证「真机还是 PC」「.NET Native 还是 CoreCLR」，
            // 否则无法判断 Spike 结论是否成立（SP01 已因此返工一次，见 04-TASKS 进度日志 2026-09-18）。
            _lastReport = DebugReport.EnvironmentHeader() + "\n" + JsonSpike.RoundTrip();
            ReportText.Text = _lastReport;
            CopyButton.IsEnabled = true;
            // 落盘 + 复制到剪贴板 + 写 app.log，省得人对着屏幕抄
            StatusText.Text = await DebugReport.PublishAsync("sp01-json", "SP01", _lastReport);
        }

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastReport)) { return; }
            StatusText.Text = DebugReport.CopyToClipboard(_lastReport) ? "已复制到剪贴板" : "复制失败";
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }
    }
}
