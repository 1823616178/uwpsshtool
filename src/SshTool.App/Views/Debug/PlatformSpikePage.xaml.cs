using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using SshTool.Core.Common;
using SshTool.Core.Sync.Api;
using Windows.ApplicationModel.ExtendedExecution;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage;
using Windows.System.Display;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Views.Debug
{
    public sealed partial class PlatformSpikePage : Page
    {
        private const string SettingPrefix = "debug.sp03.";
        private const string DpapiFileName = "sp06-dpapi.bin";
        private ExtendedExecutionSession extendedSession;
        private DisplayRequest displayRequest;
        private bool displayActive;
        private string lastReport;

        public PlatformSpikePage()
        {
            InitializeComponent();
            HostBox.Text = LoadSetting("host", string.Empty);
            PortBox.Text = LoadSetting("port", "22");
            UserBox.Text = LoadSetting("user", string.Empty);
        }

        private static string LoadSetting(string key, string fallback)
        {
            object value;
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(SettingPrefix + key, out value)
                ? (value as string) ?? fallback : fallback;
        }

        private static void SaveSetting(string key, string value)
        {
            ApplicationData.Current.LocalSettings.Values[SettingPrefix + key] = value ?? string.Empty;
        }

        private async void OnRequestExecutionClick(object sender, RoutedEventArgs e)
        {
            await RequestExtendedExecutionAsync();
        }

        private async System.Threading.Tasks.Task<bool> RequestExtendedExecutionAsync()
        {
            ReleaseExtendedExecution();
            var session = new ExtendedExecutionSession
            {
                Reason = ExtendedExecutionReason.Unspecified,
                Description = "保持 SSH 会话并记录后台存活时间"
            };
            session.Revoked += OnExtendedExecutionRevoked;
            ExecutionText.Text = "ExtendedExecution：请求中…";
            try
            {
                ExtendedExecutionResult result = await session.RequestExtensionAsync();
                if (result == ExtendedExecutionResult.Allowed)
                {
                    extendedSession = session;
                    ExecutionText.Text = "ExtendedExecution：Allowed（等待撤销）";
                    await PublishAsync("sp06-execution", "ExtendedExecution RequestExtensionAsync = Allowed");
                    return true;
                }

                session.Revoked -= OnExtendedExecutionRevoked;
                session.Dispose();
                ExecutionText.Text = "ExtendedExecution：Denied";
                await PublishAsync("sp06-execution", "ExtendedExecution RequestExtensionAsync = Denied");
                return false;
            }
            catch (Exception ex)
            {
                session.Revoked -= OnExtendedExecutionRevoked;
                session.Dispose();
                ExecutionText.Text = "ExtendedExecution：异常 " + ex.GetType().Name;
                await PublishAsync("sp06-execution", ExecutionText.Text + " " + ex.Message);
                return false;
            }
        }

        private void OnExtendedExecutionRevoked(object sender, ExtendedExecutionRevokedEventArgs args)
        {
            string line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss} ExtendedExecution Revoked = {1}",
                DateTime.Now, args.Reason);
            DebugReport.Log("SP06", line);
            Infrastructure.DispatcherHelper.Post(() =>
            {
                ExecutionText.Text = line;
                ReportText.Text = line;
                lastReport = DebugReport.EnvironmentHeader() + "\n" + line;
            });
        }

        private void OnReleaseExecutionClick(object sender, RoutedEventArgs e)
        {
            ReleaseExtendedExecution();
            ExecutionText.Text = "ExtendedExecution：已释放";
        }

        private void ReleaseExtendedExecution()
        {
            var session = extendedSession;
            extendedSession = null;
            if (session != null)
            {
                session.Revoked -= OnExtendedExecutionRevoked;
                session.Dispose();
            }
        }

        private void OnDisplayActiveClick(object sender, RoutedEventArgs e)
        {
            if (displayActive) { return; }
            try
            {
                if (displayRequest == null) { displayRequest = new DisplayRequest(); }
                displayRequest.RequestActive();
                displayActive = true;
                StatusText.Text = "屏幕常亮已请求";
                DebugReport.Log("SP06", "DisplayRequest.RequestActive 成功");
            }
            catch (Exception ex)
            {
                StatusText.Text = "屏幕常亮请求失败：" + ex.GetType().Name;
            }
        }

        private void OnDisplayReleaseClick(object sender, RoutedEventArgs e)
        {
            ReleaseDisplayRequest();
            StatusText.Text = "屏幕常亮已释放";
        }

        private void ReleaseDisplayRequest()
        {
            if (!displayActive || displayRequest == null) { return; }
            try { displayRequest.RequestRelease(); }
            catch { }
            displayActive = false;
        }

        private async void OnDpapiClick(object sender, RoutedEventArgs e)
        {
            DpapiButton.IsEnabled = false;
            try
            {
                byte[] expected = Enumerable.Range(0, 1024).Select(i => (byte)((i * 31 + 17) & 0xff)).ToArray();
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    DebugReport.FolderName, CreationCollisionOption.OpenIfExists);
                var previous = await folder.TryGetItemAsync(DpapiFileName) as StorageFile;
                string previousResult = "首次运行，无跨重启文件";
                if (previous != null)
                {
                    var priorCipher = await FileIO.ReadBufferAsync(previous);
                    var priorPlain = await new DataProtectionProvider().UnprotectAsync(priorCipher);
                    byte[] priorBytes;
                    CryptographicBuffer.CopyToByteArray(priorPlain, out priorBytes);
                    previousResult = priorBytes.SequenceEqual(expected)
                        ? "跨重启解密 PASS" : "跨重启解密 FAIL（内容不一致）";
                }

                var provider = new DataProtectionProvider("LOCAL=user");
                var cipher = await provider.ProtectAsync(CryptographicBuffer.CreateFromByteArray(expected));
                var roundTrip = await new DataProtectionProvider().UnprotectAsync(cipher);
                byte[] actual;
                CryptographicBuffer.CopyToByteArray(roundTrip, out actual);
                var file = await folder.CreateFileAsync(DpapiFileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBufferAsync(file, cipher);

                string result = "DPAPI LOCAL=user 1 KiB 往返 "
                    + (actual.SequenceEqual(expected) ? "PASS" : "FAIL")
                    + "\n" + previousResult
                    + "\n密文 " + cipher.Length + " 字节；文件 " + file.Path;
                await PublishAsync("sp06-dpapi", result);
            }
            catch (Exception ex)
            {
                await PublishAsync("sp06-dpapi", "DPAPI FAIL：" + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                DpapiButton.IsEnabled = true;
            }
        }

        private async void OnHttpClick(object sender, RoutedEventArgs e)
        {
            HttpButton.IsEnabled = false;
            try
            {
                string root = Platform.AppConfig.Current.SyncApiBaseUrl.TrimEnd('/');
                var transport = new Platform.UwpHttpTransport();
                var emptyHeaders = new List<KeyValuePair<string, string>>();
                var get = await transport.SendAsync(
                    new HttpRequestData("GET", root + "/api/v1/me", emptyHeaders, null, 15000),
                    CancellationToken.None);
                var matchHeaders = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("If-Match", "\"revision-0\"")
                };
                var head = await transport.SendAsync(
                    new HttpRequestData("HEAD", root + "/api/v1/sync/document", matchHeaders, null, 15000),
                    CancellationToken.None);

                var report = new StringBuilder();
                AppendResponse(report, "GET /api/v1/me", get);
                report.AppendLine();
                AppendResponse(report, "HEAD /api/v1/sync/document + If-Match: \"revision-0\"", head);
                await PublishAsync("sp06-http", report.ToString());
            }
            catch (Exception ex)
            {
                await PublishAsync("sp06-http", "HTTP FAIL：" + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                HttpButton.IsEnabled = true;
            }
        }

        private static void AppendResponse(StringBuilder report, string title, HttpResponseData response)
        {
            report.AppendLine(title);
            report.AppendLine("status=" + response.StatusCode.ToString(CultureInfo.InvariantCulture));
            if (response.Headers != null)
            {
                foreach (var header in response.Headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
                {
                    report.AppendLine(header.Key + ": " + header.Value);
                }
            }
            if (!string.IsNullOrEmpty(response.Body))
            {
                string body = response.Body.Length > 2048 ? response.Body.Substring(0, 2048) + "…" : response.Body;
                report.AppendLine("body=" + body);
            }
        }

        private async void OnBackgroundSshClick(object sender, RoutedEventArgs e)
        {
            int port;
            int minutes;
            if (!int.TryParse(PortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                || port <= 0 || port > 65535
                || !int.TryParse(MinutesBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out minutes)
                || minutes < 1 || minutes > 30)
            {
                StatusText.Text = "端口或持续分钟不合法（分钟 1–30）";
                return;
            }

            SaveSetting("host", HostBox.Text);
            SaveSetting("port", PortBox.Text);
            SaveSetting("user", UserBox.Text);
            SshButton.IsEnabled = false;
            StatusText.Text = "后台 SSH tick 运行中…";
            if (extendedSession == null)
            {
                await RequestExtendedExecutionAsync();
            }

            int ticks = minutes * 6;
            string command = string.Format(
                CultureInfo.InvariantCulture,
                "i=0; while [ $i -lt {0} ]; do date -u '+tick %Y-%m-%dT%H:%M:%SZ'; i=$((i+1)); sleep 10; done",
                ticks);
            try
            {
                string result = await SshTool.Native.SshSpike.ExecAsync(
                    HostBox.Text, port, UserBox.Text, PasswordBox.Password, command);
                PasswordBox.Password = string.Empty;
                await PublishAsync("sp06-background", "后台 SSH tick（计划 " + minutes
                    + " 分钟）\n" + result);
            }
            catch (Exception ex)
            {
                PasswordBox.Password = string.Empty;
                await PublishAsync("sp06-background", "后台 SSH FAIL：" + ex.GetType().Name + " " + ex.Message);
            }
            finally
            {
                SshButton.IsEnabled = true;
            }
        }

        private async System.Threading.Tasks.Task PublishAsync(string prefix, string body)
        {
            lastReport = DebugReport.EnvironmentHeader() + "\n" + body;
            ReportText.Text = lastReport;
            StatusText.Text = await DebugReport.PublishAsync(prefix, "SP06", lastReport);
        }

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(lastReport)) { return; }
            StatusText.Text = DebugReport.CopyToClipboard(lastReport) ? "已复制" : "复制失败";
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            ReleaseDisplayRequest();
            ReleaseExtendedExecution();
            if (Frame != null && Frame.CanGoBack) { Frame.GoBack(); }
        }
    }
}
