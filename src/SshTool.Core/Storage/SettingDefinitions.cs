using System;
using System.Collections.Generic;

namespace SshTool.Core.Storage
{
    public enum SettingType
    {
        String = 0,
        Int = 1,
        Bool = 2
    }

    // 单个设置键的定义：类型、默认值、（枚举类字符串的）合法值表。
    public sealed class SettingDefinition
    {
        public SettingDefinition(string key, SettingType type, object defaultValue, string[] allowedValues = null)
        {
            Key = key;
            Type = type;
            DefaultValue = defaultValue;
            AllowedValues = allowedValues;
        }

        public string Key { get; private set; }
        public SettingType Type { get; private set; }
        public object DefaultValue { get; private set; }

        // 枚举类字符串的合法值（Ordinal 比较）；null = 任意字符串
        public string[] AllowedValues { get; private set; }

        public bool IsValidValue(object value)
        {
            switch (Type)
            {
                case SettingType.String:
                    var s = value as string;
                    if (s == null)
                    {
                        return false;
                    }
                    if (AllowedValues != null && Array.IndexOf(AllowedValues, s) < 0)
                    {
                        return false;
                    }
                    return true;
                case SettingType.Int:
                    return value is int;
                case SettingType.Bool:
                    return value is bool;
                default:
                    return false;
            }
        }
    }

    // 01-DESIGN §8.3 设置键定义表：默认值与类型以此表为准，强类型访问器与之逐键对应
    // （完整性由 SettingsRepositoryTests 用反射对拍）。
    public static class SettingDefinitions
    {
        private const string DefaultKeyBarLayout =
            "esc,tab,ctrl,alt,up,down,left,right,home,end,pgup,pgdn,pipe,slash,minus,tilde,paste";

        public static readonly IReadOnlyList<SettingDefinition> All = new[]
        {
            new SettingDefinition("themeMode", SettingType.String, "dark",
                new[] { "system", "dark", "light" }),
            new SettingDefinition("defaultAppearanceId", SettingType.String, "builtin-harmony-dark"),
            new SettingDefinition("terminalFontSize", SettingType.Int, 12),
            new SettingDefinition("keepScreenOn", SettingType.String, "session",
                new[] { "never", "session", "always" }),
            new SettingDefinition("keepAliveInBackground", SettingType.Bool, true),
            new SettingDefinition("backgroundDisconnectMinutes", SettingType.Int, 0),
            new SettingDefinition("hapticsEnabled", SettingType.Bool, true),
            new SettingDefinition("keyBarLayout", SettingType.String, DefaultKeyBarLayout),
            new SettingDefinition("keyBarVisible", SettingType.Bool, true),
            new SettingDefinition("pasteConfirmMultiline", SettingType.Bool, true),
            new SettingDefinition("altScreenScroll", SettingType.String, "arrows",
                new[] { "arrows", "wheel" }),
            new SettingDefinition("scrollbackLines", SettingType.Int, 5000),
            new SettingDefinition("shortcuts", SettingType.String, "{}"),
            new SettingDefinition("hostGroupCollapsed", SettingType.String, "{}"),
            new SettingDefinition("hostSortMode", SettingType.String, "name",
                new[] { "name", "recent" }),
            new SettingDefinition("useSystemAccent", SettingType.Bool, true),
            new SettingDefinition("showQuickConnect", SettingType.Bool, true),
            new SettingDefinition("language", SettingType.String, "system",
                new[] { "system", "zh-CN", "en-US" }),
            new SettingDefinition("connectTimeoutSeconds", SettingType.Int, 15),
            new SettingDefinition("reconnectMaxAttempts", SettingType.Int, 6),
            new SettingDefinition("agentKeyTimeoutMinutes", SettingType.Int, 15),
            new SettingDefinition("syncPollForegroundSeconds", SettingType.Int, 60),
            new SettingDefinition("lastVersionSeen", SettingType.String, ""),
            new SettingDefinition("logLevel", SettingType.String, "info",
                new[] { "debug", "info", "warn", "error" })
        };

        public static SettingDefinition Find(string key)
        {
            for (int i = 0; i < All.Count; i++)
            {
                if (All[i].Key == key)
                {
                    return All[i];
                }
            }
            return null;
        }

        public static SettingDefinition Require(string key)
        {
            var def = Find(key);
            if (def == null)
            {
                throw new ArgumentException("未知设置键: " + key, nameof(key));
            }
            return def;
        }
    }
}
