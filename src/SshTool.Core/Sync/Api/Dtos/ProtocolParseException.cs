using System;

namespace SshTool.Core.Sync.Api.Dtos
{
    // 响应 DTO 解析失败（必需键缺失/类型不符）。ApiClient 捕获后包装成
    // ApiError(protocol, RESPONSE_INVALID)。与 SyncDocumentInvalidException 不同：
    // API 响应允许未知键（服务端可能新增字段），只校验自己消费的键。
    public sealed class ProtocolParseException : Exception
    {
        public ProtocolParseException(string path, string reason)
            : base(path + ": " + reason)
        {
            Path = path;
            Reason = reason;
        }

        public string Path { get; private set; }
        public string Reason { get; private set; }
    }
}
