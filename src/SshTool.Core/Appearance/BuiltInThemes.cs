using System.Collections.Generic;
using SshTool.Core.Models;

namespace SshTool.Core.Appearance
{
    // A02：9 套内置主题。id 固定 builtin-<slug>；builtIn = true（A01 据此只读）。
    // 配色来源逐套注明；调色板顺序：0–7 常规（黑红绿黄蓝品青白），8–15 加亮。
    public static class BuiltInThemes
    {
        private static List<AppearanceProfile> _all;

        // 每次返回克隆：内置外观只读由服务层保证，对象本身也要防调用方改脏共享实例。
        public static IReadOnlyList<AppearanceProfile> All
        {
            get
            {
                if (_all == null)
                {
                    _all = Build();
                }
                var clones = new List<AppearanceProfile>(_all.Count);
                foreach (var t in _all)
                {
                    clones.Add(t.Clone());
                }
                return clones;
            }
        }

        private static List<AppearanceProfile> Build()
        {
            return new List<AppearanceProfile>
            {
                Make("harmony-dark", "Harmony Dark", "#E8EEFB", "#000000", "#E8EEFB", "#3A4A66",
                    // 来源：鸿蒙端 models.ets HARMONY_DARK_PALETTE 仓库不在本机，按 02-UI-DESIGN §3 深色 Token 族推导
                    "#12161F", "#F4605F", "#34C77B", "#F5A524",
                    "#4D8DFF", "#C37CE0", "#3EC6D9", "#E8EEFB",
                    "#6B7891", "#FF8584", "#5BD896", "#FFBB4D",
                    "#7CADFF", "#D6A0EC", "#6AD8E5", "#FFFFFF"),
                Make("harmony-light", "Harmony Light", "#1A2233", "#FFFFFF", "#1A2233", "#D9E4F6",
                    // 来源：02-UI-DESIGN §3 浅色 Token 族推导（同上）
                    "#1A2233", "#DC3F43", "#16A765", "#C9800C",
                    "#2F6FE4", "#9E55C9", "#1597A8", "#EEF1F7",
                    "#8A97AE", "#E86568", "#3FBE82", "#DD9E33",
                    "#5A8FEE", "#B57BD6", "#3AAEBD", "#FFFFFF"),
                Make("one-dark", "One Dark", "#ABB2BF", "#282C34", "#ABB2BF", "#3E4451",
                    // 来源：Atom One Dark
                    "#1E2127", "#E06C75", "#98C379", "#E5C07B",
                    "#61AFEF", "#C678DD", "#56B6C2", "#ABB2BF",
                    "#5C6370", "#E06C75", "#98C379", "#E5C07B",
                    "#61AFEF", "#C678DD", "#56B6C2", "#FFFFFF"),
                Make("dracula", "Dracula", "#F8F8F2", "#282A36", "#F8F8F2", "#44475A",
                    // 来源：draculatheme.com 官方规范
                    "#21222C", "#FF5555", "#50FA7B", "#F1FA8C",
                    "#BD93F9", "#FF79C6", "#8BE9FD", "#F8F8F2",
                    "#6272A4", "#FF6E6E", "#69FF94", "#FFFFA5",
                    "#D6ACFF", "#FF92DF", "#A4FFFF", "#FFFFFF"),
                Make("nord", "Nord", "#D8DEE9", "#2E3440", "#D8DEE9", "#434C5E",
                    // 来源：nordtheme.com 官方规范
                    "#3B4252", "#BF616A", "#A3BE8C", "#EBCB8B",
                    "#81A1C1", "#B48EAD", "#88C0D0", "#E5E9F0",
                    "#4C566A", "#BF616A", "#A3BE8C", "#EBCB8B",
                    "#81A1C1", "#B48EAD", "#8FBCBB", "#ECEFF4"),
                Make("solarized-dark", "Solarized Dark", "#839496", "#002B36", "#93A1A1", "#073642",
                    // 来源：ethanschoonover.com/solarized 官方规范
                    "#073642", "#DC322F", "#859900", "#B58900",
                    "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
                    "#002B36", "#CB4B16", "#586E75", "#657B83",
                    "#839496", "#6C71C4", "#93A1A1", "#FDF6E3"),
                Make("solarized-light", "Solarized Light", "#657B83", "#FDF6E3", "#586E75", "#EEE8D5",
                    // 来源：ethanschoonover.com/solarized 官方规范（与深色共用强调色）
                    "#073642", "#DC322F", "#859900", "#B58900",
                    "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
                    "#002B36", "#CB4B16", "#586E75", "#657B83",
                    "#839496", "#6C71C4", "#93A1A1", "#FDF6E3"),
                Make("tokyo-night", "Tokyo Night", "#C0CAF5", "#1A1B26", "#C0CAF5", "#283457",
                    // 来源：folke/tokyonight.nvim
                    "#15161E", "#F7768E", "#9ECE6A", "#E0AF68",
                    "#7AA2F7", "#BB9AF7", "#7DCFFF", "#A9B1D6",
                    "#414868", "#F7768E", "#9ECE6A", "#E0AF68",
                    "#7AA2F7", "#BB9AF7", "#7DCFFF", "#C0CAF5"),
                Make("github-light", "GitHub Light", "#24292F", "#FFFFFF", "#24292F", "#ADD6FF",
                    // 来源：GitHub Primer 浅色
                    "#24292F", "#CF222E", "#116329", "#4D2D00",
                    "#0969DA", "#8250DF", "#1B7C83", "#6E7781",
                    "#57606A", "#A40E26", "#1A7F37", "#9A6700",
                    "#218BFF", "#A475F9", "#3192AA", "#8C959F"),
            };
        }

        private static AppearanceProfile Make(
            string slug, string name,
            string foreground, string background, string cursor, string selection,
            params string[] palette)
        {
            return new AppearanceProfile
            {
                Id = "builtin-" + slug,
                Name = name,
                BuiltIn = true,
                FontFamily = "JetBrains Mono",   // §7.4 随包字体
                FontSize = 12,
                LineHeight = 1.2,
                FontWeightBold = false,
                BoldAsBright = true,
                CursorStyle = CursorStyle.Block,
                CursorBlink = true,
                Padding = 4,
                Palette = new List<string>(palette),
                Foreground = foreground,
                Background = background,
                Cursor = cursor,
                Selection = selection
            };
        }
    }
}
