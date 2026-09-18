using System;
using System.Globalization;
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
    /// </summary>
    internal static class SshAutoTest
    {
        public const string SeedFileName = "ssh-autotest.json";

        public static async Task RunIfSeedPresentAsync()
        {
            string json;
            try
            {
                var item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(SeedFileName);
                var file = item as StorageFile;
                if (file == null) { return; }
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
                string host = seed.Value<string>("host") ?? string.Empty;
                int port = seed.Value<int?>("port") ?? 22;
                string user = seed.Value<string>("user") ?? string.Empty;
                string password = seed.Value<string>("password") ?? string.Empty;
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
        }
    }
}
