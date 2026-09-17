using System.Text.RegularExpressions;
using SshTool.Core.Models;

namespace SshTool.Core.Validation
{
    // HostGroup：name 1–255；color 必填 #RRGGBB
    public static class GroupValidator
    {
        private static readonly Regex ColorRegex = new Regex("^#[0-9a-fA-F]{6}$");

        public static ValidationResult Validate(HostGroup group)
        {
            var result = new ValidationResult();
            if (group == null)
            {
                result.Add("group", ValidationKeys.Required);
                return result;
            }

            if (string.IsNullOrWhiteSpace(group.Name))
            {
                result.Add("name", ValidationKeys.Required);
            }
            else if (group.Name.Length > 255)
            {
                result.Add("name", ValidationKeys.NameTooLong);
            }

            if (string.IsNullOrEmpty(group.Color) || !ColorRegex.IsMatch(group.Color))
            {
                result.Add("color", ValidationKeys.ColorFormat);
            }
            return result;
        }

        public static bool IsValidColor(string color)
        {
            return !string.IsNullOrEmpty(color) && ColorRegex.IsMatch(color);
        }
    }
}
