using System;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using Windows.Globalization;

namespace SshTool.App.Platform
{
    // fix/functional-pass（P1-4）：把设置项 language 落到 ApplicationLanguages.PrimaryLanguageOverride。
    // 此前设置页只存值、从未应用，选「English」后界面依旧跟随系统。启动时（首个页面创建前）
    // 调用一次即对本次启动生效；设置页改动时也写入，toast 提示重启后完整生效。
    public static class LanguageOverride
    {
        public static void Apply(string setting)
        {
            try
            {
                string value = LanguagePolicy.ToPrimaryLanguageOverride(setting);
                string current = ApplicationLanguages.PrimaryLanguageOverride ?? string.Empty;
                if (!string.Equals(current, value, StringComparison.OrdinalIgnoreCase))
                {
                    ApplicationLanguages.PrimaryLanguageOverride = value;
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Language", "apply language override failed", ex);
            }
        }
    }
}
