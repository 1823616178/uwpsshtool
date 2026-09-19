using System;

namespace SshTool.Core.Sync
{
    // §5.2 下行两道保护的失败原因（与桌面端 sync-coordinator.ts 文案对齐，见 SyncLocalAdapter）。
    public enum SyncApplyFailure
    {
        HostFingerprintChanged,
        TunnelBusy
    }

    // ApplyDocumentAsync 在任一道保护触发时抛出：整份远端文档不应用，调用方（S12 协调器）进 error。
    public sealed class SyncApplyException : Exception
    {
        public SyncApplyException(SyncApplyFailure failure, string message)
            : base(message)
        {
            Failure = failure;
        }

        public SyncApplyFailure Failure { get; private set; }
    }
}
