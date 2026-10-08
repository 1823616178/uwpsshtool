using System;

namespace SshTool.Core.Common
{
    // fix/functional-pass（P1-4）：设置项 language（"system" / "zh-CN" / "en-US"）→
    // ApplicationLanguages.PrimaryLanguageOverride 的取值。空串 = 跟随系统（清除覆盖）。
    public static class LanguagePolicy
    {
        public static string ToPrimaryLanguageOverride(string setting)
        {
            if (string.Equals(setting, "zh-CN", StringComparison.OrdinalIgnoreCase))
            {
                return "zh-CN";
            }
            if (string.Equals(setting, "en-US", StringComparison.OrdinalIgnoreCase))
            {
                return "en-US";
            }
            return string.Empty;
        }
    }
}
