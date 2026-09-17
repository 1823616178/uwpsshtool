using System;
using System.Threading.Tasks;
using SshTool.Core.Common;
using Windows.ApplicationModel;
using Windows.Storage;

namespace SshTool.App.Platform
{
    // X05：包内 Assets/appconfig.json（由 csproj 按 Configuration 从 Config/ 复制）。
    public sealed class AppConfig
    {
        public static AppConfig Current { get; private set; } = FromParsed(new ParsedAppConfig
        {
            SyncApiBaseUrl = AppConfigParser.DefaultSyncApiBaseUrl,
            AllowHttp = AppConfigParser.DefaultAllowHttp,
            LogLevel = AppConfigParser.DefaultLogLevel
        });

        public string SyncApiBaseUrl { get; private set; }
        public bool AllowHttp { get; private set; }
        public LogLevel LogLevel { get; private set; }

        public static async Task LoadAsync()
        {
            try
            {
                var file = await Package.Current.InstalledLocation.GetFileAsync(@"Assets\appconfig.json");
                var json = await FileIO.ReadTextAsync(file);
                var parsed = AppConfigParser.Parse(json);
                Current = FromParsed(parsed);
                FileLogger.Instance.MinLevel = Current.LogLevel;
                foreach (var w in parsed.Warnings)
                {
                    FileLogger.Instance.Log(SshTool.Core.Common.LogLevel.Warning, "AppConfig", w);
                }
                FileLogger.Instance.Log(SshTool.Core.Common.LogLevel.Info, "AppConfig",
                    "配置已加载 syncApiBaseUrl=" + Current.SyncApiBaseUrl + " logLevel=" + Current.LogLevel);
            }
            catch (Exception ex)
            {
                FileLogger.Instance.Log(SshTool.Core.Common.LogLevel.Warning, "AppConfig",
                    "读取 Assets/appconfig.json 失败，使用默认值：" + ex.Message);
            }
        }

        private static AppConfig FromParsed(ParsedAppConfig parsed)
        {
            LogLevel level;
            switch ((parsed.LogLevel ?? "info").ToLowerInvariant())
            {
                case "debug": level = SshTool.Core.Common.LogLevel.Debug; break;
                case "warning": level = SshTool.Core.Common.LogLevel.Warning; break;
                case "error": level = SshTool.Core.Common.LogLevel.Error; break;
                default: level = SshTool.Core.Common.LogLevel.Info; break;
            }
            return new AppConfig
            {
                SyncApiBaseUrl = parsed.SyncApiBaseUrl,
                AllowHttp = parsed.AllowHttp,
                LogLevel = level
            };
        }
    }
}
