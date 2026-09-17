using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SshTool.Core.Models
{
    // 01-DESIGN.md §8.1 AppearanceProfile 🏠
    public sealed class AppearanceProfile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool BuiltIn { get; set; }
        public string FontFamily { get; set; }
        public int FontSize { get; set; }            // 8–28
        public double LineHeight { get; set; }       // 1.0–1.6
        public bool FontWeightBold { get; set; }     // 用粗体字重
        public bool BoldAsBright { get; set; }
        public CursorStyle CursorStyle { get; set; }
        public bool CursorBlink { get; set; }
        public int Padding { get; set; }             // 0–16
        public List<string> Palette { get; set; } = new List<string>();  // 16×#RRGGBB
        public string Foreground { get; set; }
        public string Background { get; set; }
        public string Cursor { get; set; }
        public string Selection { get; set; }

        public JObject Extra { get; set; }

        public AppearanceProfile Clone()
        {
            return new AppearanceProfile
            {
                Id = Id,
                Name = Name,
                BuiltIn = BuiltIn,
                FontFamily = FontFamily,
                FontSize = FontSize,
                LineHeight = LineHeight,
                FontWeightBold = FontWeightBold,
                BoldAsBright = BoldAsBright,
                CursorStyle = CursorStyle,
                CursorBlink = CursorBlink,
                Padding = Padding,
                Palette = new List<string>(Palette),
                Foreground = Foreground,
                Background = Background,
                Cursor = Cursor,
                Selection = Selection,
                Extra = Extra == null ? null : (JObject)Extra.DeepClone()
            };
        }
    }
}
