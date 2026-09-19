using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using SshTool.Core.Models;

namespace SshTool.Core.Appearance
{
    // A04：.itermcolors（Apple plist XML）→ AppearanceProfile。
    // XML 解析用 System.Xml.Linq（netstandard1.4 自带，无需手写解析器）；
    // DOCTYPE 用 DtdProcessing.Ignore 跳过（不联网取 Apple DTD）。
    // 必需键：`Ansi 0 Color`…`Ansi 15 Color`、`Foreground/Background/Cursor/
    // Selection Color`，分量 0–1（<real>/<integer>）；缺键、分量越界、
    // 非法 XML 一律返回 Fail（不抛异常）。未知键（如 Color Space、Bold Color）
    // 忽略。
    public static class ItermcolorsParser
    {
        private const string ForegroundKey = "Foreground Color";
        private const string BackgroundKey = "Background Color";
        private const string CursorKey = "Cursor Color";
        private const string SelectionKey = "Selection Color";

        public static ThemeImportResult Parse(string text, string suggestedName)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return ThemeImportResult.Fail("文件为空：不是合法的 .itermcolors 文件");
            }
            try
            {
                XElement rootDict = LoadRootDict(text);
                Dictionary<string, XElement> map = DictToMap(rootDict, "<顶层>");
                var palette = new List<string>(16);
                for (int i = 0; i < 16; i++)
                {
                    string key = "Ansi " + i.ToString(CultureInfo.InvariantCulture) + " Color";
                    XElement colorDict = RequireDict(map, key);
                    palette.Add(ParseColor(colorDict, key));
                }
                string foreground = ParseColor(RequireDict(map, ForegroundKey), ForegroundKey);
                string background = ParseColor(RequireDict(map, BackgroundKey), BackgroundKey);
                string cursor = ParseColor(RequireDict(map, CursorKey), CursorKey);
                string selection = ParseColor(RequireDict(map, SelectionKey), SelectionKey);

                AppearanceProfile profile = ThemeImportResult.NewImportedProfile(suggestedName);
                profile.Palette.AddRange(palette);
                profile.Foreground = foreground;
                profile.Background = background;
                profile.Cursor = cursor;
                profile.Selection = selection;
                return ThemeImportResult.Succeed(new List<AppearanceProfile> { profile });
            }
            catch (Exception ex)
            {
                return ThemeImportResult.Fail("不是合法的 .itermcolors 文件：" + ex.Message);
            }
        }

        private static XElement LoadRootDict(string text)
        {
            var settings = new XmlReaderSettings
            {
                // netstandard1.4 的 XmlReaderSettings 没有 XmlResolver 属性；
                // DtdProcessing.Ignore 下本来也不会取外部 DTD，直接跳过即可。
                DtdProcessing = DtdProcessing.Ignore
            };
            using (var reader = XmlReader.Create(new StringReader(text), settings))
            {
                XDocument doc = XDocument.Load(reader);
                XElement plist = doc.Root;
                if (plist == null || !string.Equals(plist.Name.LocalName, "plist", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("顶层不是 <plist>");
                }
                foreach (XElement child in plist.Elements())
                {
                    if (string.Equals(child.Name.LocalName, "dict", StringComparison.Ordinal))
                    {
                        return child;
                    }
                }
                throw new InvalidOperationException("缺少顶层 <dict>");
            }
        }

        // plist dict = <key>取值交替；返回 键→值元素 映射。
        private static Dictionary<string, XElement> DictToMap(XElement dict, string where)
        {
            var map = new Dictionary<string, XElement>(StringComparer.Ordinal);
            List<XElement> children = new List<XElement>(dict.Elements());
            for (int i = 0; i < children.Count; i++)
            {
                XElement child = children[i];
                if (!string.Equals(child.Name.LocalName, "key", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(where + "的 dict 结构损坏：期望 <key>，实际 <"
                        + child.Name.LocalName + ">");
                }
                if (i + 1 >= children.Count)
                {
                    throw new InvalidOperationException(where + "的键 '" + child.Value + "' 缺少取值");
                }
                map[child.Value] = children[i + 1];
                i++;
            }
            return map;
        }

        private static XElement RequireDict(Dictionary<string, XElement> map, string key)
        {
            XElement value;
            if (!map.TryGetValue(key, out value))
            {
                throw new InvalidOperationException("缺少必需的颜色键 '" + key + "'");
            }
            if (!string.Equals(value.Name.LocalName, "dict", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("键 '" + key + "' 的取值不是颜色 dict");
            }
            return value;
        }

        private static string ParseColor(XElement colorDict, string key)
        {
            Dictionary<string, XElement> map = DictToMap(colorDict, "键 '" + key + "'");
            double r = Component(map, key, "Red Component");
            double g = Component(map, key, "Green Component");
            double b = Component(map, key, "Blue Component");
            return "#"
                + ToByte(r).ToString("X2", CultureInfo.InvariantCulture)
                + ToByte(g).ToString("X2", CultureInfo.InvariantCulture)
                + ToByte(b).ToString("X2", CultureInfo.InvariantCulture);
        }

        private static double Component(Dictionary<string, XElement> map, string colorKey, string component)
        {
            XElement element;
            if (!map.TryGetValue(component, out element))
            {
                throw new InvalidOperationException("键 '" + colorKey + "' 缺少 '" + component + "'");
            }
            string kind = element.Name.LocalName;
            if (!string.Equals(kind, "real", StringComparison.Ordinal)
                && !string.Equals(kind, "integer", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("键 '" + colorKey + "' 的 '" + component + "' 不是数值");
            }
            double value;
            if (!double.TryParse(element.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                throw new InvalidOperationException("键 '" + colorKey + "' 的 '" + component + "' 不是数值："
                    + element.Value.Trim());
            }
            if (value < 0 || value > 1)
            {
                throw new InvalidOperationException("键 '" + colorKey + "' 的 '" + component + "' 超出 0–1 范围："
                    + element.Value.Trim());
            }
            return value;
        }

        private static int ToByte(double component)
        {
            int v = (int)Math.Round(component * 255, MidpointRounding.AwayFromZero);
            if (v < 0)
            {
                return 0;
            }
            if (v > 255)
            {
                return 255;
            }
            return v;
        }
    }
}
