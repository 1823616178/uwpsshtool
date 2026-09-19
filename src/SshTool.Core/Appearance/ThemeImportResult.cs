using System.Collections.Generic;
using SshTool.Core.Common;
using SshTool.Core.Models;

namespace SshTool.Core.Appearance
{
    // A04：配色导入结果。非法文件不抛异常：一律返回 Ok=false + 可直接展示的
    // 中文 Error（页面弹框显示）；成功时 Profiles 为可直接 AddAsync 的外观
    //（Id 已分配、BuiltIn=false，非颜色字段取内置主题同款默认）。
    public sealed class ThemeImportResult
    {
        public bool Ok { get; set; }

        public List<AppearanceProfile> Profiles { get; private set; } = new List<AppearanceProfile>();

        public string Error { get; set; }

        public static ThemeImportResult Fail(string error)
        {
            return new ThemeImportResult { Ok = false, Error = error ?? "导入失败" };
        }

        public static ThemeImportResult Succeed(List<AppearanceProfile> profiles)
        {
            var result = new ThemeImportResult { Ok = true };
            if (profiles != null)
            {
                result.Profiles.AddRange(profiles);
            }
            return result;
        }

        // 导入外观的非颜色字段：与 BuiltInThemes.Make 同款默认
        //（随包 JetBrains Mono、字号 12、块光标闪烁、粗体用亮色）。
        public static AppearanceProfile NewImportedProfile(string name)
        {
            string trimmed = name == null ? string.Empty : name.Trim();
            return new AppearanceProfile
            {
                Id = IdGenerator.NewId(),
                Name = trimmed.Length == 0 ? "导入配色" : trimmed,
                BuiltIn = false,
                FontFamily = "JetBrains Mono",
                FontSize = 12,
                LineHeight = 1.2,
                FontWeightBold = false,
                BoldAsBright = true,
                CursorStyle = CursorStyle.Block,
                CursorBlink = true,
                Padding = 4,
                Palette = new List<string>(16),
                Foreground = "#E8EEFB",
                Background = "#000000",
                Cursor = "#E8EEFB",
                Selection = "#3A4A66"
            };
        }
    }
}
