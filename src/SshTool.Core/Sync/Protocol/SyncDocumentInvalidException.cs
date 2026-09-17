using System;

namespace SshTool.Core.Sync.Protocol
{
    // 03-SYNC-PROTOCOL.md §4.3：文档校验/解析失败时抛出，协调器进 error 且绝不上传覆盖。
    public sealed class SyncDocumentInvalidException : Exception
    {
        public SyncDocumentInvalidException(string path, string reason)
            : base((string.IsNullOrEmpty(path) ? "$" : path) + ": " + reason)
        {
            Path = string.IsNullOrEmpty(path) ? "$" : path;
            Reason = reason;
        }

        public string Path { get; private set; }
        public string Reason { get; private set; }
    }
}
