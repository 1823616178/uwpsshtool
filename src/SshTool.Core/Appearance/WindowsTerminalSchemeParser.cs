using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SshTool.Core.Models;

namespace SshTool.Core.Appearance
{
    // A04：Windows Terminal 配色 → AppearanceProfile 列表。
    // 输入二选一：单个 scheme 对象，或含 `schemes` 数组的 settings.json
    //（数组内多个 scheme 时全部返回，由页面让用户选择导入哪几个）。
    // 键：black…white / brightBlack…brightWhite（→ 调色板 0–15）、
    // foreground / background / cursorColor / selectionBackground；
    // 20 个键全部必需，缺键、非法 JSON、非法颜色值一律返回 Fail（不抛异常）。
    // 颜色接受 #RRGGBB（大小写不限，统一转大写）与 #RGB 简写。
    public static class WindowsTerminalSchemeParser
    {
        private static readonly string[] PaletteKeys = new string[]
        {
            "black", "red", "green", "yellow", "blue", "purple", "cyan", "white",
            "brightBlack", "brightRed", "brightGreen", "brightYellow",
            "brightBlue", "brightPurple", "brightCyan", "brightWhite"
        };

        private const string ForegroundKey = "foreground";
        private const string BackgroundKey = "background";
        private const string CursorKey = "cursorColor";
        private const string SelectionKey = "selectionBackground";

        public static ThemeImportResult Parse(string text, string suggestedName)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return ThemeImportResult.Fail("文件为空：不是合法的 Windows Terminal 配色");
            }
            JObject root;
            try
            {
                root = JObject.Parse(text);
            }
            catch (Exception ex)
            {
                return ThemeImportResult.Fail("不是合法 JSON：" + ex.Message);
            }
            try
            {
                JToken schemesToken = root["schemes"];
                if (schemesToken != null)
                {
                    return ParseMany(schemesToken, suggestedName);
                }
                if (!LooksLikeScheme(root))
                {
                    return ThemeImportResult.Fail("既不是单个 scheme（缺少如 'black'、'foreground' 等键），"
                        + "也没有 'schemes' 数组");
                }
                var single = new List<AppearanceProfile>
                {
                    ParseScheme(root, UniqueName(SchemeName(root, suggestedName), null))
                };
                return ThemeImportResult.Succeed(single);
            }
            catch (Exception ex)
            {
                return ThemeImportResult.Fail("不是合法的 Windows Terminal 配色：" + ex.Message);
            }
        }

        private static ThemeImportResult ParseMany(JToken schemesToken, string suggestedName)
        {
            if (schemesToken.Type != JTokenType.Array)
            {
                return ThemeImportResult.Fail("'schemes' 不是数组");
            }
            var schemes = (JArray)schemesToken;
            if (schemes.Count == 0)
            {
                return ThemeImportResult.Fail("'schemes' 数组为空：没有可导入的配色");
            }
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            var profiles = new List<AppearanceProfile>(schemes.Count);
            for (int i = 0; i < schemes.Count; i++)
            {
                if (schemes[i].Type != JTokenType.Object)
                {
                    return ThemeImportResult.Fail("'schemes' 第 " + (i + 1) + " 项不是对象");
                }
                var scheme = (JObject)schemes[i];
                string name = UniqueName(SchemeName(scheme, suggestedName), usedNames);
                try
                {
                    profiles.Add(ParseScheme(scheme, name));
                }
                catch (Exception ex)
                {
                    return ThemeImportResult.Fail("'schemes' 第 " + (i + 1) + " 项（" + name + "）非法：" + ex.Message);
                }
            }
            return ThemeImportResult.Succeed(profiles);
        }

        private static bool LooksLikeScheme(JObject scheme)
        {
            foreach (string key in PaletteKeys)
            {
                if (scheme[key] != null)
                {
                    return true;
                }
            }
            return scheme[ForegroundKey] != null || scheme[BackgroundKey] != null;
        }

        private static string SchemeName(JObject scheme, string suggestedName)
        {
            JToken nameToken = scheme["name"];
            if (nameToken != null && nameToken.Type == JTokenType.String
                && !string.IsNullOrWhiteSpace((string)nameToken))
            {
                return ((string)nameToken).Trim();
            }
            string fallback = suggestedName == null ? string.Empty : suggestedName.Trim();
            return fallback.Length == 0 ? "导入配色" : fallback;
        }

        // 同一文件内重名时加 " 2"、" 3"… 后缀（usedNames 为 null 时只做去空白/兜底）。
        private static string UniqueName(string name, HashSet<string> usedNames)
        {
            string trimmed = name == null ? string.Empty : name.Trim();
            if (trimmed.Length == 0)
            {
                trimmed = "导入配色";
            }
            if (usedNames == null)
            {
                return trimmed;
            }
            if (!usedNames.Contains(trimmed))
            {
                usedNames.Add(trimmed);
                return trimmed;
            }
            for (int n = 2; ; n++)
            {
                string candidate = trimmed + " " + n;
                if (!usedNames.Contains(candidate))
                {
                    usedNames.Add(candidate);
                    return candidate;
                }
            }
        }

        private static AppearanceProfile ParseScheme(JObject scheme, string name)
        {
            AppearanceProfile profile = ThemeImportResult.NewImportedProfile(name);
            foreach (string key in PaletteKeys)
            {
                profile.Palette.Add(NormalizeColor(key, scheme[key]));
            }
            profile.Foreground = NormalizeColor(ForegroundKey, scheme[ForegroundKey]);
            profile.Background = NormalizeColor(BackgroundKey, scheme[BackgroundKey]);
            profile.Cursor = NormalizeColor(CursorKey, scheme[CursorKey]);
            profile.Selection = NormalizeColor(SelectionKey, scheme[SelectionKey]);
            return profile;
        }

        private static string NormalizeColor(string key, JToken token)
        {
            if (token == null || token.Type != JTokenType.String)
            {
                throw new InvalidOperationException("缺少必需的颜色键 '" + key + "'");
            }
            string s = ((string)token).Trim();
            string body;
            if (s.Length == 4 && s[0] == '#')
            {
                body = new string(new char[]
                {
                    s[1], s[1], s[2], s[2], s[3], s[3]
                });
            }
            else if (s.Length == 7 && s[0] == '#')
            {
                body = s.Substring(1);
            }
            else
            {
                throw new InvalidOperationException("键 '" + key + "' 的颜色值非法（期望 #RRGGBB）：" + s);
            }
            foreach (char c in body)
            {
                if (!IsHexChar(c))
                {
                    throw new InvalidOperationException("键 '" + key + "' 的颜色值非法（期望 #RRGGBB）：" + s);
                }
            }
            return "#" + body.ToUpperInvariant();
        }

        private static bool IsHexChar(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }
    }
}
