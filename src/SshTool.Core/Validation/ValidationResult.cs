using System.Collections.Generic;

namespace SshTool.Core.Validation
{
    // D01：「字段名 → 错误资源键」字典；资源键见 Strings/*/Resources.resw（Validation_*）。
    public sealed class ValidationResult
    {
        private readonly Dictionary<string, string> _errors = new Dictionary<string, string>();

        public IReadOnlyDictionary<string, string> Errors
        {
            get { return _errors; }
        }

        public bool IsValid
        {
            get { return _errors.Count == 0; }
        }

        // 同一字段只保留第一条错误
        public void Add(string field, string errorKey)
        {
            if (!_errors.ContainsKey(field))
            {
                _errors[field] = errorKey;
            }
        }
    }

    public static class ValidationKeys
    {
        public const string Required = "Validation_Required";
        public const string NameTooLong = "Validation_NameTooLong";
        public const string HostTooLong = "Validation_HostTooLong";
        public const string PortRange = "Validation_PortRange";
        public const string KeepaliveRange = "Validation_KeepaliveRange";
        public const string ColorFormat = "Validation_ColorFormat";
        public const string ServerRequired = "Validation_ServerRequired";
        public const string DestHostRequired = "Validation_DestHostRequired";
        public const string DestServerRequired = "Validation_DestServerRequired";
        public const string SnippetContentRequired = "Validation_SnippetContentRequired";
    }
}
