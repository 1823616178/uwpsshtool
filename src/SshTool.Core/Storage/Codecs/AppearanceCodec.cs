using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SshTool.Core.Models;

namespace SshTool.Core.Storage.Codecs
{
    // §8.1 AppearanceProfile；cursorStyle ↔ block/bar/underline。
    public sealed class AppearanceCodec : IEntityCodec<AppearanceProfile>
    {
        private static readonly ISet<string> KnownKeys = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "id", "name", "builtIn", "fontFamily", "fontSize", "lineHeight", "fontWeightBold",
            "boldAsBright", "cursorStyle", "cursorBlink", "padding", "palette",
            "foreground", "background", "cursor", "selection"
        };

        public string GetId(AppearanceProfile item)
        {
            return item.Id;
        }

        public JObject Encode(AppearanceProfile item)
        {
            var o = new JObject();
            o.Set("id", item.Id);
            o.Set("name", item.Name);
            o.Set("builtIn", item.BuiltIn);
            o.Set("fontFamily", item.FontFamily);
            o.Set("fontSize", item.FontSize);
            o.Set("lineHeight", item.LineHeight);
            o.Set("fontWeightBold", item.FontWeightBold);
            o.Set("boldAsBright", item.BoldAsBright);
            o.Set("cursorStyle", CursorStyleToJson(item.CursorStyle));
            o.Set("cursorBlink", item.CursorBlink);
            o.Set("padding", item.Padding);
            o.Set("palette", item.Palette);
            o.Set("foreground", item.Foreground);
            o.Set("background", item.Background);
            o.Set("cursor", item.Cursor);
            o.Set("selection", item.Selection);
            o.MergeExtra(item.Extra);
            return o;
        }

        public AppearanceProfile Decode(JObject json)
        {
            var item = new AppearanceProfile();
            item.Id = json.GetString("id");
            item.Name = json.GetString("name");
            item.BuiltIn = json.GetBool("builtIn");
            item.FontFamily = json.GetString("fontFamily");
            item.FontSize = json.GetInt("fontSize");
            item.LineHeight = json.GetDouble("lineHeight");
            item.FontWeightBold = json.GetBool("fontWeightBold");
            item.BoldAsBright = json.GetBool("boldAsBright");
            item.CursorStyle = CursorStyleFromJson(json.GetString("cursorStyle"));
            item.CursorBlink = json.GetBool("cursorBlink");
            item.Padding = json.GetInt("padding");
            item.Palette = json.GetStringList("palette");
            item.Foreground = json.GetString("foreground");
            item.Background = json.GetString("background");
            item.Cursor = json.GetString("cursor");
            item.Selection = json.GetString("selection");
            item.Extra = json.ExtractExtra(KnownKeys);
            return item;
        }

        private static string CursorStyleToJson(CursorStyle value)
        {
            switch (value)
            {
                case CursorStyle.Block: return "block";
                case CursorStyle.Bar: return "bar";
                case CursorStyle.Underline: return "underline";
                default: throw new JsonException("未知 cursorStyle: " + value);
            }
        }

        private static CursorStyle CursorStyleFromJson(string value)
        {
            if (value == null)
            {
                return CursorStyle.Block;
            }
            switch (value)
            {
                case "block": return CursorStyle.Block;
                case "bar": return CursorStyle.Bar;
                case "underline": return CursorStyle.Underline;
                default: throw new JsonException("未知 cursorStyle: " + value);
            }
        }
    }
}
