using SshTool.Core.Models;

namespace SshTool.Core.Validation
{
    // Snippet：name 1–255 必填；content 必填（空片段无意义）
    public static class SnippetValidator
    {
        public static ValidationResult Validate(Snippet snippet)
        {
            var result = new ValidationResult();
            if (snippet == null)
            {
                result.Add("snippet", ValidationKeys.Required);
                return result;
            }

            if (string.IsNullOrWhiteSpace(snippet.Name))
            {
                result.Add("name", ValidationKeys.Required);
            }
            else if (snippet.Name.Length > 255)
            {
                result.Add("name", ValidationKeys.NameTooLong);
            }

            if (string.IsNullOrEmpty(snippet.Content))
            {
                result.Add("content", ValidationKeys.SnippetContentRequired);
            }
            return result;
        }
    }
}
