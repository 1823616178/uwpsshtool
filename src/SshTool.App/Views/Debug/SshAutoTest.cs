using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Windows.Storage;

namespace SshTool.App.Views.Debug
{
    /// <summary>
    /// SP03 及后续 N 系列回归用的无人值守 SSH 测试（真机不用人戳屏幕）：
    /// 启动后若 LocalFolder 根存在 ssh-autotest.json，读出参数<b>立即删除文件</b>，
    /// 然后执行 SSH 命令与回环 UDP 探测，报告走 DebugReport（spike-reports + app.log）。
    /// 种子文件由设备门户/本机直接写入；密码只在文件里短暂停留，读出即删，
    /// 不记日志、不落盘、不入报告（§12.2）。
    /// 格式：{"host":"…","port":22,"user":"…","password":"…","command":"uname -a","udp":true}
    /// 四个连接键可省略，省略或字符串留空时使用 DebugSshDefaults（见 doc/ENV.md §12）。
    /// </summary>
    internal static class SshAutoTest
    {
        public const string SeedFileName = "ssh-autotest.json";
        private static int isRunning;

        public static async Task RunIfSeedPresentAsync()
        {
            // OnLaunched 与 Resuming 可能紧挨着到达。只允许一个消费者读取并删除种子，
            // 避免重复探测、双跑 SSH，或一个调用在另一个调用删除后误报失败。
            if (Interlocked.CompareExchange(ref isRunning, 1, 0) != 0)
            {
                return;
            }

            try
            {
                await RunCoreAsync();
            }
            finally
            {
                Volatile.Write(ref isRunning, 0);
            }
        }

        private static async Task RunCoreAsync()
        {
            string json;
            try
            {
                // 先把报告目录建出来（应用自建目录 ACL 归本应用）：既是报告落盘处，
                // 也是门户推种子的可用通道（根目录被推入的文件 ACL 不含本应用，读不了）
                await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    DebugReport.FolderName, CreationCollisionOption.OpenIfExists);
                // 种子可能在两处：LocalFolder 根，或 spike-reports 子目录。
                // 门户往 LocalState 根推的文件 ACL 不含本应用（"Update file access failed"，
                // 2026-09-18 实测：枚举可见但打开 UnauthorizedAccess）；推到应用自建的
                // spike-reports 目录里则继承该目录的 ACL，可读。
                StorageFile file = null;
                string foundIn = null;
                foreach (var probe in new[] {
                    ApplicationData.Current.LocalFolder.Path,
                    System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, DebugReport.FolderName) })
                {
                    try
                    {
                        file = await StorageFile.GetFileFromPathAsync(
                            System.IO.Path.Combine(probe, SeedFileName));
                        foundIn = probe;
                        break;
                    }
                    catch (Exception probeEx)
                    {
                        Platform.FileLogger.Instance.Log(SshTool.Core.Common.LogLevel.Info, "SP03",
                            "自动测试：探测 " + probe + " 未得种子（" + probeEx.GetType().Name + "）");
                    }
                }
                if (file == null)
                {
                    return;
                }
                Platform.FileLogger.Instance.Log(SshTool.Core.Common.LogLevel.Info, "SP03",
                    "自动测试：发现种子（" + foundIn + "），读出后删除");
                json = await FileIO.ReadTextAsync(file);
                // 先删后跑：不管后面成功失败，密码都不在盘上多留一秒
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch (Exception ex)
            {
                Platform.FileLogger.Instance.Log(SshTool.Core.Common.LogLevel.Warning, "SP03",
                    "自动测试种子读取失败：" + ex.GetType().Name);
                return;
            }

            try
            {
                var seed = JObject.Parse(json);
                string host = seed.Value<string>("host");
                int port = seed.Value<int?>("port") ?? DebugSshDefaults.Port;
                string user = seed.Value<string>("user");
                string password = seed.Value<string>("password");
                host = string.IsNullOrWhiteSpace(host) ? DebugSshDefaults.Host : host;
                user = string.IsNullOrWhiteSpace(user) ? DebugSshDefaults.User : user;
                password = string.IsNullOrWhiteSpace(password) ? DebugSshDefaults.Password : password;
                string command = seed.Value<string>("command") ?? "uname -a";
                bool udp = seed.Value<bool?>("udp") ?? true;

                Platform.FileLogger.Instance.Log(SshTool.Core.Common.LogLevel.Info, "SP03",
                    string.Format(CultureInfo.InvariantCulture,
                        "自动测试开始：{0}@{1}:{2} 命令 {3}", user, host, port, command));

                string result = await SshTool.Native.SshSpike.ExecAsync(host, port, user, password, command);
                password = null;
                string report = DebugReport.EnvironmentHeader()
                    + "\nSP03 libssh2 连接测试（无人值守）\n目标 " + user + "@" + host + ":" + port
                    + "\n命令 " + command
                    + "\nlibssh2 " + SshTool.Native.SshSpike.Version() + "\n\n"
                    + result;
                await DebugReport.PublishAsync("sp03-ssh", "SP03", report);

                if (udp)
                {
                    string probe = SshTool.Native.SshSpike.LoopbackUdpProbe();
                    string udpReport = DebugReport.EnvironmentHeader()
                        + "\nSP03 回环 UDP 唤醒探测（无人值守）\n\n" + probe;
                    await DebugReport.PublishAsync("sp03-udp", "SP03", udpReport);
                }
            }
            catch (Exception ex)
            {
                // 异常本身可能带服务器回显之外的敏感信息？SSH 异常不含密码，原样入报告
                string failure = DebugReport.EnvironmentHeader()
                    + "\nSP03 自动测试失败\n异常：" + ex.GetType().Name + " " + ex.Message;
                await DebugReport.PublishAsync("sp03-ssh", "SP03", failure);
            }

            // 日志走队列，主动落盘——否则要靠下一次挂起才能从 app.log 看到上面的行
            try { await Platform.FileLogger.Instance.FlushAsync(); }
            catch { /* 诊断性落盘失败不影响主流程 */ }
        }
    }
}
