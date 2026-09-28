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

        // 有取值范围的整型设置（闭区间）。范围在此定义一次，设置页的 Slider 与
        // ViewModel 钳制都从这里取，不再各写一份。
        public SettingDefinition(string key, int defaultValue, int minValue, int maxValue)
            : this(key, SettingType.Int, defaultValue)
        {
            if (minValue > maxValue || defaultValue < minValue || defaultValue > maxValue)
            {
                throw new ArgumentException("设置范围非法: " + key, nameof(defaultValue));
            }
            MinValue = minValue;
            MaxValue = maxValue;
        }

        public string Key { get; private set; }
        public SettingType Type { get; private set; }
        public object DefaultValue { get; private set; }

        // 枚举类字符串的合法值（Ordinal 比较）；null = 任意字符串
        public string[] AllowedValues { get; private set; }

        // 整型闭区间；null = 不限。
        public int? MinValue { get; private set; }
        public int? MaxValue { get; private set; }

        // 把整数钳进范围（无范围时原样返回）。
        public int Clamp(int value)
        {
            if (MinValue.HasValue && value < MinValue.Value)
            {
                return MinValue.Value;
            }
            if (MaxValue.HasValue && value > MaxValue.Value)
            {
                return MaxValue.Value;
            }
            return value;
        }

        // 枚举类字符串：合法则原样返回，否则回退默认值（null/空串同样回退）。
        public string NormalizeString(string value)
        {
            return IsValidValue(value) ? value : (string)DefaultValue;
        }

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
                    return value is int && Clamp((int)value) == (int)value;
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
            new SettingDefinition("terminalFontSize", 12, 8, 28),
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
            new SettingDefinition("scrollbackLines", 5000, 1000, 50000),
            new SettingDefinition("shortcuts", SettingType.String, "{}"),
            new SettingDefinition("hostGroupCollapsed", SettingType.String, "{}"),
            new SettingDefinition("hostSortMode", SettingType.String, "name",
                new[] { "name", "recent" }),
            new SettingDefinition("useSystemAccent", SettingType.Bool, true),
            new SettingDefinition("showQuickConnect", SettingType.Bool, true),
            // V02：快速连接展开态（折叠行 ↔ 表单）；showQuickConnect 仍管整个区域显隐。
            new SettingDefinition("hostQuickConnectExpanded", SettingType.Bool, false),
            new SettingDefinition("language", SettingType.String, "system",
                new[] { "system", "zh-CN", "en-US" }),
            new SettingDefinition("connectTimeoutSeconds", 15, 5, 60),
            new SettingDefinition("reconnectMaxAttempts", 6, 0, 10),
            // K03：Agent 密钥保留时间（分钟，0 = 永不超时；上限 120 即 2 小时）。
            new SettingDefinition("agentKeyTimeoutMinutes", 15, 0, 120),
            new SettingDefinition("syncPollForegroundSeconds", SettingType.Int, 60),
            new SettingDefinition("lastVersionSeen", SettingType.String, ""),
            new SettingDefinition("logLevel", SettingType.String, "info",
                new[] { "debug", "info", "warn", "error" }),
            // W04（01-DESIGN §16.4）：应用锁、后台断线通知、终端响铃反馈。
            new SettingDefinition("appLockEnabled", SettingType.Bool, false),
            new SettingDefinition("notifyOnDisconnect", SettingType.Bool, true),
            new SettingDefinition("bellMode", SettingType.String, "vibrate",
                new[] { "vibrate", "visual", "none" }),
            // W05（01-DESIGN §16.5）：SFTP 路径书签，JSON：hostId → 路径数组。
            new SettingDefinition("sftpBookmarks", SettingType.String, "{}")
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
