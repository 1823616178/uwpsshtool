using System;
using System.Globalization;
using System.Threading.Tasks;
using SshTool.Core.Common;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System.Profile;

namespace SshTool.App.Views.Debug
{
    /// <summary>
    /// 调试页报告的共用出口：环境自述行 + 落盘 + 复制到剪贴板 + 写 app.log。
    /// 目的是让 📱 验收结果能整份取回，不用人对着屏幕抄。
    /// </summary>
    internal static class DebugReport
    {
        public const string FolderName = "spike-reports";

        /// <summary>包版本 + 配置 + 工具链，MainPage 上直接显示，用来分辨手机上装的是哪次构建。</summary>
        public static string BuildTag()
        {
#if NET_NATIVE
            const string toolchain = ".NET Native";
#else
            const string toolchain = "CoreCLR";
#endif
#if DEBUG
            const string config = "Debug";
#else
            const string config = "Release";
#endif
            var id = Package.Current.Id;
            var v = id.Version;
            return string.Format(
                CultureInfo.InvariantCulture,
                "v{0}.{1}.{2}.{3} | {4} {5} | {6}",
                v.Major, v.Minor, v.Build, v.Revision, id.Architecture, config, toolchain);
        }

        /// <summary>报告首行：回答「跑在哪台机器、哪条工具链」。</summary>
        public static string EnvironmentHeader()
        {
#if NET_NATIVE
            const string toolchain = ".NET Native";
#else
            const string toolchain = "CoreCLR(IL)";
#endif
#if DEBUG
            const string config = "Debug";
#else
            const string config = "Release";
#endif
            var id = Package.Current.Id;
            var v = id.Version;
            return string.Format(
                CultureInfo.InvariantCulture,
                "ENV: {0} | OS {1} | {2} {3} | app v{4}.{5}.{6}.{7} | {8} | {9:yyyy-MM-dd HH:mm:ss}",
                AnalyticsInfo.VersionInfo.DeviceFamily,
                OsVersion(),
                id.Architecture, config,
                v.Major, v.Minor, v.Build, v.Revision,
                toolchain,
                DateTime.Now);
        }

        // DeviceFamilyVersion 是打包成字符串的 64 位整数，每 16 位一段
        public static string OsVersion()
        {
            ulong raw;
            if (!ulong.TryParse(AnalyticsInfo.VersionInfo.DeviceFamilyVersion,
                    NumberStyles.None, CultureInfo.InvariantCulture, out raw))
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

        /// <summary>落盘到 LocalFolder\spike-reports\&lt;prefix&gt;-&lt;时间戳&gt;.txt，返回可读路径。</summary>
        public static async Task<string> SaveAsync(string prefix, string content)
        {
            var folder = await ApplicationData.Current.LocalFolder
                .CreateFolderAsync(FolderName, CreationCollisionOption.OpenIfExists);
            string name = string.Format(
                CultureInfo.InvariantCulture, "{0}-{1:yyyyMMdd-HHmmss}.txt", prefix, DateTime.Now);
            var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, content);
            return file.Path;
        }

        /// <summary>整份复制到剪贴板（手机上一按即可粘贴出去）。</summary>
        public static bool CopyToClipboard(string content)
        {
            try
            {
                var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                package.SetText(content ?? string.Empty);
                Clipboard.SetContent(package);
                Clipboard.Flush();   // 应用退出后仍保留
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>同时写一份到 app.log，便于用设备门户一次性取回。</summary>
        public static void Log(string tag, string content)
        {
            Platform.FileLogger.Instance.Log(LogLevel.Info, tag, content);
        }

        /// <summary>落盘 + 复制 + 写日志，返回给 UI 显示的一行提示。</summary>
        public static async Task<string> PublishAsync(string prefix, string tag, string content)
        {
            Log(tag, content);
            bool copied = CopyToClipboard(content);
            string path;
            try
            {
                path = await SaveAsync(prefix, content);
            }
            catch (Exception ex)
            {
                return "已写入 app.log" + (copied ? "，已复制到剪贴板" : "") + "；落盘失败：" + ex.Message;
            }
            return string.Format(
                CultureInfo.InvariantCulture,
                "已保存：{0}{1}，并已写入 app.log",
                path, copied ? "；已复制到剪贴板" : "");
        }
    }
}
